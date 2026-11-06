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
public sealed class WebSessionHub(WebHubServices services)
{
    private readonly ConcurrentDictionary<string, LogEntry> _logs = new();
    private readonly ConcurrentDictionary<WebClient, byte> _clients = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _idleSince = new();

    private sealed record LogEntry(SessionEventLog Log, IDisposable Observers, IDisposable Tools);

    private IReadOnlyList<string> _banners = services.Banners;

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

    /// <summary>Tells every tab the session list changed.</summary>
    internal void ListChanged()
    {
        var rows = Rows();
        foreach (var c in _clients.Keys) c.Send(new JsonObject { ["type"] = "sessions", ["rows"] = rows.DeepClone() });
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
            if (now - since >= TimeSpan.FromMinutes(services.IdleMinutes))
            {
                _idleSince.TryRemove(session.Id, out _);
                _ = UnloadAsync(session);
            }
        }
    }

    internal async Task<bool> UnloadAsync(Session session)
    {
        if (!await services.Opener.CloseAsync(session).ConfigureAwait(false)) return false;
        if (_logs.TryRemove(session.Id, out var entry))
        {
            entry.Observers.Dispose();
            entry.Tools.Dispose();
        }
        ListChanged();
        return true;
    }

    private void NoteDetached()
    {
        // A TAB LEAVING STARTS THE IDLE CLOCK of whatever it had open; CheckIdle measures from the first look after.
        CheckIdle();
    }

    /// <summary>
    /// The log a session's tabs follow, created on first open and kept while the session lives. <paramref name="seedAgent"/>
    /// names the saved transcript its past is read from.
    /// </summary>
    internal SessionEventLog LogFor(Session session, string? seedAgent)
    {
        return _logs.GetOrAdd(session.Id, _ =>
        {
            var log = new SessionEventLog(session.Id);
            if ((seedAgent ?? session.SessionId) is { } agent)
                log.Seed(new SessionFolder(services.Paths, agent).TranscriptPath);

            // THROUGH THE FAN-OUT, BESIDE WHATEVER ELSE SHOWS THE SESSION — the terminal's tab under /web — and kept
            // across re-wires, which replace only the port's own subscriptions.
            var observers = session.Observers?.Add(log) ?? Nothing.Instance;
            var tools = session.ToolObservers?.Add(log) ?? Nothing.Instance;
            return new LogEntry(log, observers, tools);
        }).Log;
    }

    /// <summary>The rows the session list shows: saved sessions under the roots, and every live one.</summary>
    internal JsonArray Rows()
    {
        var live = services.Manager.Sessions;
        var saved = services.Store.List(all: true);
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
                ["folder"] = s.WorkingDirectory,
                ["state"] = services.Prompts.IsWaiting(s.Id) ? "waiting" : s.IsBusy ? "working" : "idle",
                ["updatedAt"] = services.Clock.GetUtcNow().ToString("O"),
            });
        }
        foreach (var info in saved)
        {
            if (info.Superseded || shown.Contains(info.Uid) || info.WorkingDir is not { } dir || !InsideRoots(dir, services.Roots))
                continue;
            rows.Add(new JsonObject
            {
                ["id"] = info.Uid,
                ["agentId"] = info.Uid,
                ["title"] = info.Title ?? "",
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
    public static bool InsideRoots(string folder, IReadOnlyList<string> roots)
    {
        var real = RealPath(folder);
        if (real is null) return false;
        foreach (var root in roots)
        {
            if (RealPath(root) is not { } r) continue;
            var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
            if (real == r || real.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
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
