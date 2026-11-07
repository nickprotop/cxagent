using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using CxAgent.Hosting;
using CxAgent.Web;
using CxAgent.Web.Http;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Browser tabs over a real WebSocket: the session list, opening and creating within the roots, streaming a
/// turn, prompts, closing, and unloading what nobody is looking at.</summary>
public sealed partial class WebSessionHubTests : IAsyncLifetime
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly string _root = Directory.CreateTempSubdirectory("hub-").FullName;
    private string Projects => Path.Combine(_root, "projects");
    private string Work => Path.Combine(Projects, "work");
    private string Outside => Path.Combine(_root, "outside");
    private readonly Clock _clock = new();
    private MockLlmProvider _model = null!;
    private AppPaths _paths = null!;
    private SessionManager _manager = null!;
    private PromptHub _prompts = null!;
    private WebSessionHub _hub = null!;
    private HttpServer _server = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Work);
        Directory.CreateDirectory(Outside);
        _paths = new AppPaths(Path.Combine(_root, "cfg"));
        _paths.EnsureCreated();
        _manager = SessionManager.Create(_paths);
        _manager.Holder = LockHolder.Web;
        _prompts = new PromptHub();
        var rules = new PermissionRulesStore(_paths);
        rules.SetTrust(Work, TrustState.Trusted);
        var composer = new SessionComposer(new ComposerServices(_manager, _paths, rules, null, null) { Prompts = _prompts });
        _model = new MockLlmProvider("m");
        // MEMORY ON, as the app has it by default — so the browser's /memory and its panel rows have notes to show.
        var resolved = ResolvedConfig.ForTesting(_model);
        var opener = new ComposerSessionOpener(composer, resolved with { Catalog = resolved.Catalog with { Memory = true } }, AgentMode.Single);
        _hub = new WebSessionHub(new WebHubServices(_manager, _prompts, new FolderSessionStore(_paths), _paths, [Projects], opener, _clock)
        {
            IdleMinutes = 30,
        });
        _server = new HttpServer(new HttpServerOptions(IPAddress.Loopback, 0), async x =>
        {
            if (await WebSocketUpgrade.AcceptAsync(x) is { } ws) await _hub.ServeAsync(ws, "tester", CancellationToken.None);
        });
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        _manager.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    private sealed class Tab : IAsyncDisposable
    {
        private readonly ClientWebSocket _ws = new();
        private readonly List<JsonObject> _seen = [];
        private int _read;

        public static async Task<Tab> OpenAsync(int port)
        {
            var tab = new Tab();
            tab._ws.Options.SetRequestHeader("Origin", $"http://localhost:{port}");
            await tab._ws.ConnectAsync(new Uri($"ws://localhost:{port}/ws"), CancellationToken.None);
            return tab;
        }

        public Task SendAsync(JsonObject message) =>
            _ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

        /// <summary>The next message (after those already taken) matching <paramref name="match"/>.</summary>
        public async Task<JsonObject> NextAsync(Func<JsonObject, bool> match, int seconds = 10)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            while (true)
            {
                for (; _read < _seen.Count; _read++)
                    if (match(_seen[_read])) return _seen[_read++];
                _seen.Add(await ReceiveAsync(timeout.Token));
            }
        }

        public List<JsonObject> Seen => _seen;

        private async Task<JsonObject> ReceiveAsync(CancellationToken ct)
        {
            var buffer = new byte[1 << 16];
            using var all = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(buffer, ct);
                all.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);
            return JsonNode.Parse(all.ToArray())!.AsObject();
        }

        public void Abort() => _ws.Abort();

        public async ValueTask DisposeAsync()
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch (Exception) { }
            _ws.Dispose();
        }
    }

    private static Func<JsonObject, bool> Type(string type) => m => m["type"]?.GetValue<string>() == type;

    private static Func<JsonObject, bool> Kind(string kind) =>
        m => m["type"]?.GetValue<string>() == "event" && m["kind"]?.GetValue<string>() == kind;

    private static JsonObject Msg(string type, params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject { ["type"] = type };
        foreach (var (k, v) in fields) o[k] = v;
        return o;
    }

    // A TURN CAN OPEN AND CLOSE A ROUND BEFORE THE ONE THAT CARRIES THE REPLY, so "done" is the reply, then its end.
    private static async Task<JsonObject> TurnDoneAsync(Tab tab)
    {
        await tab.NextAsync(Kind("text"));
        return await tab.NextAsync(Kind("turnEnded"));
    }

    private async Task<(Tab Tab, string SessionId)> CreatedAsync()
    {
        var tab = await Tab.OpenAsync(_server.Bound.Port);
        await tab.SendAsync(Msg("create", ("folder", Work)));
        var opened = await tab.NextAsync(Type("opened"));
        return (tab, opened["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task List_shows_only_sessions_under_roots_and_hides_superseded()
    {
        var store = new FolderSessionStore(_paths);
        store.SaveTurn(new FolderSessionStore.ResumeTurn("INSIDE", [new ChatMessage { Role = "user", Content = "a" }], 1, 1, Work));
        store.SaveTurn(new FolderSessionStore.ResumeTurn("OUTSIDE", [new ChatMessage { Role = "user", Content = "b" }], 1, 1, Outside));
        store.SaveTurn(new FolderSessionStore.ResumeTurn("OLD", [new ChatMessage { Role = "user", Content = "c" }], 1, 1, Work));
        store.MarkSuperseded("OLD");

        await using var tab = await Tab.OpenAsync(_server.Bound.Port);
        await tab.SendAsync(Msg("list"));
        var rows = (await tab.NextAsync(Type("sessions")))["rows"]!.AsArray().Select(r => r!["agentId"]!.GetValue<string>()).ToList();

        Assert.Contains("INSIDE", rows);
        Assert.DoesNotContain("OUTSIDE", rows);
        Assert.DoesNotContain("OLD", rows);
    }

    [Fact]
    public async Task Held_session_row_says_who_holds_it()
    {
        new FolderSessionStore(_paths).SaveTurn(new FolderSessionStore.ResumeTurn("HELD", [new ChatMessage { Role = "user", Content = "a" }], 1, 1, Work));
        using var claim = SessionLock.TryAcquire(_paths.LocksDir, "HELD", LockHolder.Terminal);

        await using var tab = await Tab.OpenAsync(_server.Bound.Port);
        await tab.SendAsync(Msg("list"));
        var row = (await tab.NextAsync(Type("sessions")))["rows"]!.AsArray().Single(r => r!["agentId"]!.GetValue<string>() == "HELD")!;
        Assert.Equal("held", row["state"]!.GetValue<string>());

        await tab.SendAsync(Msg("open", ("id", "HELD")));
        Assert.Contains("already open in this window", (await tab.NextAsync(Type("error")))["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_outside_roots_is_refused()
    {
        await using var tab = await Tab.OpenAsync(_server.Bound.Port);
        await tab.SendAsync(Msg("create", ("folder", Outside)));
        Assert.Contains("outside", (await tab.NextAsync(Type("error")))["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_manager.Sessions);
    }

    [Fact]
    public async Task Symlink_out_of_roots_is_refused()
    {
        var link = Path.Combine(Projects, "link");
        Directory.CreateSymbolicLink(link, Outside);
        Assert.False(WebSessionHub.InsideRoots(link, [Projects]));
        Assert.False(WebSessionHub.InsideRoots(Path.Combine(link, "x"), [Projects]));
        Assert.True(WebSessionHub.InsideRoots(Work, [Projects]));
        Assert.False(WebSessionHub.InsideRoots(Projects + "-sibling", [Projects]));

        await using var tab = await Tab.OpenAsync(_server.Bound.Port);
        await tab.SendAsync(Msg("create", ("folder", link)));
        await tab.NextAsync(Type("error"));
        Assert.Empty(_manager.Sessions);
    }

    [Fact]
    public async Task Folders_lists_children_under_roots_only()
    {
        await using var tab = await Tab.OpenAsync(_server.Bound.Port);
        await tab.SendAsync(Msg("folders"));
        var roots = (await tab.NextAsync(Type("folders")))["folders"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();
        Assert.Equal([Path.GetFullPath(Projects)], roots);

        await tab.SendAsync(Msg("folders", ("under", Projects)));
        var children = (await tab.NextAsync(Type("folders")))["folders"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();
        Assert.Contains(Path.GetFullPath(Work), children);

        await tab.SendAsync(Msg("folders", ("under", Outside)));
        await tab.NextAsync(Type("error"));
    }

    [Fact]
    public async Task Clear_tells_every_tab_to_empty_the_transcript_as_the_terminal_does()
    {
        _model.EnqueueResponse(new LlmResponse { Text = "hello there", StopReason = "end_turn" });
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "hi")));
        await TurnDoneAsync(tab);

        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/clear")));
        await tab.NextAsync(Kind("cleared"));
    }

    [Fact]
    public async Task Say_streams_events_in_order_with_increasing_seq()
    {
        _model.EnqueueResponse(new LlmResponse { Text = "hello there", StopReason = "end_turn" });
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;

        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "hi")));
        await TurnDoneAsync(tab);

        var events = tab.Seen.Where(m => m["type"]?.GetValue<string>() == "event").ToList();
        var seqs = events.Select(e => e["seq"]!.GetValue<long>()).ToList();
        Assert.Equal(seqs.OrderBy(s => s), seqs);
        Assert.Equal(seqs.Count, seqs.Distinct().Count());
        Assert.Contains(events, e => e["kind"]!.GetValue<string>() == "user" && e["text"]!.GetValue<string>() == "hi");
        Assert.Contains("hello there", string.Concat(events.Where(e => e["kind"]!.GetValue<string>() == "text").Select(e => e["text"]!.GetValue<string>())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_tabs_on_one_session_both_receive_events_and_a_late_one_gets_the_past()
    {
        _model.EnqueueResponse(new LlmResponse { Text = "first", StopReason = "end_turn" });
        var (a, id) = await CreatedAsync();
        await using var _a = a;
        await using var b = await Tab.OpenAsync(_server.Bound.Port);
        await b.SendAsync(Msg("open", ("id", id)));
        await b.NextAsync(Type("opened"));

        await a.SendAsync(Msg("say", ("sessionId", id), ("text", "hi")));
        await TurnDoneAsync(a);
        await TurnDoneAsync(b);

        await using var late = await Tab.OpenAsync(_server.Bound.Port);
        await late.SendAsync(Msg("open", ("id", id)));
        await late.NextAsync(Type("opened"));
        var user = await late.NextAsync(Kind("user"));
        Assert.Equal("hi", user["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Since_after_a_drop_replays_only_what_was_missed()
    {
        _model.EnqueueResponse(new LlmResponse { Text = "one", StopReason = "end_turn" });
        _model.EnqueueResponse(new LlmResponse { Text = "two", StopReason = "end_turn" });
        var (a, id) = await CreatedAsync();
        await a.SendAsync(Msg("say", ("sessionId", id), ("text", "first")));
        var last = (await TurnDoneAsync(a))["seq"]!.GetValue<long>();
        a.Abort();

        await using var b = await Tab.OpenAsync(_server.Bound.Port);
        await b.SendAsync(Msg("open", ("id", id), ("since", last)));
        await b.NextAsync(Type("opened"));
        await b.SendAsync(Msg("say", ("sessionId", id), ("text", "second")));
        await TurnDoneAsync(b);

        var seqs = b.Seen.Where(m => m["type"]?.GetValue<string>() == "event").Select(e => e["seq"]!.GetValue<long>()).ToList();
        Assert.True(seqs.Min() > last);
        Assert.Equal(seqs.Count, seqs.Distinct().Count());
    }

    [Fact]
    public async Task Prompt_shows_in_tab_and_answer_resolves_it()
    {
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;

        var asked = _prompts.AskTrustAsync(id, Work, CancellationToken.None);
        var prompt = await tab.NextAsync(Type("prompt"));
        Assert.Equal("trust", prompt["kind"]!.GetValue<string>());

        await tab.SendAsync(Msg("answer", ("promptId", prompt["id"]!.GetValue<string>()), ("trusted", true)));
        Assert.True(await asked.WaitAsync(TimeSpan.FromSeconds(5)));
        var resolved = await tab.NextAsync(Type("promptResolved"));
        Assert.Equal("tester", resolved["by"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_permission_answer_cannot_choose_a_button_the_prompt_did_not_offer()
    {
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        var policy = new PermissionPolicy(Work, new PermissionRulesStore(_paths)) { SessionId = id };
        var asked = _prompts.AskPermissionAsync(new PermissionRequest(PermissionKind.Shell, "rm x", AlwaysRule: null) { Policy = policy },
            offerTrust: false, CancellationToken.None);
        var prompt = await tab.NextAsync(Type("prompt"));
        Assert.False(prompt["offerAlways"]!.GetValue<bool>());

        // THE TERMINAL'S OWN HEADING, so both front ends ask the same question in the same words.
        Assert.Equal(CxAgent.UI.PermissionPromptControl.HeadingFor(new PermissionRequest(PermissionKind.Shell, "rm x", null) { Policy = policy }),
            prompt["heading"]!.GetValue<string>());

        await tab.SendAsync(Msg("answer", ("promptId", prompt["id"]!.GetValue<string>()), ("choice", "always")));
        await tab.NextAsync(Type("error"));
        Assert.False(asked.IsCompleted);

        await tab.SendAsync(Msg("answer", ("promptId", prompt["id"]!.GetValue<string>()), ("choice", "once")));
        Assert.Equal(PermissionChoice.Once, await asked.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Close_releases_the_lock_and_the_row_becomes_recent()
    {
        _model.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "hi")));
        await TurnDoneAsync(tab);
        for (var i = 0; i < 50 && _manager.Sessions.Single().IsBusy; i++) await Task.Delay(50);
        var agent = _manager.Sessions.Single().SessionId!;

        await tab.SendAsync(Msg("close", ("sessionId", id)));
        await tab.NextAsync(Type("closed"));

        Assert.Empty(_manager.Sessions);
        Assert.Null(SessionLock.Probe(_paths.LocksDir, agent));
    }

    [Fact]
    public async Task Idle_session_unloads_after_idle_minutes()
    {
        var (tab, _) = await CreatedAsync();
        await tab.DisposeAsync();
        await Task.Delay(200);

        _hub.CheckIdle();
        Assert.Single(_manager.Sessions);

        _clock.Now += TimeSpan.FromMinutes(31);
        _hub.CheckIdle();
        await Task.Delay(200);
        Assert.Empty(_manager.Sessions);
    }

    [Fact]
    public async Task Two_tabs_resuming_one_session_make_one_live_session_and_retire_the_old_row()
    {
        new FolderSessionStore(_paths).SaveTurn(new FolderSessionStore.ResumeTurn("SAVED", [new ChatMessage { Role = "user", Content = "a" }], 1, 1, Work));
        await using var a = await Tab.OpenAsync(_server.Bound.Port);
        await using var b = await Tab.OpenAsync(_server.Bound.Port);

        await Task.WhenAll(a.SendAsync(Msg("open", ("id", "SAVED"))), b.SendAsync(Msg("open", ("id", "SAVED"))));
        await a.NextAsync(m => m["type"]?.GetValue<string>() is "opened" or "error");
        await b.NextAsync(m => m["type"]?.GetValue<string>() is "opened" or "error");

        Assert.Single(_manager.Sessions);
        await a.SendAsync(Msg("list"));
        var rows = (await a.NextAsync(m => m["type"]?.GetValue<string>() == "sessions" && m["rows"]!.AsArray().Count > 0))["rows"]!.AsArray();
        Assert.DoesNotContain(rows, r => r!["agentId"]!.GetValue<string>() == "SAVED");
        Assert.Single(rows);
    }

    [Fact]
    public async Task A_session_opened_and_closed_without_a_turn_is_still_listed()
    {
        // OPENING RETIRES THE ROW IT CONTINUES, so the continuation must be saved at once: closed before a turn, it
        // would otherwise leave neither row listed and the conversation unreachable.
        new FolderSessionStore(_paths).SaveTurn(new FolderSessionStore.ResumeTurn("SAVED", [new ChatMessage { Role = "user", Content = "a" }], 1, 1, Work));
        await using var tab = await Tab.OpenAsync(_server.Bound.Port);
        await tab.SendAsync(Msg("open", ("id", "SAVED")));
        await tab.NextAsync(Type("opened"));
        var successor = _manager.Sessions.Single().SessionId;
        Assert.True(_manager.Close(_manager.Sessions.Single()));

        await tab.SendAsync(Msg("list"));
        var rows = (await tab.NextAsync(m => m["type"]?.GetValue<string>() == "sessions" && m["rows"]!.AsArray().Count > 0))["rows"]!.AsArray();
        var row = Assert.Single(rows)!;
        Assert.Equal(successor, row["agentId"]!.GetValue<string>());
        Assert.Equal("a", row["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_session_closed_elsewhere_tells_its_tabs()
    {
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        var session = _manager.Sessions.Single();

        Assert.True(_manager.Close(session));

        var closed = await tab.NextAsync(Type("closed"));
        Assert.Equal(id, closed["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Help_with_arguments_is_still_the_web_help()
    {
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/help model")));
        Assert.Contains("/compress", (await tab.NextAsync(Type("notice")))["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reopening_a_session_that_no_longer_exists_says_it_is_gone()
    {
        await using var tab = await Tab.OpenAsync(_server.Bound.Port);
        await tab.SendAsync(Msg("open", ("id", "NOPE"), ("since", 41)));
        Assert.Equal("NOPE", (await tab.NextAsync(Type("gone")))["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Opening_a_session_sends_its_panel()
    {
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        var panel = await tab.NextAsync(Type("panel"));
        Assert.Equal(id, panel["sessionId"]!.GetValue<string>());
        Assert.NotNull(panel["tokens"]);
        Assert.NotNull(panel["choices"]!["modes"]);
        Assert.Equal(Work, panel["here"]!["folder"]!.GetValue<string>());
    }

    [Fact]
    // "auto" is offered only with a classifier configured, and models only with a catalog; this host has neither.
    public async Task The_panel_offers_the_models_and_edit_modes_to_pick_from()
    {
        var (tab, _) = await CreatedAsync();
        await using var __ = tab;
        var choices = (await tab.NextAsync(Type("panel")))["choices"]!;
        var modes = choices["modes"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("always-ask", modes);
        Assert.Contains("accept-edits", modes);
    }

    [Fact]
    public async Task The_panel_follows_a_turn()
    {
        _model.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn", Usage = new LlmUsage { InputTokens = 1200, OutputTokens = 30 } });
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.NextAsync(Type("panel"));
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "hi")));
        await TurnDoneAsync(tab);
        _hub.PushPanels();
        var panel = await tab.NextAsync(m => m["type"]?.GetValue<string>() == "panel" && m["session"]!["turns"]!.GetValue<int>() == 1);
        Assert.True(panel["tokens"]!["total"]!.GetValue<int>() > 0);
    }

    [Fact]
    public async Task The_sandbox_switch_changes_this_session_only_and_writes_nothing()
    {
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        var session = _manager.Sessions.Single();
        var before = File.Exists(Path.Combine(_paths.ConfigDir, "config.json")) ? File.ReadAllText(Path.Combine(_paths.ConfigDir, "config.json")) : null;

        await tab.SendAsync(Msg("setSandbox", ("sessionId", id), ("enabled", false)));
        await tab.NextAsync(Type("notice"));
        Assert.False(session.Policy!.Sandbox!.Settings.Enabled);

        await tab.SendAsync(Msg("setSandbox", ("sessionId", id), ("enabled", true)));
        await tab.NextAsync(m => m["type"]?.GetValue<string>() == "notice" && m["text"]!.GetValue<string>().Contains("on", StringComparison.Ordinal));
        Assert.True(session.Policy!.Sandbox!.Settings.Enabled);
        Assert.Equal(before, File.Exists(Path.Combine(_paths.ConfigDir, "config.json")) ? File.ReadAllText(Path.Combine(_paths.ConfigDir, "config.json")) : null);
    }

    [Fact]
    public async Task Allowed_command_runs_and_others_are_refused()
    {
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;

        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/sandbox on")));
        Assert.Contains("not available on the web", (await tab.NextAsync(Type("notice")))["text"]!.GetValue<string>(), StringComparison.Ordinal);

        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/help")));
        var help = (await tab.NextAsync(Type("notice")))["text"]!.GetValue<string>();
        Assert.Contains("/compress", help, StringComparison.Ordinal);
        Assert.DoesNotContain("/shell", help, StringComparison.Ordinal);

        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/sandbox")));
        await tab.NextAsync(Kind("said"));
    }
}
