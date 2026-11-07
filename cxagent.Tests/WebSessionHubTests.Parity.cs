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

/// <summary>
/// The browser doing what the terminal does: the commands it may now run, the thinking level, asides, images, an
/// export, and a background command's output.
/// </summary>
public sealed partial class WebSessionHubTests
{
    private static LlmResponse Reply(string text) => new() { Text = text, StopReason = "end_turn" };

    // A 1×1 PNG, as a browser's paste hands one over.
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Theory]
    [InlineData("/think")]
    [InlineData("/btw")]
    [InlineData("/agents")]
    [InlineData("/skills")]
    [InlineData("/commands")]
    [InlineData("/mcp")]
    [InlineData("/hooks")]
    [InlineData("/tasks")]
    [InlineData("/sessions")]
    public void The_terminal_s_own_commands_are_allowed_in_the_browser(string command) =>
        Assert.Null(CxAgent.Web.WebCommands.Refusal(command));

    [Fact]
    public void A_session_list_is_allowed_but_resuming_from_it_is_not()
    {
        // THE SIDEBAR RESUMES. "/sessions resume 2" typed in a tab would swap the conversation under every other tab
        // that has it open, which the sidebar's open never does.
        Assert.NotNull(CxAgent.Web.WebCommands.Refusal("/sessions resume 2"));
    }

    [Fact]
    public async Task The_panel_says_how_hard_the_model_thinks_and_whether_an_aside_is_open()
    {
        var (tab, _) = await CreatedAsync();
        await using var __ = tab;
        var panel = await tab.NextAsync(Type("panel"));
        Assert.NotNull(panel["thinking"]!["levels"]);
        Assert.False(panel["aside"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_btw_answer_reaches_the_browser_and_leaving_ends_the_aside()
    {
        _model.EnqueueResponse(Reply("main answer"));
        _model.EnqueueResponse(Reply("the aside's answer"));
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "hi")));
        await TurnDoneAsync(tab);

        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/btw what did you say?")));
        Assert.Equal("what did you say?", (await tab.NextAsync(Kind("asideAsked")))["text"]!.GetValue<string>());
        Assert.Contains("the aside's answer", (await tab.NextAsync(Kind("asideText")))["text"]!.GetValue<string>(), StringComparison.Ordinal);
        await tab.NextAsync(Kind("asideEnded"));
        var session = _manager.Sessions.Single();
        Assert.True(session.InAside);

        await tab.SendAsync(Msg("leaveAside", ("sessionId", id)));
        await tab.NextAsync(m => m["type"]?.GetValue<string>() == "panel" && m["aside"]!.GetValue<bool>() == false);
        Assert.False(session.InAside);
    }

    [Fact]
    public async Task A_pasted_image_goes_with_the_message_and_is_shown_on_it()
    {
        _model.EnqueueResponse(Reply("I see it"));
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "what is this?"),
            ("images", new JsonArray(new JsonObject { ["name"] = "shot.png", ["data"] = OnePixelPng }))));

        var shown = await tab.NextAsync(Kind("images"));
        var image = shown["images"]!.AsArray().Single()!;
        Assert.StartsWith("[Image #1 1×1]", image["label"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("data:image/png;base64,", image["src"]!.GetValue<string>(), StringComparison.Ordinal);
        await TurnDoneAsync(tab);
        Assert.Contains(_model.LastMessages!, m => m.Images is { Count: 1 });
    }

    [Fact]
    public async Task An_image_that_is_not_one_is_refused_and_nothing_is_sent()
    {
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "what is this?"),
            ("images", new JsonArray(new JsonObject { ["name"] = "x.png", ["data"] = Convert.ToBase64String("not a png"u8.ToArray()) }))));
        await tab.NextAsync(Type("notice"));
        Assert.Equal(0, _model.ChatCallCount);
    }

    [Fact]
    public async Task Export_hands_the_browser_the_conversation_as_markdown()
    {
        _model.EnqueueResponse(Reply("the answer"));
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "the question")));
        await TurnDoneAsync(tab);

        await tab.SendAsync(Msg("export", ("sessionId", id), ("full", false)));
        var export = await tab.NextAsync(Type("export"));
        Assert.EndsWith(".md", export["name"]!.GetValue<string>(), StringComparison.Ordinal);
        var markdown = export["markdown"]!.GetValue<string>();
        Assert.Contains("the question", markdown, StringComparison.Ordinal);
        Assert.Contains("the answer", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_thinking_level_the_model_does_not_offer_is_said_not_set()
    {
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/think ultra")));
        await tab.NextAsync(Kind("said"));
    }

    // ---- the switches ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_command_behind_an_off_switch_is_refused_and_the_refusal_says_where_to_turn_it_on()
    {
        var refusal = CxAgent.Web.WebCommands.Refusal("/shell");
        Assert.NotNull(refusal);
        Assert.Contains("web.allow.shell", refusal, StringComparison.Ordinal);
        Assert.Contains("F8", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_switch_turned_on_lets_its_commands_through()
    {
        var all = new Core.Web.WebAccess(Shell: true, Config: true, Plugins: true, OpenAnywhere: true);
        Assert.Null(CxAgent.Web.WebCommands.Refusal("/shell", all));
        Assert.Null(CxAgent.Web.WebCommands.Refusal("/trust", all));
        Assert.Null(CxAgent.Web.WebCommands.Refusal("/think high --save", all));
    }

    [Fact]
    public void Plugins_are_on_by_default_and_config_is_not()
    {
        Assert.Null(CxAgent.Web.WebCommands.Refusal("/plugin"));
        Assert.NotNull(CxAgent.Web.WebCommands.Refusal("/trust"));
        Assert.NotNull(CxAgent.Web.WebCommands.Refusal("/think high --save"));
        Assert.Contains("/plugin", CxAgent.Web.WebCommands.AllowedWith(Core.Web.WebAccess.Default));
        Assert.DoesNotContain("/shell", CxAgent.Web.WebCommands.AllowedWith(Core.Web.WebAccess.Default));
    }

    [Fact]
    public async Task The_page_is_told_the_switches_and_the_roots()
    {
        await using var tab = await Tab.OpenAsync(_server.Bound.Port);
        var hello = await tab.NextAsync(Type("hello"));
        Assert.False(hello["access"]!["shell"]!.GetValue<bool>());
        Assert.True(hello["access"]!["plugins"]!.GetValue<bool>());
        Assert.Equal(Projects, hello["access"]!["roots"]!.AsArray().Single()!.GetValue<string>());
    }

    [Fact]
    public async Task Export_typed_as_a_command_downloads_too()
    {
        _model.EnqueueResponse(Reply("the answer"));
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "the question")));
        await TurnDoneAsync(tab);
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/export full")));
        Assert.Contains("the question", (await tab.NextAsync(Type("export")))["markdown"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // ---- the editor -----------------------------------------------------------------------------------------------

    private async Task<(Tab Tab, string SessionId, JsonObject File)> OpenedFileAsync(string name, string content)
    {
        File.WriteAllText(Path.Combine(Work, name), content);
        var (tab, id) = await CreatedAsync();
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/open " + name)));
        return (tab, id, await tab.NextAsync(Type("openedFile")));
    }

    [Fact]
    public async Task Open_under_the_roots_sends_the_file_s_text_and_language()
    {
        var (tab, _, file) = await OpenedFileAsync("a.cs", "class A {}\n");
        await using var __ = tab;
        Assert.Equal("class A {}\n", file["text"]!.GetValue<string>());
        Assert.Equal(Path.Combine(Work, "a.cs"), file["path"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(file["language"]?.GetValue<string>()));
    }

    [Fact]
    public async Task Open_outside_the_roots_is_refused_and_names_the_switch()
    {
        var outside = Path.Combine(Outside, "secret.txt");
        File.WriteAllText(outside, "no");
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/open " + outside)));
        var notice = (await tab.NextAsync(Type("notice")))["text"]!.GetValue<string>();
        Assert.Contains("openAnywhere", notice, StringComparison.Ordinal);
        Assert.DoesNotContain(tab.Seen, m => m["type"]?.GetValue<string>() == "openedFile");
    }

    [Fact]
    public async Task Save_keeps_the_file_s_conventions_and_tells_the_model()
    {
        File.WriteAllBytes(Path.Combine(Work, "b.txt"), [0xEF, 0xBB, 0xBF, .. "one\r\ntwo\r\n"u8.ToArray()]);
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/open b.txt")));
        var key = (await tab.NextAsync(Type("openedFile")))["sessionId"]!.GetValue<string>();

        await tab.SendAsync(Msg("saveFile", ("sessionId", key), ("text", "one\ntwo\nthree\n")));
        await tab.NextAsync(Type("fileSaved"));
        var bytes = File.ReadAllBytes(Path.Combine(Work, "b.txt"));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("one\r\ntwo\r\nthree\r\n", System.Text.Encoding.UTF8.GetString(bytes[3..]));

        _model.EnqueueResponse(Reply("noted"));
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "go on")));
        await TurnDoneAsync(tab);
        Assert.Contains(_model.LastMessages!, m => m.Content.Contains("the user edited b.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_over_a_change_made_on_disk_asks_first_and_a_forced_save_writes()
    {
        var (tab, _, file) = await OpenedFileAsync("c.txt", "mine\n");
        await using var __ = tab;
        var key = file["sessionId"]!.GetValue<string>();
        File.WriteAllText(Path.Combine(Work, "c.txt"), "the agent's\n");

        await tab.SendAsync(Msg("saveFile", ("sessionId", key), ("text", "mine, edited\n")));
        Assert.Equal("the agent's\n", (await tab.NextAsync(Type("fileConflict")))["text"]!.GetValue<string>());
        Assert.Equal("the agent's\n", File.ReadAllText(Path.Combine(Work, "c.txt")));

        await tab.SendAsync(Msg("saveFile", ("sessionId", key), ("text", "mine, edited\n"), ("force", true)));
        await tab.NextAsync(Type("fileSaved"));
        Assert.Equal("mine, edited\n", File.ReadAllText(Path.Combine(Work, "c.txt")));
    }

    [Fact]
    public async Task A_change_made_on_disk_while_open_is_sent_to_the_tab()
    {
        var (tab, _, file) = await OpenedFileAsync("d.txt", "before\n");
        await using var __ = tab;
        File.WriteAllText(Path.Combine(Work, "d.txt"), "after\n");
        var changed = await tab.NextAsync(Type("fileChanged"));
        Assert.Equal(file["sessionId"]!.GetValue<string>(), changed["sessionId"]!.GetValue<string>());
        Assert.Equal("after\n", changed["text"]!.GetValue<string>());
    }

    // ---- the shell ------------------------------------------------------------------------------------------------

    /// <summary>A second server over the same sessions, with the switches <paramref name="allow"/> sets.</summary>
    private async Task<(HttpServer Server, WebSessionHub Hub)> ServerWithAsync(Core.Web.WebAccess allow)
    {
        var composer = new SessionComposer(new ComposerServices(_manager, _paths, new PermissionRulesStore(_paths), null, null) { Prompts = _prompts });
        var opener = new ComposerSessionOpener(composer, ResolvedConfig.ForTesting(_model), AgentMode.Single);
        var hub = new WebSessionHub(new WebHubServices(_manager, _prompts, new FolderSessionStore(_paths), _paths, [Projects], opener, _clock)
        {
            Allow = allow,
        });
        var server = new HttpServer(new HttpServerOptions(System.Net.IPAddress.Loopback, 0), async x =>
        {
            if (await WebSocketUpgrade.AcceptAsync(x) is { } ws) await hub.ServeAsync(ws, "tester", CancellationToken.None);
        });
        await server.StartAsync();
        return (server, hub);
    }

    [Fact]
    public async Task A_shell_is_refused_while_its_switch_is_off()
    {
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/shell")));
        Assert.Contains("web.allow.shell", (await tab.NextAsync(Type("notice")))["text"]!.GetValue<string>(), StringComparison.Ordinal);
        await tab.SendAsync(Msg("openShell", ("sessionId", id)));
        Assert.Contains("web.allow.shell", (await tab.NextAsync(Type("notice")))["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_its_switch_on_a_shell_runs_in_the_session_s_folder_and_answers_what_is_typed()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (server, _) = await ServerWithAsync(Core.Web.WebAccess.Default with { Shell = true });
        try
        {
            await using var tab = await Tab.OpenAsync(server.Bound.Port);
            await tab.SendAsync(Msg("create", ("folder", Work)));
            var id = (await tab.NextAsync(Type("opened")))["sessionId"]!.GetValue<string>();
            await tab.SendAsync(Msg("openShell", ("sessionId", id)));
            var key = (await tab.NextAsync(Type("openedShell")))["sessionId"]!.GetValue<string>();

            await tab.SendAsync(Msg("shellInput", ("sessionId", key), ("data", "echo shell-$((40+2)) && pwd\r")));
            var seen = new System.Text.StringBuilder();
            while (!seen.ToString().Contains("shell-42", StringComparison.Ordinal) || !seen.ToString().Contains(Work, StringComparison.Ordinal))
            {
                var output = await tab.NextAsync(Type("shellOutput"));
                seen.Append(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(output["data"]!.GetValue<string>())));
            }

            await tab.SendAsync(Msg("shellInput", ("sessionId", key), ("data", "exit 3\r")));
            Assert.Equal(3, (await tab.NextAsync(Type("shellExited")))["exitCode"]!.GetValue<int>());
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task What_the_shell_showed_reaches_the_model_with_the_next_message()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (server, _) = await ServerWithAsync(Core.Web.WebAccess.Default with { Shell = true });
        try
        {
            await using var tab = await Tab.OpenAsync(server.Bound.Port);
            await tab.SendAsync(Msg("create", ("folder", Work)));
            var id = (await tab.NextAsync(Type("opened")))["sessionId"]!.GetValue<string>();
            await tab.SendAsync(Msg("openShell", ("sessionId", id)));
            var key = (await tab.NextAsync(Type("openedShell")))["sessionId"]!.GetValue<string>();

            // THE PAGE HOLDS THE SCREEN, so it sends the lines it shows.
            await tab.SendAsync(Msg("shellReport", ("sessionId", key), ("lines", new JsonArray("$ make", "build ok")), ("exitCode", 0)));
            _model.EnqueueResponse(Reply("seen"));
            await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "what happened?")));
            await TurnDoneAsync(tab);
            Assert.Contains(_model.LastMessages!, m => m.Content.Contains("build ok", StringComparison.Ordinal));
        }
        finally
        {
            await server.StopAsync();
        }
    }

    // ---- the config page ------------------------------------------------------------------------------------------

    private const string ConfigWithSecrets = """
        {
          "providers": { "local": { "kind": "openai-compatible", "model": "m1", "baseUrl": "http://127.0.0.1:9/v1", "apiKey": "sk-secret-1234" } },
          "defaultProvider": "local",
          "web": { "users": { "me": "pw" }, "roots": ["/srv"], "allow": { "config": true } }
        }
        """;

    private string ConfigPath => Path.Combine(_paths.ConfigDir, "config.json");

    [Fact]
    public async Task The_config_page_is_refused_while_its_switch_is_off()
    {
        await using var tab = await Tab.OpenAsync(_server.Bound.Port);
        await tab.SendAsync(Msg("getConfig"));
        Assert.Contains("web.allow.config", (await tab.NextAsync(Type("error")))["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_config_page_shows_the_file_without_its_secrets_or_the_browser_s_own_limits()
    {
        File.WriteAllText(ConfigPath, ConfigWithSecrets);
        var (server, _) = await ServerWithAsync(Core.Web.WebAccess.Default with { Config = true });
        try
        {
            await using var tab = await Tab.OpenAsync(server.Bound.Port);
            await tab.SendAsync(Msg("getConfig"));
            var text = (await tab.NextAsync(Type("config")))["text"]!.GetValue<string>();
            Assert.Contains("\"model\": \"m1\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-secret-1234", text, StringComparison.Ordinal);
            Assert.Contains("1234", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"users\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"roots\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"allow\"", text, StringComparison.Ordinal);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task A_save_writes_the_edit_and_keeps_the_hidden_secret_and_the_browser_s_limits()
    {
        File.WriteAllText(ConfigPath, ConfigWithSecrets);
        var (server, _) = await ServerWithAsync(Core.Web.WebAccess.Default with { Config = true });
        try
        {
            await using var tab = await Tab.OpenAsync(server.Bound.Port);
            await tab.SendAsync(Msg("getConfig"));
            var text = (await tab.NextAsync(Type("config")))["text"]!.GetValue<string>();

            // AN EDIT, AND AN ATTEMPT TO WIDEN ITS OWN ACCESS: the second is ignored.
            var edited = JsonNode.Parse(text)!.AsObject();
            edited["providers"]!["local"]!["model"] = "m2";
            edited["web"] ??= new JsonObject();
            edited["web"]!["allow"] = new JsonObject { ["shell"] = true };
            edited["web"]!["users"] = new JsonObject { ["intruder"] = "x" };
            await tab.SendAsync(Msg("saveConfig", ("text", edited.ToJsonString())));
            await tab.NextAsync(Type("configSaved"));

            var saved = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
            Assert.Equal("m2", saved["providers"]!["local"]!["model"]!.GetValue<string>());
            Assert.Equal("sk-secret-1234", saved["providers"]!["local"]!["apiKey"]!.GetValue<string>());
            Assert.Null(saved["web"]!["allow"]!["shell"]);
            Assert.True(saved["web"]!["allow"]!["config"]!.GetValue<bool>());
            Assert.Equal("pw", saved["web"]!["users"]!["me"]!.GetValue<string>());
            Assert.Null(saved["web"]!["users"]!["intruder"]);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task A_save_the_loader_refuses_writes_nothing_and_says_why()
    {
        File.WriteAllText(ConfigPath, ConfigWithSecrets);
        var before = File.ReadAllText(ConfigPath);
        var (server, _) = await ServerWithAsync(Core.Web.WebAccess.Default with { Config = true });
        try
        {
            await using var tab = await Tab.OpenAsync(server.Bound.Port);
            await tab.SendAsync(Msg("getConfig"));
            await tab.NextAsync(Type("config"));
            await tab.SendAsync(Msg("saveConfig", ("text", """{ "providers": { "local": { "kind": "no-such-kind", "model": "m" } }, "defaultProvider": "local" }""")));
            Assert.NotEmpty((await tab.NextAsync(Type("configRefused")))["errors"]!.AsArray());
            await tab.SendAsync(Msg("saveConfig", ("text", "{ not json")));
            await tab.NextAsync(Type("configRefused"));
            Assert.Equal(before, File.ReadAllText(ConfigPath));
        }
        finally { await server.StopAsync(); }
    }
}
