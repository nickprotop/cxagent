using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CxAgent.Core.Agents;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.Hosting;

namespace CxAgent.Web;

/// <summary>
/// One browser tab: its socket, the sessions it has open, and the prompts it is showing.
///
/// <para>EVERY MESSAGE IN IS DATA FROM A BROWSER — checked against the roots, the allow-list and the prompt it
/// answers, never trusted for what it claims.</para>
///
/// <para>A SLOW TAB IS DROPPED, NOT WAITED FOR. Its outgoing queue is bounded; a phone that stops reading would
/// otherwise hold every event of every session it has open in memory. When the queue fills, the socket closes and the
/// tab reconnects with "since", which replays what it missed.</para>
/// </summary>
internal sealed class WebClient : IPromptResponder, IDisposable
{
    // ROOM FOR A PASTED SCREENSHOT OR TWO. Images arrive as base64 inside the message that sends them; a 4K screenshot
    // as PNG is several megabytes before it is shrunk, and the shrinking is done here, where the terminal does it.
    private const int MaxIncoming = 24 * 1024 * 1024;
    private const int MaxImages = 8;
    // ROOM FOR A WHOLE LOG'S REPLAY AND THEN SOME: a tab opening a long session is sent up to the log's 20,000 events at
    // once, and a queue smaller than that drops the tab mid-replay — again on every reconnect.
    private const int MaxQueued = 50_000;

    private readonly WebSessionHub _hub;
    private readonly WebSocket _socket;
    private readonly string _who;
    private readonly Channel<string> _out = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueued)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });
    private readonly ConcurrentDictionary<string, IDisposable> _open = new();
    private readonly ConcurrentDictionary<string, PendingPrompt> _shown = new();
    private readonly CancellationTokenSource _gone = new();
    private Task _sending = Task.CompletedTask;

    public WebClient(WebSessionHub hub, WebSocket socket, string? user)
    {
        _hub = hub;
        _socket = socket;
        _who = user ?? "web";
    }

    private WebHubServices S => _hub.Services;

    /// <summary>The login this tab came with, or null without one.</summary>
    public string? Token { get; init; }

    public bool HasOpen(string sessionId) => _open.ContainsKey(sessionId);

    private readonly ConcurrentDictionary<string, string> _panels = new();

    // THE FILES THIS TAB HAS OPEN IN ITS EDITOR.
    private WebFiles? _filesOrNull;
    private WebFiles _files => _filesOrNull ??= new WebFiles(Send);

    /// <summary>The sessions this tab has open — not the workers' own views, which have no panel of their own.</summary>
    public IEnumerable<string> OpenSessions => _open.Keys.Where(k => !k.Contains('~') && !k.Contains('#') && !k.Contains('@') && !k.Contains('$'));

    /// <summary>Sends a session's panel, when it differs from the last one this tab was sent.</summary>
    public void SendPanel(string sessionId, JsonObject panel)
    {
        var text = panel.ToJsonString();
        if (_panels.TryGetValue(sessionId, out var last) && last == text) return;
        _panels[sessionId] = text;
        Send(panel.DeepClone().AsObject());
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _gone.Token);
        var sending = _sending = SendLoopAsync(linked.Token);
        Send(new JsonObject
        {
            ["type"] = "hello",
            ["user"] = _who,
            ["login"] = Token is not null,
            ["banners"] = new JsonArray([.. _hub.Banners.Select(b => (JsonNode)b)]),
            ["commands"] = new JsonArray([.. WebCommands.AllowedWith(S.Allow).Select(c => (JsonNode)c)]),

            // THE SWITCHES, FOR THE PAGE TO SHOW. Read-only there: only the terminal's F8 changes them.
            ["access"] = new JsonObject
            {
                ["shell"] = S.Allow.Shell, ["config"] = S.Allow.Config,
                ["plugins"] = S.Allow.Plugins, ["openAnywhere"] = S.Allow.OpenAnywhere,
                ["roots"] = new JsonArray([.. S.Roots.Select(r => (JsonNode)r)]),
            },
        });
        Send(new JsonObject { ["type"] = "sessions", ["rows"] = _hub.Rows() });
        try
        {
            while (_socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                if (await ReceiveAsync(linked.Token).ConfigureAwait(false) is not { } text) break;
                if (ReferenceEquals(text, TooLarge))
                {
                    Error($"That message was larger than {MaxIncoming / (1024 * 1024)} MB and was not sent — fewer or smaller images.");
                    continue;
                }
                JsonObject? message;
                try { message = JsonNode.Parse(text) as JsonObject; }
                catch (JsonException) { message = null; }
                if (message is null) { Error("Not a message."); continue; }
                try
                {
                    await HandleAsync(message).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Error(e.Message);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
        finally
        {
            _out.Writer.TryComplete();
            Gone();
            try { await sending.ConfigureAwait(false); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Ends this tab: what is already queued — the banner saying why — is sent first, briefly bounded, and then the
    /// socket is closed properly so the page knows it was told rather than dropped.
    /// </summary>
    public async Task CloseAsync()
    {
        _out.Writer.TryComplete();
        try { await _sending.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception) { /* a tab that will not take its last messages is closed anyway */ }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
        Gone();
        try { _socket.Abort(); } catch (Exception) { }
    }

    // NEVER DISPOSED, AND NEVER THROWS: a prompt resolving or a broadcast can reach a tab while it is going away.
    private void Gone()
    {
        try { _gone.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>A session this tab had open was closed — by anyone.</summary>
    public void SessionClosed(string sessionId)
    {
        if (!_open.ContainsKey(sessionId)) return;
        Leave(sessionId);
        Send(new JsonObject { ["type"] = "closed", ["sessionId"] = sessionId });
    }

    public void Send(JsonObject message)
    {
        // FULL MEANS THE TAB STOPPED READING: let it go, and it comes back with "since".
        if (!_out.Writer.TryWrite(message.ToJsonString())) Gone();
    }

    public void Dispose()
    {
        _filesOrNull?.Dispose();
        foreach (var key in _shells.Keys) if (_shells.TryRemove(key, out var shell)) shell.Dispose();
        foreach (var sub in _open.Values) sub.Dispose();
        _open.Clear();
    }

    // ---- prompts -------------------------------------------------------------------------------------------------

    public bool Serves(string sessionId) => _open.ContainsKey(sessionId);

    public void Show(PendingPrompt prompt, CancellationToken resolved)
    {
        _shown[prompt.Id] = prompt;
        Send(WebProtocol.Prompt(prompt));
    }

    public void Resolved(string promptId, Resolution resolution)
    {
        if (!_shown.TryRemove(promptId, out _)) return;
        Send(new JsonObject { ["type"] = "promptResolved", ["promptId"] = promptId, ["by"] = resolution.AnsweredBy });
    }

    // ---- messages ------------------------------------------------------------------------------------------------

    private async Task HandleAsync(JsonObject m)
    {
        switch (Str(m, "type"))
        {
            case "list":
                Send(new JsonObject { ["type"] = "sessions", ["rows"] = _hub.Rows() });
                break;
            case "folders":
                Folders(Str(m, "under"));
                break;
            case "open":
                Open(Str(m, "id"), Long(m, "since") ?? 0);
                break;
            case "create":
                Create(Str(m, "folder"));
                break;
            case "say":
                Say(Str(m, "sessionId"), Str(m, "text"), m["images"] as JsonArray);
                break;
            case "complete":
                if (LiveSession(Str(m, "sessionId")) is { } typing)
                    Complete(typing, Str(m, "text") ?? "", m["caret"]?.GetValue<int>() ?? 0, m["n"]?.GetValue<int>() ?? 0);
                break;
            case "cycleMode":
                // SHIFT+TAB, AS IN THE TERMINAL — Session.CycleEditMode, the one rule both keys run. The panel is sent at
                // once so the picker under the message box shows the new mode without waiting for the next push.
                if (LiveSession(Str(m, "sessionId")) is { } cycling)
                {
                    cycling.CycleEditMode();
                    if (_hub.PanelFor(cycling.Id) is { } panel) SendPanel(cycling.Id, panel);
                }
                break;
            case "leaveAside":
                LeaveAside(Str(m, "sessionId"));
                break;
            case "export":
                Export(Str(m, "sessionId"), m["full"] is JsonValue f && f.TryGetValue<bool>(out var full) && full);
                break;
            case "answer":
                Answer(m);
                break;
            case "stop":
                Live(Str(m, "sessionId"))?.CancelTurn();
                break;
            case "close":
            {
                // BESIDE THE LOOP TOO: a close waits up to ten seconds for a turn to finish.
                var (sid, confirm) = (Str(m, "sessionId"), m["confirm"]?.GetValue<bool>() == true);
                _ = Beside(() => CloseSessionAsync(sid, confirm));
                break;
            }
            case "leave":
                Leave(Str(m, "sessionId"));
                break;
            case "openWorker":
                OpenWorker(Str(m, "sessionId"), Str(m, "agentId"), Long(m, "since") ?? 0);
                break;
            case "workerAction":
                WorkerAction(Str(m, "sessionId"), Str(m, "agentId"), Str(m, "action"));
                break;
            case "stopCommand":
                StopCommand(Str(m, "sessionId"), Long(m, "pid"));
                break;
            case "plugins":
            {
                // BESIDE THE MESSAGE LOOP: reading the catalogs waits on the network, and the loop is what answers prompts.
                var (sid, refresh) = (Str(m, "sessionId"), m["refresh"] is JsonValue rv && rv.TryGetValue<bool>(out var r) && r);
                _ = Beside(() => PluginsAsync(sid, refresh));
                break;
            }
            case "pluginAction":
                await PluginActionAsync(Str(m, "sessionId"), Str(m, "name"), Str(m, "action"), Str(m, "folder"), m["values"] as JsonObject).ConfigureAwait(false);
                break;
            case "configForm":
                _configFor = Str(m, "sessionId") ?? _configFor;
                ConfigForm(Str(m, "tab") ?? "general", Str(m, "item"));
                break;
            case "configFormSave":
                _configFor = Str(m, "sessionId") ?? _configFor;
                ConfigFormSave(Str(m, "tab") ?? "", m["values"] as JsonObject ?? [], Str(m, "choice"), Str(m, "item"));
                break;
            case "configFormAction":
                _configFor = Str(m, "sessionId") ?? _configFor;
                ConfigFormAction(Str(m, "tab") ?? "", Str(m, "item"), Str(m, "action") ?? "",
                    m["confirmed"] is JsonValue cv && cv.TryGetValue<bool>(out var confirmed) && confirmed);
                break;
            case "getConfig":
                GetConfig();
                break;
            case "saveConfig":
                SaveConfig(Str(m, "text") ?? "", Str(m, "choice"));
                break;
            case "openShell":
                if (LiveSession(Str(m, "sessionId")) is { } shellFor) OpenShell(shellFor);
                else Error("No such open session.");
                break;
            case "shellInput":
                if (_shells.TryGetValue(Str(m, "sessionId") ?? "", out var typedInto) && Str(m, "data") is { } typed)
                    typedInto.Pty.Write(Encoding.UTF8.GetBytes(typed));
                break;
            case "shellResize":
                if (_shells.TryGetValue(Str(m, "sessionId") ?? "", out var resized))
                    resized.Pty.Resize((int)(Long(m, "rows") ?? 24), (int)(Long(m, "cols") ?? 80));
                break;
            case "shellReport":
                ShellReport(Str(m, "sessionId"), m["lines"] as JsonArray, (int?)Long(m, "exitCode"));
                break;
            case "memoryOpen":
                if (LiveSession(Str(m, "sessionId")) is { } reading) OpenMemory(reading, Str(m, "name") ?? "");
                else Error("No such open session.");
                break;
            case "memoryForget":
                if (LiveSession(Str(m, "sessionId")) is { } forgetting)
                    ForgetMemory(forgetting, Str(m, "name") ?? "", m["confirm"]?.GetValue<bool>() == true);
                else Error("No such open session.");
                break;
            case "openFile":
                if (LiveSession(Str(m, "sessionId")) is { } opening) OpenFile(opening, Str(m, "path") ?? "");
                else Error("No such open session.");
                break;
            case "saveFile":
                await _files.SaveAsync(Str(m, "sessionId") ?? "", Str(m, "text") ?? "",
                    m["force"] is JsonValue fv && fv.TryGetValue<bool>(out var force) && force).ConfigureAwait(false);
                break;
            case "reloadFile":
                _files.Reload(Str(m, "sessionId") ?? "");
                break;
            case "openCommand":
                OpenCommand(Str(m, "sessionId"), Long(m, "pid"));
                break;
            case "copyCommand":
                CopyCommand(Str(m, "sessionId"), Long(m, "pid"));
                break;
            case "setSandbox":
                SetSandbox(Str(m, "sessionId"), m["enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) ? on : null);
                break;
            default:
                Error("Unknown message.");
                break;
        }
    }

    private void Folders(string? under)
    {
        if (under is null)
        {
            Send(new JsonObject { ["type"] = "folders", ["folders"] = new JsonArray([.. S.Roots.Select(r => (JsonNode)r)]) });
            return;
        }
        var full = Path.GetFullPath(under);
        if (!WebSessionHub.InsideRoots(full, S.Roots) || !Directory.Exists(full))
        {
            Error("That folder is outside the folders this server opens.");
            return;
        }
        var children = Directory.EnumerateDirectories(full)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(d => (JsonNode)d);
        Send(new JsonObject { ["type"] = "folders", ["under"] = full, ["folders"] = new JsonArray([.. children]) });
    }

    private void Create(string? folder)
    {
        if (folder is null) { Error("Which folder?"); return; }
        var full = Path.GetFullPath(folder);
        if (!Directory.Exists(full)) { Error("No such folder."); return; }
        if (WebSessionHub.Resolved(full, S.Roots) is not { } real) { Error("That folder is outside the folders this server opens."); return; }
        var session = S.Opener.Create(real);
        Attach(session, 0);
        _hub.ListChanged();
    }

    private void Open(string? id, long since)
    {
        if (id is null) { Error("Which session?"); return; }
        if (S.Manager.Sessions.FirstOrDefault(s => s.Id == id || s.SessionId == id) is { } live)
        {
            if (!WebSessionHub.InsideRoots(live.WorkingDirectory, S.Roots)) { Error("That session is outside the folders this server opens."); return; }
            Attach(live, since);
            return;
        }

        var session = _hub.Resume(id, out var refusal);
        if (session is null)
        {
            // GONE — unloaded, closed, or from a server since restarted: the page drops its view of it.
            if (refusal == "gone") Send(new JsonObject { ["type"] = "gone", ["id"] = id });
            else Error(refusal ?? "That session could not be opened.");
            return;
        }
        Attach(session, 0);
        _hub.ListChanged();
    }

    private void Attach(Session session, long since)
    {
        var log = _hub.LogFor(session);
        if (_open.TryRemove(session.Id, out var previous)) previous.Dispose();
        Send(WebProtocol.Opened(session));

        // THE REPLAY IS QUEUED UNDER THE LOG'S LOCK, before the first live event can be.
        log.Subscribe(since, Send, replay =>
        {
            if (replay.Reset) Send(new JsonObject { ["type"] = "reset", ["sessionId"] = session.Id });
            foreach (var e in replay.Events) Send(e.DeepClone().AsObject());
        }, out var handle);
        _open[session.Id] = handle;

        // WHAT IS ALREADY WAITING — a prompt raised before this tab arrived is shown to it now.
        foreach (var prompt in S.Prompts.ShowableFor(session.Id)) Show(prompt, CancellationToken.None);

        // AND ITS PANEL AT ONCE, not at the next tick.
        _panels.TryRemove(session.Id, out _);
        if (_hub.PanelFor(session.Id) is { } panel) SendPanel(session.Id, panel);
    }

    // A WORKER'S OWN VIEW: its transcript from the start, then live — the terminal's agent tab, in a browser tab.
    private void OpenWorker(string? sessionId, string? agentId, long since)
    {
        if (LiveSession(sessionId) is not { } session || agentId is null) { Error("Open the worker's session first."); return; }
        if (_hub.WorkerLog(session, agentId) is not { } log || session.SubAgents.FindByAgentId(agentId) is not { } stored)
        {
            Error("That worker is no longer there.");
            return;
        }
        var key = WebSessionHub.WorkerKey(session.Id, agentId);
        if (_open.TryRemove(key, out var previous)) previous.Dispose();
        Send(new JsonObject
        {
            ["type"] = "openedWorker",
            ["sessionId"] = key,
            ["parent"] = session.Id,
            ["agentId"] = agentId,
            ["name"] = stored.Name,
            ["workerType"] = stored.TypeName,
            ["task"] = stored.Description,
            ["model"] = stored.Agent.ModelId,
            ["folder"] = stored.Agent.Root ?? session.WorkingDirectory,
        });
        log.Subscribe(since, Send, replay =>
        {
            if (replay.Reset) Send(new JsonObject { ["type"] = "reset", ["sessionId"] = key });
            foreach (var e in replay.Events) Send(e.DeepClone().AsObject());
        }, out var handle);
        _open[key] = handle;
    }

    private void WorkerAction(string? sessionId, string? agentId, string? action)
    {
        if (LiveSession(sessionId) is not { } session || agentId is null
            || session.SubAgents.FindByAgentId(agentId) is not { } stored) { Error("That worker is no longer there."); return; }
        switch (action)
        {
            case "stop":
                _ = Task.Run(() => session.StopAgent(stored.Name));
                break;
            case "background":
                if (!session.MoveToBackground(stored.Name)) Notice(session.Id, $"{stored.Name} could not move to the background.", "Warning");
                break;
            default:
                Error("Unknown worker action.");
                break;
        }
    }

    // A BACKGROUND COMMAND THIS SESSION STARTED, stopped as F7 stops it.
    private void StopCommand(string? sessionId, long? pid)
    {
        if (LiveSession(sessionId) is not { } session || pid is null) { Error("No such command."); return; }
        var entry = Core.Jobs.BackgroundJobBoard.Default.For(session.Owns).FirstOrDefault(e => e.Job.Pid == pid);
        if (entry is null || entry.Finished) { Error("That command is not running."); return; }
        _ = Task.Run(entry.Process.Kill);
    }

    // run_shell's own limit on what it hands a model, and the terminal tab's Copy uses it for the same reason.
    private const int CopyLimit = 8192;

    private Core.Jobs.BackgroundJobEntry? CommandOf(Session session, long? pid) =>
        pid is null ? null : Core.Jobs.BackgroundJobBoard.Default.For(session.Owns).FirstOrDefault(e => e.Job.Pid == pid);

    /// <summary>
    /// A background command's tab, as F7's Enter opens one in the terminal: what it has written so far, then each new
    /// chunk as it is written, and its state when that changes — until the tab leaves it or the command's file stops
    /// growing after it ended.
    ///
    /// <para>READ FROM ITS OUTPUT FILE, as the terminal's tab reads it: the command runs detached and writes there,
    /// and nothing else holds its output.</para>
    /// </summary>
    private void OpenCommand(string? sessionId, long? pid)
    {
        if (LiveSession(sessionId) is not { } session || CommandOf(session, pid) is not { } entry) { Error("No such command."); return; }
        var key = $"{session.Id}#{pid}";
        if (_open.TryRemove(key, out var previous)) previous.Dispose();
        var stop = new CancellationTokenSource();
        _open[key] = new Stopper(stop);
        Send(new JsonObject
        {
            ["type"] = "openedCommand", ["sessionId"] = key, ["parent"] = session.Id, ["pid"] = pid,
            ["command"] = entry.Job.Command, ["path"] = entry.Job.OutputPath,
        });
        _ = FollowAsync(key, entry, stop.Token);
    }

    private async Task FollowAsync(string key, Core.Jobs.BackgroundJobEntry entry, CancellationToken stop)
    {
        var follower = entry.Job.OutputPath is { } path ? new Core.Jobs.OutputFollower(path) : null;
        string? lastState = null;
        var first = true;
        try
        {
            while (!stop.IsCancellationRequested && !_gone.IsCancellationRequested)
            {
                var chunk = follower?.ReadNew();

                // THE FILE'S BYTE-ORDER MARK IS NOT OUTPUT: the command's writer puts one at the top, and a page shows it.
                if (first && chunk is { Length: > 0 }) { chunk = chunk.TrimStart('\uFEFF'); first = false; }
                if (!string.IsNullOrEmpty(chunk)) Send(new JsonObject { ["type"] = "commandOutput", ["sessionId"] = key, ["text"] = chunk });
                var view = entry.View();
                var state = view.State.ToString();
                if (state != lastState)
                {
                    lastState = state;
                    Send(new JsonObject
                    {
                        ["type"] = "commandState", ["sessionId"] = key, ["state"] = state,
                        ["exitCode"] = entry.Finished ? view.ExitCode : null,
                    });
                }

                // ONE LAST READ AFTER IT ENDS, for what it wrote on its way out; then nothing more will come.
                if (entry.Finished && string.IsNullOrEmpty(chunk)) break;
                await Task.Delay(400, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException e) { Send(new JsonObject { ["type"] = "commandOutput", ["sessionId"] = key, ["text"] = $"\n(could not read its output: {e.Message})" }); }
    }

    /// <summary>
    /// The terminal tab's Copy to transcript: the command's last lines, queued to go with the next message, without a
    /// turn of their own.
    /// </summary>
    private void CopyCommand(string? sessionId, long? pid)
    {
        if (LiveSession(sessionId) is not { } session || CommandOf(session, pid) is not { } entry) { Error("No such command."); return; }
        var lines = new Core.Jobs.OutputLines();
        if (entry.Job.OutputPath is { } path && new Core.Jobs.OutputFollower(path).ReadNew() is { } all) lines.Append(all);
        var text = string.Join("\n", lines.Display());
        if (text.Length > CopyLimit) text = text[^CopyLimit..];
        session.Inject($"Output of the background command `{Core.Jobs.OutputTail.OneLine(entry.Job.Command)}` "
            + $"(pid {entry.Job.Pid}), its last {text.Length} characters — the whole of it is in "
            + $"{entry.Job.OutputPath}:\n```\n{text}\n```");
        Notice(session.Id, "Its output goes with your next message.", "Info");
    }

    private sealed class Stopper(CancellationTokenSource stop) : IDisposable
    {
        public void Dispose()
        {
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Turns the sandbox on or off for THIS SESSION ONLY, now — nothing is written to config.json, which a browser
    /// never changes. The next session starts as config says.
    /// </summary>
    private void SetSandbox(string? sessionId, bool? enabled)
    {
        if (LiveSession(sessionId) is not { } session || enabled is not { } on) { Error("No such open session."); return; }
        var current = session.Policy?.Sandbox?.Settings ?? session.Resolution?.Sandbox ?? Core.Sandbox.SandboxSettings.AppDefault;
        session.UseSandbox(current with { Enabled = on });
        Notice(session.Id, on
            ? "The sandbox is on for this session."
            : "The sandbox is off for this session: its commands run as you and ask first. config.json is unchanged.", on ? "Info" : "Warning");
        _panels.TryRemove(session.Id, out _);
        if (_hub.PanelFor(session.Id) is { } panel) SendPanel(session.Id, panel);
    }

    private Session? LiveSession(string? sessionId) =>
        sessionId is null || sessionId.Contains('~') ? null : Live(sessionId);

    private void Leave(string? sessionId)
    {
        if (sessionId is null) return;
        if (WebFiles.IsFileKey(sessionId)) { _filesOrNull?.Close(sessionId); return; }
        if (_shells.TryRemove(sessionId, out var shell)) { shell.Dispose(); return; }
        foreach (var key in _shells.Keys.Where(k => k.StartsWith(sessionId + "$", StringComparison.Ordinal)))
            if (_shells.TryRemove(key, out var left)) left.Dispose();

        // AND EVERYTHING ELSE OPEN UNDER IT: its workers' views and its commands' followers, which would otherwise go on
        // streaming to a tab that no longer shows the session.
        foreach (var key in _open.Keys.Where(k => k.StartsWith(sessionId + "~", StringComparison.Ordinal) || k.StartsWith(sessionId + "#", StringComparison.Ordinal)))
            if (_open.TryRemove(key, out var under)) under.Dispose();
        _filesOrNull?.CloseSession(sessionId);
        if (_open.TryRemove(sessionId, out var sub)) sub.Dispose();

        // ITS PROMPTS GO WITH IT: no longer served, they would never hear they were answered.
        foreach (var (id, prompt) in _shown)
            if (prompt.SessionId == sessionId) _shown.TryRemove(id, out _);
    }

    private void Say(string? sessionId, string? text, JsonArray? images = null)
    {
        if (images is { Count: > 0 } && sessionId?.Contains('~') == true)
        {
            Notice(sessionId, "A worker takes text only; send the image in the session.", "Warning");
            return;
        }
        // TO A WORKER: what the terminal's agent tab sends — a message to that worker, not to its session.
        if (sessionId is not null && sessionId.Contains('~') && _open.ContainsKey(sessionId) && !string.IsNullOrWhiteSpace(text))
        {
            var parts = sessionId.Split('~', 2);
            if (Live(parts[0]) is { } parent && parent.SubAgents.FindByAgentId(parts[1]) is { } worker)
            {
                _ = Task.Run(async () =>
                {
                    if (await parent.SendFromAgentTab(worker.Name, text).ConfigureAwait(false) is { } refused)
                        Notice(sessionId, refused, "Warning");
                });
                return;
            }
        }
        // AN IMAGE ALONE IS A MESSAGE: a pasted screenshot with nothing typed asks about the screenshot.
        if (Live(sessionId) is not { } session || (string.IsNullOrWhiteSpace(text) && images is not { Count: > 0 })) { Error("Nothing to send."); return; }
        text ??= "";
        if (text.TrimStart().StartsWith('/'))
        {
            if (WebCommands.Refusal(text, S.Allow, t => session.FindTyped(t) is not null) is { } refused) { Notice(session.Id, refused, "Warning"); return; }
            if (WebCommands.IsHelp(text)) { Notice(session.Id, WebCommands.Help(S.Allow), "Info"); return; }
            if (OwnCommand(session, text)) return;
        }
        Core.Models.Attachments? attached = null;
        if (images is { Count: > 0 })
        {
            if (Attach(session, images, out var why) is not { } prepared)
            {
                Notice(session.Id, why ?? "That image could not be read.", "Warning");
                return;
            }
            attached = new Core.Models.Attachments([], prepared);

            // THE LABELS GO IN THE TEXT, as the terminal's tokens sit in its line: the model reads "[Image #1 …]" and
            // the picture beside it, and a message can say "compare #1 with #2".
            var labels = string.Join(" ", prepared.Select(i => i.Label));
            text = text.Trim().Length == 0 ? labels : text.TrimEnd() + "\n" + labels;
        }
        switch (session.Submit(text, attachments: attached))
        {
            case Session.SubmitOutcome.Refused r:
                Notice(session.Id, r.Reason, "Warning");
                break;
            case Session.SubmitOutcome.NoAgent:
                Notice(session.Id, "No model is configured for this session.", "Warning");
                break;
        }
        _hub.ListChanged();
    }

    /// <summary>
    /// What the composer offers for what is typed — the terminal's own rows, from <see cref="CxAgent.UI.ComposerCompletion"/>,
    /// less what a browser may not run: a refused command or verb is not offered, and an <c>@</c> path only inside the
    /// folders this server opens (anywhere with <c>openAnywhere</c>), never in cxagent's own folder — a directory
    /// listing is as much a read as opening a file. Answered with the number the page sent, so a late reply to an
    /// earlier keystroke is told apart and dropped.
    /// </summary>
    private void Complete(Session session, string text, int caret, int n)
    {
        var manager = S.Manager;
        bool Custom(string line) => session.FindTyped(line) is not null;
        var sources = new CxAgent.UI.ComposerSources(manager.Commands,
            set =>
            {
                var values = session.Values(set);
                if (values.Count == 0) values = manager.Values(set, session.WorkingDirectory);
                return [.. values.Select(v => new Core.Commands.CommandArgument(v.Name, v.Summary))];
            },
            [.. Core.Commands.SessionCommands.FromCatalog(session.Catalog(), manager.Commands.IsRegistered),
             .. WebCommands.OwnCommands(S.Allow, manager.Commands.IsRegistered)],
            session.WorkingDirectory) { CommandsOff = session.InAside };

        var paths = CxAgent.UI.AtToken.At(text, caret) is not null;
        var rows = CxAgent.UI.ComposerCompletion.For(text, caret, sources)
            .Where(r => paths ? Reachable(r.Label) : r.Text is null || WebCommands.Refusal(r.Text, S.Allow, Custom) is null);
        Send(new JsonObject
        {
            ["type"] = "completions", ["sessionId"] = session.Id, ["n"] = n,
            ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode)new JsonObject
            {
                ["label"] = r.Label, ["summary"] = r.Summary, ["text"] = r.Text, ["caret"] = r.Caret,
            })]),
        });

        bool Reachable(string label)
        {
            var full = Path.GetFullPath(Path.Combine(session.WorkingDirectory, label));
            if (WebSessionHub.Resolved(full, [S.Paths.ConfigDir]) is not null) return false;
            return S.Allow.OpenAnywhere || WebSessionHub.Resolved(full, S.Roots) is not null;
        }
    }

    /// <summary>
    /// The commands a browser answers itself, because the terminal answers them with a window or a tab of its own:
    /// they reach this page as a download or a tab, never the session's registry, which under /web is the terminal's.
    /// </summary>
    private bool OwnCommand(Session session, string text)
    {
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var args = parts.Length > 1 ? parts[1].Trim() : "";
        switch (parts[0].ToLowerInvariant())
        {
            case "/export":
                Export(session.Id, args.Equals("full", StringComparison.OrdinalIgnoreCase));
                return true;
            case "/open":
                OpenFile(session, args);
                return true;
            case "/shell":
                OpenShell(session);
                return true;
            case "/config":
                ConfigForm("general");
                return true;
            // THE VERBS ONLY: the bare listing is Core's and runs as typed. These would otherwise reach the terminal's
            // verbs, which open its tab and ask in its window.
            case "/memory" when args.StartsWith("open", StringComparison.OrdinalIgnoreCase):
                OpenMemory(session, args[4..]);
                return true;
            case "/memory" when args.StartsWith("forget", StringComparison.OrdinalIgnoreCase):
                ForgetMemory(session, args[6..], confirmed: false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// <c>/open</c>: a file in a tab of its own. Under the web roots always; anywhere the terminal's /open reaches only
    /// with <c>web.allow.openAnywhere</c> — a login reading and writing any file the user can is a decision, not a
    /// default.
    /// </summary>
    private void OpenFile(Session session, string argument)
    {
        var target = CxAgent.UI.OpenTarget.For(argument, session.WorkingDirectory);
        if (target.Path is not { } path) { Notice(session.Id, "Name the file: /open <path>.", "Info"); return; }
        var allowed = WebSessionHub.Resolved(path, S.Roots)
            ?? (S.Allow.OpenAnywhere ? WebSessionHub.Resolved(path, [Path.GetPathRoot(path) ?? "/"]) : null);
        if (allowed is null)
        {
            Notice(session.Id, File.Exists(path) || Directory.Exists(path)
                ? $"{path} is outside the folders this server opens. Opening files anywhere is turned on in the terminal: F8 › Web › Browser access, or web.allow.openAnywhere in config.json."
                : $"There is no file {path}.", "Warning");
            return;
        }
        // NEVER CXAGENT'S OWN FOLDER, whatever the roots and switches say: it holds config.json — the keys, the users'
        // hashes and the very switches a login could turn on for itself — the permission rules, the server's certificate
        // and every session's logs. A root above it (cxagent web started in ~) must not reach it.
        if (WebSessionHub.Resolved(allowed, [S.Paths.ConfigDir]) is not null)
        {
            Notice(session.Id, $"{path} is in cxagent's own folder, which a browser never opens — its configuration is the config page's, behind web.allow.config.", "Warning");
            return;
        }
        if (_files.Open(session, path, allowed) is { } refused) Notice(session.Id, refused, "Warning");
    }

    /// <summary>
    /// <c>/memory open</c>: a note in the editor, BY NAME ONLY. The one thing in cxagent's own folder a browser opens —
    /// the user's notes are theirs to read and correct wherever they work, as in the terminal. A name, never a path: the
    /// store resolves it inside the memory folder, a name is a kebab-case slug, and a link there is not a note — so no
    /// other file of that folder is reachable this way.
    /// </summary>
    private void OpenMemory(Session session, string name)
    {
        name = name.Trim();
        if (session.Memory is not { } store) { Notice(session.Id, "Memory is off. It is turned on in the terminal: F8 › General, or memory.enabled in config.json.", "Info"); return; }
        if (store.PathOf(name) is not { } path) { Notice(session.Id, $"There is no note called '{name}'. /memory lists them.", "Warning"); return; }
        if (_files.Open(session, path, path) is { } refused) Notice(session.Id, refused, "Warning");
    }

    /// <summary><c>/memory forget</c>: asked first, as the terminal asks, then deleted.</summary>
    private void ForgetMemory(Session session, string name, bool confirmed)
    {
        var ask = session.AskForgetMemory(name);
        if (ask.Note is not { } note) { Notice(session.Id, ask.Text, "Warning"); return; }
        if (!confirmed)
        {
            Send(new JsonObject { ["type"] = "confirmForget", ["sessionId"] = session.Id, ["name"] = note.Name, ["text"] = ask.Text });
            return;
        }
        try { session.ForgetMemory(note.Name); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notice(session.Id, $"Could not delete: {e.Message}", "Warning"); }
        if (_hub.PanelFor(session.Id) is { } panel) SendPanel(session.Id, panel);
    }

    // ---- the plugin marketplace: F2, behind web.allow.plugins ---------------------------------------------------------

    private WebPlugins? _pluginsOrNull;
    private WebPlugins Plugins => _pluginsOrNull ??= new WebPlugins(S.Paths, S.Manager);

    private const string PluginsOff =
        "Plugins are off for browsers. They are turned on in the terminal: F8 › Web › Browser access (plugins), or web.allow.plugins in config.json.";

    private async Task PluginsAsync(string? sessionId, bool refresh)
    {
        if (!S.Allow.Plugins) { Error(PluginsOff); return; }
        if (LiveSession(sessionId) is not { } session) { Error("Open a session first: plugins load into a session."); return; }
        Send(await Plugins.ListAsync(session, refresh).ConfigureAwait(false));
    }

    /// <summary>
    /// One action on one plugin row. Long ones — a download waits on its permission prompt, which this same tab
    /// answers — run beside the message loop, so the answer can arrive while the action waits for it.
    /// </summary>
    private async Task PluginActionAsync(string? sessionId, string? name, string? action, string? folder, JsonObject? values)
    {
        if (!S.Allow.Plugins) { Error(PluginsOff); return; }
        if (LiveSession(sessionId) is not { } session || name is null || action is null) { Error("Which plugin?"); return; }
        _ = Task.Run(async () =>
        {
            string said;
            // ANY FAILURE IS ANSWERED: a page left without a result keeps its "…ing" line forever.
            try { said = await Plugins.ActAsync(session, name, action, folder, values).ConfigureAwait(false); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                said = $"'{name}': {e.Message}";
            }
            Send(new JsonObject { ["type"] = "pluginResult", ["sessionId"] = session.Id, ["name"] = name, ["action"] = action, ["text"] = said });
            Send(await Plugins.ListAsync(session, refresh: false).ConfigureAwait(false));
        });
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ---- the config page: config.json, behind web.allow.config ------------------------------------------------------

    private WebConfig? _configOrNull;
    private WebConfig Config => _configOrNull ??= new WebConfig(S.Paths, S.Manager, S.ConfigSaved);

    private void GetConfig()
    {
        if (!S.Allow.Config) { Error(WebCommands.Refusal("/config", S.Allow) ?? ConfigOff); return; }
        if (Config.Read(out var why) is { } text) Send(new JsonObject { ["type"] = "config", ["text"] = text });
        else Error(why ?? "config.json could not be read.");
    }

    /// <summary>One of F8's tabs as a form, from the file as it is now.</summary>
    private void ConfigForm(string tab, string? item = null)
    {
        if (!S.Allow.Config) { Error(ConfigOff); return; }
        if (tab == "json") { GetConfig(); return; }
        var read = CxAgent.UI.Config.ProvidersFile.Read(S.Paths, CxAgent.UI.Startup.Env());
        if (read.Settings is not { } settings) { Error(string.Join(" ", read.Errors)); return; }
        JsonObject form;
        switch (tab)
        {
            case "general":
                form = new JsonObject { ["sections"] = WebConfigForms.General(CxAgent.UI.Config.GeneralDraft.From(settings, read.TavilyKeyAsWritten)) };
                break;
            case "providers":
                form = WebConfigForms.Providers(settings, item);
                break;
            case "mcp":
                form = WebConfigForms.Mcp(settings, item);
                break;
            case "agents":
                form = WebConfigForms.Agents(settings, item);
                break;
            case "hooks":
                if (HooksDraft() is not { } hooks) return;
                form = WebConfigForms.Hooks(hooks, item);
                break;
            case "web":
                form = WebConfigForms.Web(settings.Web ?? Core.Web.WebSettings.Default);
                break;
            case "project":
            {
                if (ConfigSession("its folder is the project") is not { } session) return;
                form = WebConfigForms.Project(Core.Sessions.ProjectSummary.For(new Core.Sessions.ProjectFacts(session.WorkingDirectory,
                    session.Policy?.Trust ?? Core.Permissions.TrustState.Unknown, session.Catalog(),
                    session.Hooks?.Snapshot ?? Core.Hooks.HookSnapshot.Empty) { Memory = session.Memory }), session.WorkingDirectory);
                break;
            }
            default:
                Error($"No such configuration tab: {tab}.");
                return;
        }
        form["type"] = "configForm";
        form["tab"] = tab;
        form["tabs"] = new JsonArray([.. WebConfigForms.Tabs.Select(t => (JsonNode)new JsonObject { ["id"] = t.Id, ["title"] = t.Title })]);
        Send(form);
    }

    /// <summary>
    /// A form's save: the tab's draft, built from the file as it is now, set from what the page sent, checked by the
    /// draft's own rules, and written by its own Apply — as the terminal's tab saves it.
    /// </summary>
    private void ConfigFormSave(string tab, JsonObject values, string? choice, string? item)
    {
        if (!S.Allow.Config) { Error(ConfigOff); return; }
        var read = CxAgent.UI.Config.ProvidersFile.Read(S.Paths, CxAgent.UI.Startup.Env());
        if (read.Settings is not { } settings) { Refused(read.Errors); return; }
        Core.Config.CacheChoice? picked = choice is not null && Enum.TryParse<Core.Config.CacheChoice>(choice, true, out var c) ? c : null;
        WebConfigSave result;
        switch (tab)
        {
            case "general":
            {
                var draft = CxAgent.UI.Config.GeneralDraft.From(settings, read.TavilyKeyAsWritten);
                WebConfigForms.SetGeneral(draft, values);
                if (draft.Problem is { } problem) { Refused([problem]); return; }
                if (!draft.IsDirty) { Send(new JsonObject { ["type"] = "configSaved", ["warnings"] = new JsonArray("Nothing changed.") }); return; }
                result = Config.Commit(draft.Apply, new Core.Config.ConfigChange(new HashSet<string>(), new Dictionary<string, string>(), false, "general settings saved"),
                    Core.Config.CacheChoice.ApplyNow);
                break;
            }
            case "providers":
            {
                if (ProvidersSave(settings, values, item, picked) is not { } done) return;
                (result, item) = done;
                break;
            }
            case "mcp":
            {
                if (WebConfigForms.NumberProblem(values, "timeoutMs") is { } bad) { Refused([bad]); return; }
                var isNew = item == WebConfigForms.NewItem;
                var name = isNew ? ((values["name"] as JsonValue)?.GetValue<string>() ?? "").Trim() : item ?? "";
                if (name.Length == 0) { Refused(["A server needs a name."]); return; }
                var cfg = isNew ? new Core.Llm.McpServerConfig([]) : settings.McpServers.TryGetValue(name, out var had) ? had : null;
                if (cfg is null) { Refused([$"There is no server '{name}'."]); return; }
                var draft = WebConfigForms.SetMcp(name, cfg, isNew, values);
                if (isNew && string.IsNullOrWhiteSpace(draft.CommandLine) && !draft.IsRemote) { Refused(["A server needs a command or a URL."]); return; }

                // A LOCAL SERVER'S COMMAND RUNS AS THE USER: a new one, or a changed command line or folder, needs the shell switch.
                if (!S.Allow.Shell && !draft.IsRemote && (isNew || draft.ToFields() is { } f && (f.Command is not null || f.Cwd is not null || f.Clear.Contains("cwd"))))
                {
                    Refused([WebConfig.CommandsNeedShell]);
                    return;
                }
                if (!draft.IsDirty) { Send(new JsonObject { ["type"] = "configSaved", ["warnings"] = new JsonArray("Nothing changed.") }); return; }
                var fields = draft.ToFields();
                result = McpCommit(isNew ? root => Core.Config.McpEdits.Add(root, name, fields) : root => Core.Config.McpEdits.Update(root, name, fields));
                item = name;
                break;
            }
            case "hooks":
            {
                if (WebConfigForms.NumberProblem(values, "timeoutSeconds") is { } bad) { Refused([bad]); return; }
                if (HooksDraft() is not { } hooks) return;
                if (item == WebConfigForms.NewItem)
                {
                    var source = Enum.TryParse<Core.Hooks.HookSource>((values["source"] as JsonValue)?.GetValue<string>(), out var src) ? src : Core.Hooks.HookSource.User;
                    if (!hooks.CanSaveTo(source)) { Refused([$"There is no {CxAgent.UI.Config.HooksFile.Label(source)} to save to here."]); return; }
                    var added = hooks.Add(source);
                    WebConfigForms.SetHook(added, values);
                    if (!S.Allow.Shell && added.Type == "command") { Refused([WebConfig.CommandsNeedShell]); return; }
                    item = null;
                }
                else if (WebConfigForms.HookAt(hooks, item) is { } hook)
                {
                    if (!hook.Editable) { Refused(["This hook's type is not one cxagent runs; it is kept as it is."]); return; }
                    var (type, command, shellName) = (hook.Type, hook.Command, hook.Shell);
                    WebConfigForms.SetHook(hook, values);

                    // A COMMAND HOOK MADE, OR ITS COMMAND CHANGED, runs that command as the user.
                    if (!S.Allow.Shell && hook.Type == "command" && (type != "command" || command != hook.Command || shellName != hook.Shell))
                    {
                        Refused([WebConfig.CommandsNeedShell]);
                        return;
                    }
                    item = null;
                }
                else { Refused(["That hook changed since this page read it — Reload."]); return; }
                if (HooksSave(hooks) is not { } saved) return;
                result = saved;
                break;
            }
            case "agents":
            {
                if (WebConfigForms.NumberProblem(values, "maxTurns") is { } bad) { Refused([bad]); return; }
                var draft = WebConfigForms.SetAgent(WebConfigForms.DraftFor(settings, item ?? Core.Agents.AgentTypeCatalog.DefaultTypeName), values);
                if (draft.IsNew && draft.Name.Length == 0) { Refused(["A type needs a name."]); return; }
                if (draft.ProviderProblem is { } problem) { Refused([problem]); return; }
                if (!draft.IsDirty) { Send(new JsonObject { ["type"] = "configSaved", ["warnings"] = new JsonArray("Nothing changed.") }); return; }
                var fields = draft.ToFields();
                var name = draft.Name;
                Action<JsonObject> edit = draft.IsGeneral ? root => Core.Config.AgentEdits.SetGeneral(root, fields)
                    : draft.IsBuiltin ? root => Core.Config.AgentEdits.SetOverride(root, name, fields)
                    : draft.IsNew ? root => Core.Config.AgentEdits.AddType(root, name, fields)
                    : root => Core.Config.AgentEdits.UpdateType(root, name, fields);
                // AN AGENT TYPE TOUCHES NO PROMPT CACHE: every session is refreshed in place, as F8's Agents tab does.
                result = Config.Commit(edit, new Core.Config.ConfigChange(new HashSet<string>(), new Dictionary<string, string>(), true, $"{name} saved"),
                    Core.Config.CacheChoice.ApplyNow);
                item = name;
                break;
            }
            case "project":
            {
                // TRUST, THE ONE SETTING HERE, through /trust's own path — stored, announced and applied as there.
                if (ConfigSession("its folder is the project") is not { } session) return;
                if (WebConfigForms.ProjectTrust(values) is not { } want || want == (session.Policy?.FolderTrusted == true))
                {
                    Send(new JsonObject { ["type"] = "configSaved", ["warnings"] = new JsonArray("Nothing changed.") });
                    return;
                }
                session.SetTrust(want ? "yes" : "no");
                result = new WebConfigSave(true, []);
                break;
            }
            default:
                Error($"No such configuration tab: {tab}.");
                return;
        }
        if (!result.Saved) { Refused(result.Errors); return; }
        Send(new JsonObject { ["type"] = "configSaved", ["tab"] = tab, ["warnings"] = new JsonArray([.. result.Errors.Select(e => (JsonNode)e)]) });
        ConfigForm(tab, item);
    }

    /// <summary>
    /// The Providers tab's save: the defaults, and one instance — new or edited — through F8's InstanceDraft, which
    /// decides what changed and which sessions it moves. Null when it answered the page itself: a refusal, nothing to
    /// save, or the question a model change asks first.
    /// </summary>
    private (WebConfigSave, string?)? ProvidersSave(Core.Llm.ProviderSettings settings, JsonObject values, string? item, Core.Config.CacheChoice? choice)
    {
        var edits = new List<Action<JsonObject>>();
        var instances = new HashSet<string>(StringComparer.Ordinal);
        var moved = new HashSet<string>(StringComparer.Ordinal);
        var everyone = false;
        var notes = new List<string>();

        var newDefault = (values["defaultProvider"] as JsonValue)?.GetValue<string>();
        if (newDefault is { Length: > 0 } && newDefault != settings.DefaultProvider)
        {
            edits.Add(root => Core.Config.ProviderEdits.SetDefault(root, newDefault));
            everyone = true;
            notes.Add($"{newDefault} is now the default");
        }
        var reviewer = (values["classifier"] as JsonValue)?.GetValue<string>();
        if (reviewer is not null && (reviewer.Length == 0 ? null : reviewer) != settings.Classifier)
        {
            edits.Add(root => Core.Config.ProviderEdits.SetReviewer(root, reviewer.Length == 0 ? null : reviewer));
            everyone = true;
            notes.Add(reviewer.Length == 0 ? "no reviewer" : $"{reviewer} reviews auto mode");
        }

        if (WebConfigForms.NumberProblem(values, "contextWindow", "maxConcurrentAgents") is { } bad) { Refused([bad]); return null; }
        if (item == WebConfigForms.NewItem)
        {
            var name = ((values["name"] as JsonValue)?.GetValue<string>() ?? "").Trim();
            var kind = (values["kind"] as JsonValue)?.GetValue<string>() ?? "openai-compatible";
            var model = ((values["model"] as JsonValue)?.GetValue<string>() ?? "").Trim();
            if (name.Length == 0 || model.Length == 0) { Refused(["A new provider needs a name and a model."]); return null; }
            var draft = WebConfigForms.SetInstance(name, new Core.Llm.ProviderInstanceConfig(kind, model, null, null, null), values);
            var fresh = new CxAgent.UI.Config.InstanceDraft(name, new Core.Llm.ProviderInstanceConfig(kind, model, null, null, null)) { IsNew = true };
            fresh.BaseUrl = draft.BaseUrl;
            fresh.NewApiKey = draft.NewApiKey;
            var fields = fresh.ToFields();
            edits.Add(root => Core.Config.ProviderEdits.Add(root, name, fields));
            var added = fresh.AddChange(settings);
            everyone |= added.Everyone;
            notes.Add(added.Note);
            item = name;
        }
        else if (item is not null && settings.Providers.TryGetValue(item, out var cfg))
        {
            var draft = WebConfigForms.SetInstance(item, cfg, values);
            if (draft.IsDirty)
            {
                var fields = draft.ToFields();
                var name = item;
                edits.Add(root => Core.Config.ProviderEdits.Update(root, name, fields));
                var change = draft.ChangeFor(settings);
                instances.UnionWith(change.Instances);
                moved.UnionWith(change.ModelChanged);
                everyone |= change.Everyone;
                notes.Add(change.Note);
            }
        }
        if (edits.Count == 0) { Send(new JsonObject { ["type"] = "configSaved", ["warnings"] = new JsonArray("Nothing changed.") }); return null; }

        // F8'S QUESTION: a session on an instance whose model or endpoint changed re-sends its conversation when it moves.
        if (choice is null && S.Manager.Sessions.Any(x => x.InstanceName is { } i && moved.Contains(i)))
        {
            Send(new JsonObject { ["type"] = "configAsk", ["instances"] = new JsonArray([.. moved.Select(i => (JsonNode)i)]) });
            return null;
        }
        var all = new Core.Config.ConfigChange(instances, new Dictionary<string, string>(), everyone, string.Join("; ", notes)) { ModelChanged = moved };
        return (Config.Commit(root => { foreach (var e in edits) e(root); }, all, choice ?? Core.Config.CacheChoice.ApplyNow), item);
    }

    /// <summary>An action on one item of a list tab — remove a provider — asked about first when F8 asks.</summary>
    private void ConfigFormAction(string tab, string? item, string action, bool confirmed)
    {
        if (!S.Allow.Config) { Error(ConfigOff); return; }
        var read = CxAgent.UI.Config.ProvidersFile.Read(S.Paths, CxAgent.UI.Startup.Env());
        if (read.Settings is not { } settings || item is null) { Refused(read.Errors); return; }
        switch ((tab, action))
        {
            case ("providers", "remove"):
            {
                var using_ = S.Manager.Sessions.Where(x => x.InstanceName == item).Select(x => System.IO.Path.GetFileName(x.WorkingDirectory)).ToList();
                var plan = CxAgent.UI.Config.ProviderRemovalPlan.For(settings, item, using_);
                if (plan.Refused) { Refused(plan.Lines); return; }
                if (!confirmed)
                {
                    Send(new JsonObject
                    {
                        ["type"] = "configConfirm", ["tab"] = tab, ["item"] = item, ["action"] = action,
                        ["text"] = $"Remove '{item}'?" + (plan.Lines.Count > 0 ? " " + string.Join("; ", plan.Lines) + "." : ""),
                    });
                    return;
                }
                var result = Config.Commit(root => Core.Config.ProviderEdits.Remove(root, item),
                    new Core.Config.ConfigChange(new HashSet<string> { item }, new Dictionary<string, string>(), true, $"{item} was removed"),
                    Core.Config.CacheChoice.ApplyNow);
                if (!result.Saved) { Refused(result.Errors); return; }
                Send(new JsonObject { ["type"] = "configSaved", ["tab"] = tab, ["warnings"] = new JsonArray() });
                ConfigForm(tab);
                return;
            }
            case ("mcp", "enable" or "disable" or "remove"):
            {
                // TURNING A LOCAL SERVER ON starts its command.
                if (action == "enable" && !S.Allow.Shell && settings.McpServers.TryGetValue(item, out var server) && !server.IsRemote)
                {
                    Refused([WebConfig.CommandsNeedShell]);
                    return;
                }
                if (action == "remove" && !confirmed)
                {
                    Send(new JsonObject { ["type"] = "configConfirm", ["tab"] = tab, ["item"] = item, ["action"] = action, ["text"] = $"Remove the MCP server '{item}'? Its tools leave every session." });
                    return;
                }
                var result = McpCommit(action switch
                {
                    "remove" => root => Core.Config.McpEdits.Remove(root, item),
                    _ => root => Core.Config.McpEdits.SetEnabled(root, item, action == "enable"),
                });
                if (!result.Saved) { Refused(result.Errors); return; }
                Send(new JsonObject { ["type"] = "configSaved", ["tab"] = tab, ["warnings"] = new JsonArray([.. result.Errors.Select(e => (JsonNode)e)]) });
                ConfigForm(tab, action == "remove" ? null : item);
                return;
            }
            case ("hooks", "remove"):
            {
                if (HooksDraft() is not { } hooks) return;
                if (WebConfigForms.HookAt(hooks, item) is not { } e) { Refused(["That hook changed since this page read it — Reload."]); return; }
                if (!confirmed)
                {
                    Send(new JsonObject { ["type"] = "configConfirm", ["tab"] = tab, ["item"] = item, ["action"] = action,
                        ["text"] = $"Remove the {e.Event} hook{(e.Name is { } n ? $" '{n}'" : "")} from {CxAgent.UI.Config.HooksFile.Label(e.Source)}?" });
                    return;
                }
                hooks.Remove(e);
                if (HooksSave(hooks) is not { } result) return;
                if (!result.Saved) { Refused(result.Errors); return; }
                Send(new JsonObject { ["type"] = "configSaved", ["tab"] = tab, ["warnings"] = new JsonArray([.. result.Errors.Select(e => (JsonNode)e)]) });
                ConfigForm(tab);
                return;
            }
            case ("agents", "reset" or "remove"):
            {
                if (!confirmed)
                {
                    Send(new JsonObject
                    {
                        ["type"] = "configConfirm", ["tab"] = tab, ["item"] = item, ["action"] = action,
                        ["text"] = action == "reset" ? $"Reset '{item}' to what cxagent ships? Your changes to it are removed." : $"Remove the agent type '{item}'?",
                    });
                    return;
                }
                var result = Config.Commit(action == "reset" ? root => Core.Config.AgentEdits.ResetBuiltin(root, item) : root => Core.Config.AgentEdits.RemoveType(root, item),
                    new Core.Config.ConfigChange(new HashSet<string>(), new Dictionary<string, string>(), true, $"{item} {(action == "reset" ? "reset to shipped" : "removed")}"),
                    Core.Config.CacheChoice.ApplyNow);
                if (!result.Saved) { Refused(result.Errors); return; }
                Send(new JsonObject { ["type"] = "configSaved", ["tab"] = tab, ["warnings"] = new JsonArray() });
                ConfigForm(tab, action == "reset" ? item : null);
                return;
            }
            default:
                Error("Unknown action.");
                return;
        }
    }

    // THE SESSION THE CONFIG PAGE WAS OPENED FROM: its folder is where the project's and local hooks files are.
    private string? _configFor;

    private CxAgent.UI.Config.HooksDraft? HooksDraft() =>
        ConfigSession("its folder holds the project's hooks") is { } session
            ? CxAgent.UI.Config.HooksFile.Load(S.Paths, CxAgent.UI.Startup.Env(), session.WorkingDirectory)
            : null;

    /// <summary>
    /// The session the config page was opened from, or null — said, with <paramref name="why"/> it is needed. THE PAGE'S
    /// OWN SESSION, NEVER ANOTHER: a page whose session closed must not edit a different project's hooks or trust.
    /// </summary>
    private Session? ConfigSession(string why)
    {
        var session = _configFor is null ? OpenSessions.Select(LiveSession).FirstOrDefault(x => x is not null) : LiveSession(_configFor);
        if (session is null) { Error($"Open a session first: {why}."); return null; }
        _configFor = session.Id;
        return session;
    }

    /// <summary>
    /// The Hooks tab's save, as F8's: every hook checked for the field its type needs, the files written, and the
    /// hooks applied — the session this page came from runs the files as saved; every other takes this machine's only,
    /// its own repository's still waiting for its own review.
    /// </summary>
    private WebConfigSave? HooksSave(CxAgent.UI.Config.HooksDraft draft)
    {
        if (draft.Entries.Select(CxAgent.UI.Config.HooksTab.MissingField).FirstOrDefault(x => x is not null) is { } missing) { Refused([missing]); return null; }
        if (!draft.IsDirty) { Send(new JsonObject { ["type"] = "configSaved", ["warnings"] = new JsonArray("Nothing changed.") }); return null; }
        var errors = CxAgent.UI.Config.HooksFile.Save(draft, S.Paths, CxAgent.UI.Startup.Env());
        if (errors.Count > 0) return new WebConfigSave(false, errors);
        var user = CxAgent.UI.Config.ProvidersFile.Read(S.Paths, CxAgent.UI.Startup.Env()).Settings?.Hooks ?? Core.Hooks.HooksSettings.None;
        var shown = LiveSession(_configFor);
        var safe = shown is not null && CxAgent.UI.Config.HooksFile.SafeToApply(draft)
                   && Core.Hooks.HookSources.RepoRoot(shown.WorkingDirectory) == draft.RepoRoot;
        foreach (var x in S.Manager.Sessions)
        {
            if (ReferenceEquals(x, shown) && safe) x.ReviewHooks(user);
            else x.ReviewSystemHooks(user);
        }
        return new WebConfigSave(true, safe ? [] : ["A project hooks file changed on disk while this was open, so only this machine's hooks were applied. /hooks review applies it."]);
    }

    /// <summary>
    /// An MCP save: written like any config save, then the servers reconnected to what was written — the host's own
    /// manager, as F8's MCP tab reloads. Where the host has none, the change takes effect at the next start, and says so.
    /// </summary>
    private WebConfigSave McpCommit(Action<JsonObject> edit)
    {
        var result = Config.Commit(edit, new Core.Config.ConfigChange(new HashSet<string>(), new Dictionary<string, string>(), true, "MCP servers changed"),
            Core.Config.CacheChoice.ApplyNow);
        if (!result.Saved) return result;
        if (S.McpReload is not { } reload) return result with { Errors = [.. result.Errors, "Saved; MCP servers reconnect at the next start."] };
        var servers = CxAgent.UI.Config.ProvidersFile.Read(S.Paths, CxAgent.UI.Startup.Env()).Settings?.McpServers;
        if (servers is not null) _ = Task.Run(() => reload(servers));
        return result;
    }

    private void Refused(IEnumerable<string> errors) =>
        Send(new JsonObject { ["type"] = "configRefused", ["errors"] = new JsonArray([.. errors.Select(e => (JsonNode)e)]) });

    private const string ConfigOff =
        "Configuration is off for browsers. It is turned on in the terminal: F8 › Web › Browser access (config), or web.allow.config in config.json.";

    /// <summary>
    /// A save from the page. When it would move a running session to another model or endpoint, the page is asked how
    /// first — the question F8 asks, because the move re-sends the conversation — unless it already said.
    /// </summary>
    private void SaveConfig(string text, string? choice)
    {
        if (!S.Allow.Config) { Error(ConfigOff); return; }
        Core.Config.CacheChoice? picked = choice is not null && Enum.TryParse<Core.Config.CacheChoice>(choice, ignoreCase: true, out var c) ? c : null;
        if (picked is null && Config.Moved(text) is { Count: > 0 } moved)
        {
            Send(new JsonObject { ["type"] = "configAsk", ["instances"] = new JsonArray([.. moved.Select(i => (JsonNode)i)]) });
            return;
        }
        var result = Config.Save(text, picked ?? Core.Config.CacheChoice.ApplyNow, S.Allow.Shell);
        if (!result.Saved)
        {
            Send(new JsonObject { ["type"] = "configRefused", ["errors"] = new JsonArray([.. result.Errors.Select(e => (JsonNode)e)]) });
            return;
        }
        Send(new JsonObject { ["type"] = "configSaved", ["warnings"] = new JsonArray([.. result.Errors.Select(e => (JsonNode)e)]) });
        _hub.ListChanged();
    }

    // ---- the shell: /shell, a real terminal in a tab, behind web.allow.shell -----------------------------------------

    private sealed record OpenShellTab(Session Session, WebPty Pty) : IDisposable
    {
        public void Dispose() => Pty.Dispose();
    }

    private readonly ConcurrentDictionary<string, OpenShellTab> _shells = new();
    private int _shellCount;

    /// <summary>
    /// The terminal's /shell, in a browser tab: the user's own shell on a pseudo-terminal, in the session's folder, as
    /// them — outside the sandbox and its prompts, which is why it waits on a switch only the terminal turns on.
    /// </summary>
    private void OpenShell(Session session)
    {
        if (!S.Allow.Shell)
        {
            Notice(session.Id, WebCommands.Refusal("/shell", S.Allow) ?? "The shell is off for browsers.", "Warning");
            return;
        }
        var key = $"{session.Id}${Interlocked.Increment(ref _shellCount)}";
        WebPty pty;
        try { pty = WebPty.Start(session.WorkingDirectory, 24, 80); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or PlatformNotSupportedException)
        {
            Notice(session.Id, $"The shell could not start: {e.Message}", "Warning");
            return;
        }
        _shells[key] = new OpenShellTab(session, pty);
        Send(new JsonObject
        {
            ["type"] = "openedShell", ["sessionId"] = key, ["parent"] = session.Id,
            ["shell"] = pty.ShellName, ["folder"] = session.WorkingDirectory,
        });

        // A THREAD OF ITS OWN, because a PTY read blocks until the shell writes; base64 because a read can end inside a
        // UTF-8 character, and the page's terminal decodes the stream itself.
        new Thread(() =>
        {
            var buffer = new byte[16 * 1024];
            while (!_gone.IsCancellationRequested)
            {
                var n = pty.Read(buffer);
                if (n <= 0) break;
                Send(new JsonObject { ["type"] = "shellOutput", ["sessionId"] = key, ["data"] = Convert.ToBase64String(buffer, 0, n) });
            }
            pty.WaitForExit(500);
            if (_shells.ContainsKey(key)) Send(new JsonObject { ["type"] = "shellExited", ["sessionId"] = key, ["exitCode"] = pty.ExitCode });
        }) { IsBackground = true, Name = "web shell " + key }.Start();
    }

    /// <summary>
    /// What a shell showed, sent by the page that drew it — its terminal holds the screen — as the terminal's tab sends
    /// it: queued with the next message, never a turn of its own; someone who just read a build log may be reading.
    /// </summary>
    private void ShellReport(string? key, JsonArray? lines, int? exitCode)
    {
        if (key is null || !_shells.TryGetValue(key, out var shell) || lines is null) return;
        var text = lines.Select(l => (l as JsonValue)?.TryGetValue<string>(out var t) == true ? t : "").ToList();
        shell.Session.Inject(Core.Commands.ShellTranscript.Render(new Core.Commands.ShellOutcome("", text, exitCode)));
        Notice(shell.Session.Id, "What the shell showed goes with your next message.", "Info");
    }

    // ONE NUMBERING PER SESSION FOR THIS TAB, as the terminal's composer numbers its tokens for its life.
    private readonly ConcurrentDictionary<string, int> _imageCount = new();

    /// <summary>
    /// The images a message carries, shrunk and encoded as the terminal's paste does — or null, with why, when one is
    /// not an image or the model is known not to see them. All or nothing: a message sent without one of the pictures
    /// it talks about answers a different question.
    /// </summary>
    private List<Core.Models.AttachedImage>? Attach(Session session, JsonArray images, out string? why)
    {
        why = null;
        if (images.Count > MaxImages) { why = $"At most {MaxImages} images go with one message."; return null; }
        if (CxAgent.UI.ImageTokens.Refusal(session.Provider) is { } cannot) { why = cannot; return null; }
        var prepared = new List<CxAgent.UI.ImageTokens.Prepared>();
        foreach (var node in images)
        {
            var name = (node?["name"] as JsonValue)?.GetValue<string>() ?? "the pasted image";
            byte[] bytes;
            try { bytes = Convert.FromBase64String((node?["data"] as JsonValue)?.GetValue<string>() ?? ""); }
            catch (FormatException) { why = $"{name} did not arrive whole."; return null; }
            if (CxAgent.UI.ImageTokens.Encode(bytes, name, out var refusal) is not { } one) { why = refusal; return null; }
            prepared.Add(one);
        }
        return [.. prepared.Select(p =>
        {
            var n = _imageCount.AddOrUpdate(session.Id, 1, (_, c) => c + 1);
            var label = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[Image #{n} {p.Width}×{p.Height}]");
            return new Core.Models.AttachedImage(label, p.Hash, p.Width, p.Height)
            {
                Bytes = p.Bytes,
                OriginalWidth = p.Original.Width,
                OriginalHeight = p.Original.Height,
                MediaType = p.MediaType,
            };
        })];
    }

    /// <summary>Esc in the terminal's btw mode: the aside stops, and what is typed goes to the main agent again.</summary>
    private void LeaveAside(string? sessionId)
    {
        if (LiveSession(sessionId) is not { } session) { Error("No such open session."); return; }
        session.LeaveAside();
        _panels.TryRemove(session.Id, out _);
        if (_hub.PanelFor(session.Id) is { } panel) SendPanel(session.Id, panel);
    }

    /// <summary>
    /// F12 for a browser: the conversation as markdown, readable or full, handed to the page to save. Read from the
    /// session's saved history as the terminal's export is, so the two files are the same.
    /// </summary>
    private void Export(string? sessionId, bool full)
    {
        if (LiveSession(sessionId) is not { } session) { Error("No such open session."); return; }
        session.SaveNow();
        if (session.SessionId is not { } agentId
            || Core.Export.ExportSource.Load(S.Paths, agentId, session.SpendLabel) is not { } source)
        {
            Notice(session.Id, "Nothing to export yet.", "Info");
            return;
        }
        var parts = full ? Core.Export.ExportOptions.Full : Core.Export.ExportOptions.Readable;
        var result = Core.Export.ExportMarkdown.Render(source, new Core.Export.ExportOptions(Core.Export.ExportScope.Whole, 0, parts));
        var title = SessionHeader.Read(new SessionFolder(S.Paths, agentId))?.Title;
        Send(new JsonObject
        {
            ["type"] = "export",
            ["sessionId"] = session.Id,
            ["name"] = Core.Export.ExportTarget.DefaultFileName(title, DateTimeOffset.Now),
            ["markdown"] = result.Markdown,
        });
    }

    private void Answer(JsonObject m)
    {
        var id = Str(m, "promptId");
        if (id is null || !_shown.TryGetValue(id, out var prompt)) { Error("That question is no longer waiting."); return; }
        if (WebProtocol.ParseAnswer(prompt, m) is not { } answer) { Error("That is not an answer this question offers."); return; }
        S.Prompts.Answer(id, answer, _who);
    }

    private async Task CloseSessionAsync(string? sessionId, bool confirm)
    {
        if (Live(sessionId) is not { } session) { Error("No such open session."); return; }
        if (session.IsBusy)
        {
            if (!confirm)
            {
                Send(new JsonObject { ["type"] = "confirmClose", ["sessionId"] = session.Id, ["text"] = "A turn is running. Close anyway?" });
                return;
            }
            session.CancelTurn();
            for (var i = 0; i < 100 && session.IsBusy; i++) await Task.Delay(100).ConfigureAwait(false);
        }
        Leave(session.Id);
        if (await _hub.UnloadAsync(session).ConfigureAwait(false))
            Send(new JsonObject { ["type"] = "closed", ["sessionId"] = session.Id });
        else
            Error("The session would not close — a turn is still finishing.");
    }

    // ---- plumbing ------------------------------------------------------------------------------------------------

    /// <summary>Work run beside the message loop, its failure said to the tab rather than lost.</summary>
    private Task Beside(Func<Task> work) => Task.Run(async () =>
    {
        try { await work().ConfigureAwait(false); }
        catch (Exception e) when (e is not OutOfMemoryException) { Error(e.Message); }
    });

    private Session? Live(string? sessionId) =>
        sessionId is null || !_open.ContainsKey(sessionId) ? null : S.Manager.Sessions.FirstOrDefault(s => s.Id == sessionId);

    private void Notice(string sessionId, string text, string severity) =>
        Send(new JsonObject { ["type"] = "notice", ["sessionId"] = sessionId, ["text"] = text, ["severity"] = severity });

    private void Error(string message) => Send(new JsonObject { ["type"] = "error", ["message"] = message });

    private static string? Str(JsonObject m, string key) =>
        m[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Long(JsonObject m, string key) =>
        m[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    // A MESSAGE TOO LARGE TO TAKE, read to its end and dropped — the tab is told, and keeps its connection.
    private static readonly string TooLarge = new('\0', 1);

    private async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var all = new MemoryStream();
        while (true)
        {
            var r = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            all.Write(buffer, 0, r.Count);

            // TOO LARGE: the rest of it is read and thrown away, so the next message starts where it should.
            if (all.Length > MaxIncoming)
            {
                while (!r.EndOfMessage)
                {
                    r = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return null;
                }
                return TooLarge;
            }
            if (r.EndOfMessage) return Encoding.UTF8.GetString(all.ToArray());
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var text in _out.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            Gone();
        }
    }
}

/// <summary>The messages the server builds, and how a tab's answer is read back into one a prompt offered.</summary>
internal static class WebProtocol
{
    public static JsonObject Opened(Session session) => new()
    {
        ["type"] = "opened",
        ["sessionId"] = session.Id,
        ["agentId"] = session.SessionId,
        ["folder"] = session.WorkingDirectory,
        ["model"] = session.Provider?.ModelId,
        ["mode"] = session.Mode.Edits.ToString(),
        ["agentMode"] = session.Mode.Agent.ToString(),
        ["sandbox"] = session.Policy?.Sandbox?.Status.Kind.ToString(),
    };

    public static JsonObject Prompt(PendingPrompt prompt) => prompt switch
    {
        PermissionPending p => new JsonObject
        {
            ["type"] = "prompt",
            ["id"] = p.Id,
            ["sessionId"] = p.SessionId,
            ["kind"] = "permission",

            // THE TERMINAL'S OWN WORDS for this request — which kind, inside or outside the folder, the sandbox's
            // two special questions — so a browser asks exactly what the terminal would.
            ["heading"] = CxAgent.UI.PermissionPromptControl.HeadingFor(p.Request, p.OfferTrust),
            ["what"] = p.Request.Kind.ToString(),
            ["display"] = p.Request.Display,
            ["requester"] = p.Request.Requester,
            ["offerAlways"] = p.Request.AlwaysRule is not null,
            ["alwaysCovers"] = p.Request.AlwaysRule,
            ["offerTrust"] = p.OfferTrust,
            ["sandboxFailure"] = p.Request.SandboxFailure,
            ["unsandboxedReason"] = p.Request.UnsandboxedReason,
        },
        QuestionPending q => new JsonObject
        {
            ["type"] = "prompt",
            ["id"] = q.Id,
            ["sessionId"] = q.SessionId,
            ["kind"] = "question",
            ["questions"] = new JsonArray([.. q.Questions.Select(Question)]),
        },
        TrustPending t => new JsonObject
        {
            ["type"] = "prompt",
            ["id"] = t.Id,
            ["sessionId"] = t.SessionId,
            ["kind"] = "trust",
            ["folder"] = t.Folder,
            // THE TERMINAL'S WORDS AND ORDER, from TrustQuestion: the cautious answer first.
            ["lines"] = new JsonArray([.. CxAgent.UI.TrustQuestion.Lines.Select(l => (JsonNode)l)]),
            ["answers"] = new JsonArray([.. CxAgent.UI.TrustQuestion.Answers.Select((a, i) => (JsonNode)new JsonObject { ["label"] = a, ["trusted"] = i == 1 })]),
        },
        _ => new JsonObject { ["type"] = "prompt", ["id"] = prompt.Id, ["sessionId"] = prompt.SessionId, ["kind"] = "unknown" },
    };

    private static JsonNode Question(UserQuestion q) => new JsonObject
    {
        ["question"] = q.Question,
        ["header"] = q.Header,
        ["multiple"] = q.Multiple,
        ["options"] = new JsonArray([.. q.Choices.Select(o => (JsonNode)new JsonObject { ["label"] = o.Label, ["description"] = o.Description })]),
    };

    /// <summary>
    /// The answer a tab sent, as the prompt's own kind — or null when it is not one the prompt offered. A tab cannot
    /// choose "always" on a prompt that showed no such button, nor trust a folder it was not asked about.
    /// </summary>
    public static PromptAnswer? ParseAnswer(PendingPrompt prompt, JsonObject m)
    {
        switch (prompt)
        {
            case PermissionPending p:
                return (m["choice"] as JsonValue)?.TryGetValue<string>(out var c) == true ? c switch
                {
                    "once" => new PermissionAnswer(PermissionChoice.Once),
                    "deny" => new PermissionAnswer(PermissionChoice.Deny),
                    "always" when p.Request.AlwaysRule is not null => new PermissionAnswer(PermissionChoice.Always),
                    "trust" when p.OfferTrust => new PermissionAnswer(PermissionChoice.TrustFolder),
                    _ => null,
                } : null;
            case QuestionPending:
                if (m["cancelled"] is JsonValue cancelled && cancelled.TryGetValue<bool>(out var gaveUp) && gaveUp)
                    return new QuestionAnswer(QuestionAnswers.Cancel);
                if (m["answers"] is not JsonArray answers || answers.Any(a => a is not JsonValue v || !v.TryGetValue<string>(out _)))
                    return null;
                return new QuestionAnswer(new QuestionAnswers([.. answers.Select(a => a!.GetValue<string>())]));
            case TrustPending:
                return m["trusted"] is JsonValue t && t.TryGetValue<bool>(out var yes) ? new TrustAnswer(yes) : null;
            default:
                return null;
        }
    }
}
