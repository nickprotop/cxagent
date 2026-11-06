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
    private const int MaxIncoming = 64 * 1024;
    private const int MaxQueued = 10_000;

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

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _gone.Token);
        var sending = SendLoopAsync(linked.Token);
        Send(new JsonObject
        {
            ["type"] = "hello",
            ["user"] = _who,
            ["login"] = Token is not null,
            ["banners"] = new JsonArray([.. _hub.Banners.Select(b => (JsonNode)b)]),
            ["commands"] = new JsonArray([.. WebCommands.Allowed.Select(c => (JsonNode)c)]),
        });
        Send(new JsonObject { ["type"] = "sessions", ["rows"] = _hub.Rows() });
        try
        {
            while (_socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                if (await ReceiveAsync(linked.Token).ConfigureAwait(false) is not { } text) break;
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
            await _gone.CancelAsync().ConfigureAwait(false);
            try { await sending.ConfigureAwait(false); } catch (Exception) { }
        }
    }

    public Task CloseAsync()
    {
        _gone.Cancel();
        try { _socket.Abort(); } catch (Exception) { }
        return Task.CompletedTask;
    }

    public void Send(JsonObject message)
    {
        // FULL MEANS THE TAB STOPPED READING: let it go, and it comes back with "since".
        if (!_out.Writer.TryWrite(message.ToJsonString())) _gone.Cancel();
    }

    public void Dispose()
    {
        foreach (var sub in _open.Values) sub.Dispose();
        _open.Clear();
        _gone.Dispose();
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
                Say(Str(m, "sessionId"), Str(m, "text"));
                break;
            case "answer":
                Answer(m);
                break;
            case "stop":
                Live(Str(m, "sessionId"))?.CancelTurn();
                break;
            case "close":
                await CloseSessionAsync(Str(m, "sessionId"), m["confirm"]?.GetValue<bool>() == true).ConfigureAwait(false);
                break;
            case "leave":
                Leave(Str(m, "sessionId"));
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
        if (!WebSessionHub.InsideRoots(full, S.Roots)) { Error("That folder is outside the folders this server opens."); return; }
        var session = S.Opener.Create(full);
        Attach(session, 0, null);
        _hub.ListChanged();
    }

    private void Open(string? id, long since)
    {
        if (id is null) { Error("Which session?"); return; }
        if (S.Manager.Sessions.FirstOrDefault(s => s.Id == id || s.SessionId == id) is { } live)
        {
            if (!WebSessionHub.InsideRoots(live.WorkingDirectory, S.Roots)) { Error("That session is outside the folders this server opens."); return; }
            Attach(live, since, null);
            return;
        }

        var snapshot = S.Store.LoadById(id);
        if (snapshot is null) { Error("No such session."); return; }
        if (snapshot.WorkingDir is not { } dir || !WebSessionHub.InsideRoots(dir, S.Roots))
        {
            Error("That session is outside the folders this server opens.");
            return;
        }
        if (S.Store.HolderOf(id) is { } held) { Error($"That session is {SessionLock.HeldMessage(held)}."); return; }

        var session = S.Opener.Resume(snapshot);
        Attach(session, 0, snapshot.AgentId);
        _hub.ListChanged();
    }

    private void Attach(Session session, long since, string? seedAgent)
    {
        var log = _hub.LogFor(session, seedAgent);
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
        foreach (var prompt in S.Prompts.PendingFor(session.Id)) Show(prompt, CancellationToken.None);
    }

    private void Leave(string? sessionId)
    {
        if (sessionId is not null && _open.TryRemove(sessionId, out var sub)) sub.Dispose();
    }

    private void Say(string? sessionId, string? text)
    {
        if (Live(sessionId) is not { } session || string.IsNullOrWhiteSpace(text)) { Error("Nothing to send."); return; }
        if (text.TrimStart().StartsWith('/'))
        {
            if (WebCommands.Refusal(text) is { } refused) { Notice(session.Id, refused, "Warning"); return; }
            if (WebCommands.IsHelp(text)) { Notice(session.Id, WebCommands.Help(), "Info"); return; }
        }
        switch (session.Submit(text))
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

    private Session? Live(string? sessionId) =>
        sessionId is null || !_open.ContainsKey(sessionId) ? null : S.Manager.Sessions.FirstOrDefault(s => s.Id == sessionId);

    private void Notice(string sessionId, string text, string severity) =>
        Send(new JsonObject { ["type"] = "notice", ["sessionId"] = sessionId, ["text"] = text, ["severity"] = severity });

    private void Error(string message) => Send(new JsonObject { ["type"] = "error", ["message"] = message });

    private static string? Str(JsonObject m, string key) =>
        m[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Long(JsonObject m, string key) =>
        m[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    private async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var all = new MemoryStream();
        while (true)
        {
            var r = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            all.Write(buffer, 0, r.Count);

            // A MESSAGE THIS LARGE IS NOT ONE THIS PAGE SENDS.
            if (all.Length > MaxIncoming) return null;
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
            _gone.Cancel();
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
