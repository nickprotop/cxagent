using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.Hosting;

namespace CxAgent.Web;

/// <summary>What the hub works with.</summary>
/// <param name="Manager">The process's sessions.</param>
/// <param name="Prompts">Where every question is raced between the terminal and the tabs.</param>
/// <param name="Store">The saved sessions, for the list and for resuming.</param>
/// <param name="Paths">Where the logs and locks are.</param>
/// <param name="Roots">The folders a browser may open sessions under, already expanded.</param>
/// <param name="Opener">How a new or resumed session comes alive, and how one ends.</param>
/// <param name="Clock">Time, for idle unloading.</param>
public sealed record WebHubServices(
    SessionManager Manager,
    PromptHub Prompts,
    FolderSessionStore Store,
    AppPaths Paths,
    IReadOnlyList<string> Roots,
    ISessionOpener Opener,
    TimeProvider Clock)
{
    /// <summary>Minutes a session nobody is looking at stays loaded; 0 keeps it. Only when the opener unloads at all.</summary>
    public int IdleMinutes { get; init; } = 30;

    /// <summary>
    /// Told the config a browser's save made live, for a host that keeps its own copy — the terminal, whose new tabs are
    /// built from it. Null under <c>cxagent web</c>, whose sessions are built from the manager's.
    /// </summary>
    public Action<Core.Llm.ResolvedConfig>? ConfigSaved { get; init; }

    /// <summary>Reconnects the MCP servers after a browser's MCP save; null where the host has none to reconnect.</summary>
    public Func<IReadOnlyDictionary<string, Core.Llm.McpServerConfig>, Task>? McpReload { get; init; }

    /// <summary>What a browser may do beyond the conversation — read when the server starts, never changed by a browser.</summary>
    public Core.Web.WebAccess Allow { get; init; } = Core.Web.WebAccess.Default;

    /// <summary>Lines every tab shows at the top — no login, plain HTTP beyond this machine.</summary>
    public IReadOnlyList<string> Banners { get; init; } = [];
}

/// <summary>
/// The browser side of every session: who has which open, the event log each tab follows, and the sessions a tab may
/// list, open, create and close.
///
/// <para>ONE PROCESS'S SESSIONS. The hub never reaches another process's session — a session held elsewhere is listed
/// as held and refused, by its lock.</para>
///
/// <para>THE ROOTS ARE A BOUNDARY OF THIS PAGE, NOT A SANDBOX. They keep a login from opening a session on any folder of
/// the disk; what an open session's agent may then touch is the permission policy's and the sandbox's business.</para>
/// </summary>
public sealed class WebSessionHub
{
    private readonly WebHubServices services;
    private readonly ConcurrentDictionary<string, LogEntry> _logs = new();
    private readonly ConcurrentDictionary<WebClient, byte> _clients = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _idleSince = new();
    private readonly ConcurrentDictionary<string, byte> _unloading = new();
    private readonly object _logGate = new();
    private readonly SemaphoreSlim _resuming = new(1, 1);
    private readonly ConcurrentDictionary<string, Action<Core.Agents.SpawnedChild>> _spawns = new();
    private readonly ConcurrentDictionary<string, Action<Core.Export.ExportSource>> _restores = new();
    private readonly ConcurrentDictionary<string, Action<SessionChangeKind>> _clears = new();
    private readonly ConcurrentDictionary<string, Action<ToolCallReport>> _calls = new();
    private readonly ConcurrentDictionary<string, IDisposable> _queues = new();
    private int _listPending;

    /// <summary>A hub over <paramref name="services"/>, told whenever the manager closes a session.</summary>
    public WebSessionHub(WebHubServices services)
    {
        this.services = services;
        _banners = services.Banners;
        services.Manager.SessionClosed += OnSessionClosed;
    }

    private sealed record LogEntry(SessionEventLog Log, IDisposable Observers, IDisposable Tools);

    internal sealed class Both(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
    }

    private IReadOnlyList<string> _banners;

    internal WebHubServices Services => services;

    /// <summary>What every tab shows at the top.</summary>
    internal IReadOnlyList<string> Banners => _banners;

    /// <summary>Sets the lines every tab shows at the top — known only once the server knows where it listens.</summary>
    public void SetBanners(IReadOnlyList<string> banners) => _banners = banners;

    /// <summary>Serves one tab until its socket closes. <paramref name="user"/> is who logged in, or null without login.</summary>
    public Task ServeAsync(WebSocket socket, string? user, CancellationToken ct) => ServeAsync(socket, user, null, ct);

    /// <summary>
    /// Serves one tab until its socket closes, remembering the login <paramref name="token"/> it came with so that
    /// logging out closes it — a session that survived its own logout would be a stolen one that never ends.
    /// </summary>
    public async Task ServeAsync(WebSocket socket, string? user, string? token, CancellationToken ct)
    {
        var client = new WebClient(this, socket, user) { Token = token };
        _clients[client] = 0;
        using var registration = services.Prompts.Add(client);
        try
        {
            await client.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
            NoteDetached();
        }
    }

    /// <summary>Sends <paramref name="message"/> to every tab — a banner, the server going away.</summary>
    public void Broadcast(JsonObject message)
    {
        foreach (var c in _clients.Keys) c.Send(message.DeepClone().AsObject());
    }

    /// <summary>Closes every tab's socket — the server is stopping or the terminal stopped sharing.</summary>
    public async Task DisconnectAllAsync(string reason)
    {
        Broadcast(new JsonObject { ["type"] = "banner", ["text"] = reason });
        foreach (var c in _clients.Keys) await c.CloseAsync().ConfigureAwait(false);
    }

    /// <summary>Closes every tab that came with <paramref name="token"/> — its login was just logged out.</summary>
    public async Task DisconnectTokenAsync(string token)
    {
        foreach (var c in _clients.Keys.Where(c => c.Token == token)) await c.CloseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Tells every tab the session list changed — once for a burst of changes. Building the list reads every saved
    /// session's header and probes its lock, which is too much to repeat per message per tab.
    /// </summary>
    internal void ListChanged()
    {
        if (Interlocked.Exchange(ref _listPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(250).ConfigureAwait(false);
            Volatile.Write(ref _listPending, 0);
            var rows = Rows();
            foreach (var c in _clients.Keys) c.Send(new JsonObject { ["type"] = "sessions", ["rows"] = rows.DeepClone() });
        });
    }

    /// <summary>
    /// Lets go of everything this hub attached to sessions — the server is stopping. A session the terminal keeps is
    /// left with no browser observer behind, so turning /web off and on does not stack them.
    /// </summary>
    public void DetachAll()
    {
        services.Manager.SessionClosed -= OnSessionClosed;
        foreach (var id in _logs.Keys) Drop(id);
        foreach (var (id, spawn) in _spawns)
            if (services.Manager.Sessions.FirstOrDefault(s => s.Id == id) is { } session) session.WorkerSpawned -= spawn;
        _spawns.Clear();
        foreach (var (id, restore) in _restores)
            if (services.Manager.Sessions.FirstOrDefault(s => s.Id == id) is { } session) session.Restored -= restore;
        _restores.Clear();
        foreach (var (id, clear) in _clears)
            if (services.Manager.Sessions.FirstOrDefault(s => s.Id == id) is { } session) session.Changed -= clear;
        _clears.Clear();
        foreach (var (id, call) in _calls)
            if (services.Manager.Sessions.FirstOrDefault(s => s.Id == id) is { } session) session.ToolCallFinished -= call;
        _calls.Clear();
        foreach (var queue in _queues.Values) queue.Dispose();
        _queues.Clear();
    }

    /// <summary>
    /// Sends every tab the side panel of each session it has open — each tab only when it changed. Called every
    /// second or so by the host; a test calls it directly.
    /// </summary>
    public void PushPanels()
    {
        var built = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
        foreach (var client in _clients.Keys)
            foreach (var id in client.OpenSessions)
            {
                if (!built.TryGetValue(id, out var panel)) built[id] = panel = PanelFor(id);
                if (panel is not null) client.SendPanel(id, panel);
            }
    }

    /// <summary>The panel for a session this hub has a log for, or null.</summary>
    internal JsonObject? PanelFor(string sessionId) =>
        _logs.TryGetValue(sessionId, out var entry) && services.Manager.Sessions.FirstOrDefault(s => s.Id == sessionId) is { } session
            ? WebPanel.Build(session, entry.Log, services.Manager)
            : null;

    /// <summary>The key a worker's own view goes by: its session and its agent id.</summary>
    internal static string WorkerKey(string sessionId, string agentId) => sessionId + "~" + agentId;

    /// <summary>
    /// A worker's own transcript, as its terminal tab shows it: a log fed by the worker's feed, which replays what it has
    /// done so far and then follows it live. Kept while its session lives.
    /// </summary>
    internal SessionEventLog? WorkerLog(Session session, string agentId)
    {
        var key = WorkerKey(session.Id, agentId);
        if (_logs.TryGetValue(key, out var existing)) return existing.Log;
        if (session.SubAgents.FindByAgentId(agentId) is not { } stored) return null;
        lock (_logGate)
        {
            if (_logs.TryGetValue(key, out existing)) return existing.Log;
            var log = new SessionEventLog(key);
            var feed = stored.Agent.Feed.Attach(new Core.Sessions.ChildView(log, log));
            _logs[key] = new LogEntry(log, feed, Nothing.Instance);
            return log;
        }
    }

    // A WORKER STARTING IS SAID IN ITS SESSION'S LOG, with what it is — so its card can say "planner · deepseek" and open
    // its own view.
    private void OnChildSpawned(Session session, Core.Agents.SpawnedChild child)
    {
        if (!_logs.TryGetValue(session.Id, out var entry)) return;
        var agentId = child.Child.Agent.Id;
        var stored = session.SubAgents.FindByAgentId(agentId);
        entry.Log.Worker(child.JobId, agentId, stored?.Name ?? agentId, stored?.TypeName, stored?.Description);
    }

    // A SESSION CLOSED ANYWHERE — /exit in the terminal, a browser, idle — leaves every tab that had it open, told.
    private void OnSessionClosed(Session session)
    {
        Drop(session.Id);
        foreach (var key in _logs.Keys.Where(k => k.StartsWith(session.Id + "~", StringComparison.Ordinal)).ToList()) Drop(key);
        if (_spawns.TryRemove(session.Id, out var spawn)) session.WorkerSpawned -= spawn;
        if (_restores.TryRemove(session.Id, out var restore)) session.Restored -= restore;
        if (_clears.TryRemove(session.Id, out var clear)) session.Changed -= clear;
        if (_calls.TryRemove(session.Id, out var call)) session.ToolCallFinished -= call;
        if (_queues.TryRemove(session.Id, out var queue)) queue.Dispose();
        foreach (var c in _clients.Keys) c.SessionClosed(session.Id);
        ListChanged();
    }

    private void Drop(string sessionId)
    {
        if (_logs.TryRemove(sessionId, out var entry))
        {
            entry.Observers.Dispose();
            entry.Tools.Dispose();
        }
    }

    /// <summary>
    /// Resumes a saved session — one at a time, re-checked inside the gate, and the conversation it continues retired
    /// at once, as the terminal's resume does. Two tabs tapping the same row then make one live session, not two
    /// writers of one conversation. Null with <paramref name="refusal"/> set when it may not be resumed.
    /// </summary>
    internal Session? Resume(string agentId, out string? refusal)
    {
        _resuming.Wait();
        try
        {
            if (services.Manager.Sessions.FirstOrDefault(s => s.SessionId == agentId) is { } already)
            {
                refusal = null;
                return already;
            }
            var snapshot = services.Store.LoadById(agentId);
            if (snapshot is null) { refusal = "gone"; return null; }
            if (snapshot.WorkingDir is not { } dir || !InsideRoots(dir, services.Roots))
            {
                refusal = "That session is outside the folders this server opens.";
                return null;
            }
            if (services.Store.HolderOf(agentId) is { } held) { refusal = $"That session is {SessionLock.HeldMessage(held)}."; return null; }
            if (services.Store.List(all: true).FirstOrDefault(i => i.Uid == agentId) is { Superseded: true })
            {
                refusal = "That conversation was already continued in a newer session.";
                return null;
            }

            var session = services.Opener.Resume(snapshot);
            services.Store.MarkSuperseded(agentId);
            refusal = null;
            return session;
        }
        finally
        {
            _resuming.Release();
        }
    }

    /// <summary>
    /// Unloads sessions nobody is looking at: no tab has them open, no turn is running, nothing waits on a person, for
    /// <see cref="WebHubServices.IdleMinutes"/>. Called every minute by the host; a test calls it directly.
    /// </summary>
    public void CheckIdle()
    {
        if (!services.Opener.UnloadsIdle || services.IdleMinutes <= 0) return;
        var now = services.Clock.GetUtcNow();
        foreach (var session in services.Manager.Sessions)
        {
            var watched = _clients.Keys.Any(c => c.HasOpen(session.Id));
            if (watched || session.IsBusy || services.Prompts.IsWaiting(session.Id))
            {
                _idleSince.TryRemove(session.Id, out _);
                continue;
            }
            var since = _idleSince.GetOrAdd(session.Id, now);
            // ONCE: the minute timer and a tab leaving can both get here; only the one that takes the mark unloads.
            if (now - since >= TimeSpan.FromMinutes(services.IdleMinutes) && _idleSince.TryRemove(session.Id, out _))
                _ = UnloadAsync(session);
        }
    }

    internal async Task<bool> UnloadAsync(Session session)
    {
        if (!_unloading.TryAdd(session.Id, 0)) return false;
        try
        {
            // THE MANAGER'S SessionClosed drops the log and tells the tabs.
            return await services.Opener.CloseAsync(session).ConfigureAwait(false);
        }
        finally
        {
            _unloading.TryRemove(session.Id, out _);
        }
    }

    private void NoteDetached()
    {
        // A TAB LEAVING STARTS THE IDLE CLOCK of whatever it had open; CheckIdle measures from the first look after.
        CheckIdle();
    }

    /// <summary>
    /// The log a session's tabs follow, created on first open and kept while the session lives, its past replayed from
    /// the session's own history — which, for a resumed one, names the conversation it continues.
    /// </summary>
    internal SessionEventLog LogFor(Session session)
    {
        if (_logs.TryGetValue(session.Id, out var existing)) return existing.Log;

        // BUILT UNDER A LOCK, so two tabs opening at once do not each subscribe a log and leave one attached forever.
        lock (_logGate)
        {
            if (_logs.TryGetValue(session.Id, out existing)) return existing.Log;
            var entry = Build();
            _logs[session.Id] = entry;
            return entry.Log;
        }

        LogEntry Build()
        {
            var log = new SessionEventLog(session.Id);
            if (session.SessionId is { } agent)
                if (Core.Export.ExportSource.Load(services.Paths, agent) is { } past) log.Replay(past);

            // THROUGH THE FAN-OUT, BESIDE WHATEVER ELSE SHOWS THE SESSION — the terminal's tab under /web — and kept
            // across re-wires, which replace only the port's own subscriptions.
            var observers = session.Observers?.Add(log) ?? Nothing.Instance;
            var tools = session.ToolObservers?.Add(log) ?? Nothing.Instance;

            // WORKERS ANNOUNCE THEMSELVES in this log as they start.
            Action<Core.Agents.SpawnedChild> spawned = child => OnChildSpawned(session, child);
            if (_spawns.TryAdd(session.Id, spawned)) session.WorkerSpawned += spawned;

            // A LATER RESUME — /sessions resume typed in an open tab — shows the conversation it brought back, below
            // what the tab already has, as the terminal does.
            Action<Core.Export.ExportSource> restored = past => log.Replay(past);
            if (_restores.TryAdd(session.Id, restored)) session.Restored += restored;
            // /clear EMPTIES THE TRANSCRIPT HERE AS IN THE TERMINAL — the session announces the clear before it says
            // "Conversation cleared.", so that line lands after the wipe, not under it.
            Action<SessionChangeKind> cleared = kind => { if (kind is SessionChangeKind.ContextCleared) log.Cleared(); };
            if (_clears.TryAdd(session.Id, cleared)) session.Changed += cleared;
            // A WORKER'S CALLS, AS THEY FINISH — background ones included, whose own tool rows never reach this log — so
            // its card shows the call table the terminal's expanded worker row does.
            Action<ToolCallReport> called = log.WorkerCall;
            if (_calls.TryAdd(session.Id, called)) session.ToolCallFinished += called;
            // WHAT WAITS FOR THE TURN, as the terminal's queued block shows it: the whole queue on each line typed,
            // and empty once the turn took it or it went back to a composer.
            if (!_queues.ContainsKey(session.Id))
            {
                Action<string, string> pending = (whole, _) => log.Queued(whole);
                Action<string> drained = _ => log.Queued("");
                Action<string, string?> cancelled = (_, _) => log.Queued("");
                session.Pending += pending;
                session.Drained += drained;
                session.Cancelled += cancelled;
                if (!_queues.TryAdd(session.Id, new Unsubscribe(() =>
                {
                    session.Pending -= pending;
                    session.Drained -= drained;
                    session.Cancelled -= cancelled;
                })))
                {
                    session.Pending -= pending;
                    session.Drained -= drained;
                    session.Cancelled -= cancelled;
                }
            }
            // ITS ASIDES TOO — /btw answered beside the conversation — as a watcher beside the terminal's own.
            var asides = session.WatchAsides(log);
            return new LogEntry(log, new Both(observers, asides), tools);
        }
    }

    /// <summary>The rows the session list shows: saved sessions under the roots, and every live one.</summary>
    internal JsonArray Rows()
    {
        var live = services.Manager.Sessions;
        // /sessions' OWN RULE — Core.Commands.SessionsCommand.Offered: every saved session but one a resume replaced.
        var saved = Core.Commands.SessionsCommand.Offered(services.Store.List(all: true));
        var savedById = saved.ToDictionary(i => i.Uid, StringComparer.Ordinal);
        var titles = saved.ToDictionary(i => i.Uid, i => i.Title, StringComparer.Ordinal);
        var rows = new JsonArray();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in live)
        {
            if (!InsideRoots(s.WorkingDirectory, services.Roots)) continue;
            if (s.SessionId is { } agent) shown.Add(agent);
            rows.Add(new JsonObject
            {
                ["id"] = s.Id,
                ["agentId"] = s.SessionId,
                ["title"] = s.SessionId is { } id && titles.GetValueOrDefault(id) is { } t ? t : "",
                // THE ID AND TOKENS /sessions SHOWS, so a row here can be told apart and named there.
                ["short"] = s.SessionId is { } sid ? Core.Commands.SessionsCommand.Short(sid) : null,
                ["tokens"] = s.SessionId is { } tid && savedById.TryGetValue(tid, out var had) ? Core.Commands.SessionsCommand.TokensOf(had) : null,
                ["folder"] = s.WorkingDirectory,
                ["state"] = services.Prompts.IsWaiting(s.Id) ? "waiting" : s.IsBusy ? "working" : "idle",
                ["updatedAt"] = services.Clock.GetUtcNow().ToString("O"),
            });
        }
        foreach (var info in saved)
        {
            if (shown.Contains(info.Uid) || info.WorkingDir is not { } dir || !InsideRoots(dir, services.Roots))
                continue;
            rows.Add(new JsonObject
            {
                ["id"] = info.Uid,
                ["agentId"] = info.Uid,
                ["title"] = info.Title ?? "",
                ["short"] = Core.Commands.SessionsCommand.Short(info.Uid),
                ["tokens"] = Core.Commands.SessionsCommand.TokensOf(info),
                ["folder"] = dir,
                ["state"] = info.Holder is null ? "recent" : "held",
                ["heldBy"] = info.Holder is { } h ? SessionLock.HeldMessage(h) : null,
                ["updatedAt"] = info.UpdatedAt.ToString("O"),
            });
        }
        return rows;
    }

    /// <summary>
    /// Whether <paramref name="folder"/> lies inside one of <paramref name="roots"/> once every link on its way is
    /// followed — so a link under a root that points elsewhere does not count as under it.
    /// </summary>
    public static bool InsideRoots(string folder, IReadOnlyList<string> roots) => Resolved(folder, roots) is not null;

    /// <summary>
    /// <paramref name="folder"/> with every link followed, when that lies inside one of <paramref name="roots"/>; else
    /// null. Opening the resolved path, not the one asked for, means a link swapped in after the check moves nothing.
    /// </summary>
    public static string? Resolved(string folder, IReadOnlyList<string> roots)
    {
        var real = RealPath(folder);
        if (real is null) return null;
        foreach (var root in roots)
        {
            if (RealPath(root) is not { } r) continue;
            var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
            if (real == r || real.StartsWith(prefix, StringComparison.Ordinal)) return real;
        }
        return null;
    }

    /// <summary>The configured roots with <c>~</c> expanded on this machine; empty means the folder the server started in.</summary>
    public static IReadOnlyList<string> ExpandRoots(IReadOnlyList<string> configured, string launchFolder)
    {
        if (configured.Count == 0) return [Path.GetFullPath(launchFolder)];
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [.. configured.Select(r => Path.GetFullPath(r == "~" ? home : r.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, r[2..]) : r))];
    }

    // EVERY COMPONENT'S LINK FOLLOWED, not just the last: a link halfway down the path moves everything after it.
    private static string? RealPath(string path, int depth = 0)
    {
        if (depth > 32) return null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        var root = Path.GetPathRoot(full) ?? "/";
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null) return null;
                var resolved = RealPath(target.FullName, depth + 1);
                if (resolved is null) return null;
                next = resolved;
            }
            current = next;
        }
        return current.Length > root.Length ? current.TrimEnd(Path.DirectorySeparatorChar) : current;
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}

/// <summary>Runs an unsubscribe once, when disposed.</summary>
internal sealed class Unsubscribe(Action undo) : IDisposable
{
    private Action? _undo = undo;
    public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
}
