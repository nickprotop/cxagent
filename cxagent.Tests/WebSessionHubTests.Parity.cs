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
    private async Task<(HttpServer Server, WebSessionHub Hub)> ServerWithAsync(Core.Web.WebAccess allow, string? root = null)
    {
        var composer = new SessionComposer(new ComposerServices(_manager, _paths, new PermissionRulesStore(_paths), null, null) { Prompts = _prompts });
        // MEMORY ON, as the app has it and as the shared harness does.
        var resolved = ResolvedConfig.ForTesting(_model);
        var opener = new ComposerSessionOpener(composer, resolved with { Catalog = resolved.Catalog with { Memory = true } }, AgentMode.Single);
        var hub = new WebSessionHub(new WebHubServices(_manager, _prompts, new FolderSessionStore(_paths), _paths, [root ?? Projects], opener, _clock)
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

    // ---- the plugin marketplace -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_marketplace_and_its_actions_are_refused_while_plugins_are_off()
    {
        var (server, _) = await ServerWithAsync(Core.Web.WebAccess.Default with { Plugins = false });
        try
        {
            await using var tab = await Tab.OpenAsync(server.Bound.Port);
            await tab.SendAsync(Msg("create", ("folder", Work)));
            var id = (await tab.NextAsync(Type("opened")))["sessionId"]!.GetValue<string>();
            await tab.SendAsync(Msg("plugins", ("sessionId", id)));
            Assert.Contains("web.allow.plugins", (await tab.NextAsync(Type("error")))["message"]!.GetValue<string>(), StringComparison.Ordinal);
            await tab.SendAsync(Msg("pluginAction", ("sessionId", id), ("name", "calculator"), ("action", "install"), ("folder", Work)));
            Assert.Contains("web.allow.plugins", (await tab.NextAsync(Type("error")))["message"]!.GetValue<string>(), StringComparison.Ordinal);
            await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/plugin load x")));
            Assert.Contains("web.allow.plugins", (await tab.NextAsync(Type("notice")))["text"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        finally { await server.StopAsync(); }
    }

    // ---- the config page as F8's forms ----------------------------------------------------------------------------

    private const string ConfigWithTavily = """
        {
          "providers": { "local": { "kind": "openai-compatible", "model": "m1", "baseUrl": "http://127.0.0.1:9/v1" } },
          "defaultProvider": "local",
          "webSearch": { "tavilyApiKey": "tvly-secret-9876" }
        }
        """;

    private async Task<(Tab Tab, HttpServer Server)> ConfigTabAsync(string config, bool shell = false)
    {
        File.WriteAllText(ConfigPath, config);
        var (server, _) = await ServerWithAsync(Core.Web.WebAccess.Default with { Config = true, Shell = shell });
        return (await Tab.OpenAsync(server.Bound.Port), server);
    }

    [Fact]
    public async Task The_general_form_shows_f8_s_fields_and_hides_the_search_key()
    {
        var (tab, server) = await ConfigTabAsync(ConfigWithTavily);
        try
        {
            await tab.SendAsync(Msg("configForm", ("tab", "general")));
            var form = await tab.NextAsync(Type("configForm"));
            var text = form.ToJsonString();
            Assert.Contains("Hand it to the background", text, StringComparison.Ordinal);
            Assert.Contains("Session tools", text, StringComparison.Ordinal);
            Assert.DoesNotContain("tvly-secret-9876", text, StringComparison.Ordinal);
            Assert.Contains("9876", text, StringComparison.Ordinal);
        }
        finally { await tab.DisposeAsync(); await server.StopAsync(); }
    }

    [Fact]
    public async Task A_general_save_writes_what_changed_and_keeps_the_key_it_never_sent()
    {
        var (tab, server) = await ConfigTabAsync(ConfigWithTavily);
        try
        {
            await tab.SendAsync(Msg("configFormSave", ("tab", "general"), ("values", new JsonObject { ["detach"] = "false", ["tavily"] = "" })));
            await tab.NextAsync(Type("configSaved"));
            var saved = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
            Assert.False(saved["shellDetachOnTimeout"]!.GetValue<bool>());
            Assert.Equal("tvly-secret-9876", saved["webSearch"]!["tavilyApiKey"]!.GetValue<string>());
        }
        finally { await tab.DisposeAsync(); await server.StopAsync(); }
    }

    [Fact]
    public async Task The_general_form_offers_memory_and_turning_it_off_writes_what_F8_writes()
    {
        var (tab, server) = await ConfigTabAsync(ConfigWithTavily);
        try
        {
            await tab.SendAsync(Msg("configFormSave", ("tab", "general"), ("values", new JsonObject { ["memory"] = "false" })));
            await tab.NextAsync(Type("configSaved"));
            Assert.False(JsonNode.Parse(File.ReadAllText(ConfigPath))!["memory"]!["enabled"]!.GetValue<bool>());
            Assert.Contains("Notes across sessions", WebConfigForms.General(CxAgent.UI.Config.GeneralDraft.From(
                ProviderConfigLoader.Parse(File.ReadAllText(ConfigPath), new Dictionary<string, string>()))).ToJsonString(), StringComparison.Ordinal);
        }
        finally { await tab.DisposeAsync(); await server.StopAsync(); }
    }

    [Fact]
    public async Task A_general_save_the_draft_refuses_writes_nothing()
    {
        var (tab, server) = await ConfigTabAsync(ConfigWithTavily);
        var before = File.ReadAllText(ConfigPath);
        try
        {
            await tab.SendAsync(Msg("configFormSave", ("tab", "general"), ("values", new JsonObject { ["cap"] = "Number", ["capText"] = "lots" })));
            Assert.Contains("whole number", (await tab.NextAsync(Type("configRefused")))["errors"]!.ToJsonString(), StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllText(ConfigPath));
        }
        finally { await tab.DisposeAsync(); await server.StopAsync(); }
    }

    private const string TwoProviders = """
        {
          "providers": {
            "local": { "kind": "openai-compatible", "model": "m1", "baseUrl": "http://127.0.0.1:9/v1", "apiKey": "sk-secret-1234" },
            "spare": { "kind": "openai-compatible", "model": "s1", "baseUrl": "http://127.0.0.1:9/v1" }
          },
          "defaultProvider": "local"
        }
        """;

    [Fact]
    public async Task The_providers_form_lists_each_provider_and_hides_its_key()
    {
        var (tab, server) = await ConfigTabAsync(TwoProviders);
        try
        {
            await tab.SendAsync(Msg("configForm", ("tab", "providers"), ("item", "local")));
            var form = await tab.NextAsync(Type("configForm"));
            Assert.Equal(["local", "spare"], form["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()));
            Assert.DoesNotContain("sk-secret-1234", form.ToJsonString(), StringComparison.Ordinal);
        }
        finally { await tab.DisposeAsync(); await server.StopAsync(); }
    }

    [Fact]
    public async Task A_provider_edit_writes_through_f8_s_draft_and_keeps_its_key()
    {
        var (tab, server) = await ConfigTabAsync(TwoProviders);
        try
        {
            await tab.SendAsync(Msg("configFormSave", ("tab", "providers"), ("item", "spare"),
                ("values", new JsonObject { ["description"] = "the spare one", ["apiKey"] = "" })));
            await tab.NextAsync(Type("configSaved"));
            var saved = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
            Assert.Equal("the spare one", saved["providers"]!["spare"]!["description"]!.GetValue<string>());
            Assert.Equal("sk-secret-1234", saved["providers"]!["local"]!["apiKey"]!.GetValue<string>());
        }
        finally { await tab.DisposeAsync(); await server.StopAsync(); }
    }

    [Fact]
    public async Task A_new_provider_is_added_and_removing_one_asks_first()
    {
        var (tab, server) = await ConfigTabAsync(TwoProviders);
        try
        {
            await tab.SendAsync(Msg("configFormSave", ("tab", "providers"), ("item", "+new"),
                ("values", new JsonObject { ["name"] = "third", ["kind"] = "ollama", ["model"] = "q3", ["baseUrl"] = "http://127.0.0.1:11434" })));
            await tab.NextAsync(Type("configSaved"));
            Assert.Equal("q3", JsonNode.Parse(File.ReadAllText(ConfigPath))!["providers"]!["third"]!["model"]!.GetValue<string>());

            await tab.SendAsync(Msg("configFormAction", ("tab", "providers"), ("item", "third"), ("action", "remove")));
            await tab.NextAsync(Type("configConfirm"));
            Assert.NotNull(JsonNode.Parse(File.ReadAllText(ConfigPath))!["providers"]!["third"]);
            await tab.SendAsync(Msg("configFormAction", ("tab", "providers"), ("item", "third"), ("action", "remove"), ("confirmed", true)));
            await tab.NextAsync(Type("configSaved"));
            Assert.Null(JsonNode.Parse(File.ReadAllText(ConfigPath))!["providers"]!["third"]);
        }
        finally { await tab.DisposeAsync(); await server.StopAsync(); }
    }

    [Fact]
    public async Task An_mcp_server_is_added_turned_off_and_removed_after_a_confirmation()
    {
        var (tab, server) = await ConfigTabAsync(TwoProviders, shell: true);
        try
        {
            await tab.SendAsync(Msg("configFormSave", ("tab", "mcp"), ("item", "+new"),
                ("values", new JsonObject { ["name"] = "files", ["command"] = "npx -y some-server /tmp" })));
            await tab.NextAsync(Type("configSaved"));
            var mcp = () => JsonNode.Parse(File.ReadAllText(ConfigPath))!["mcp"]?["files"];
            Assert.Equal("npx", mcp()!["command"]![0]!.GetValue<string>());

            await tab.SendAsync(Msg("configFormAction", ("tab", "mcp"), ("item", "files"), ("action", "disable")));
            await tab.NextAsync(Type("configSaved"));
            Assert.False(mcp()!["enabled"]!.GetValue<bool>());

            await tab.SendAsync(Msg("configFormAction", ("tab", "mcp"), ("item", "files"), ("action", "remove")));
            await tab.NextAsync(Type("configConfirm"));
            await tab.SendAsync(Msg("configFormAction", ("tab", "mcp"), ("item", "files"), ("action", "remove"), ("confirmed", true)));
            await tab.NextAsync(Type("configSaved"));
            Assert.Null(mcp());
        }
        finally { await tab.DisposeAsync(); await server.StopAsync(); }
    }

    [Fact]
    public async Task An_agent_type_of_your_own_is_added_and_a_shipped_one_is_changed_and_reset()
    {
        var (tab, server) = await ConfigTabAsync(TwoProviders);
        try
        {
            await tab.SendAsync(Msg("configFormSave", ("tab", "agents"), ("item", "+new"),
                ("values", new JsonObject { ["name"] = "researcher", ["description"] = "reads papers", ["briefing"] = "Read carefully.", ["runsOn"] = "Session" })));
            await tab.NextAsync(Type("configSaved"));
            var types = () => JsonNode.Parse(File.ReadAllText(ConfigPath))!["agents"];
            Assert.Equal("reads papers", types()!["researcher"]!["description"]!.GetValue<string>());

            await tab.SendAsync(Msg("configFormSave", ("tab", "agents"), ("item", "explore"), ("values", new JsonObject { ["maxTurns"] = "12" })));
            await tab.NextAsync(Type("configSaved"));
            Assert.Equal(12, types()!["explore"]!["maxTurns"]!.GetValue<int>());

            await tab.SendAsync(Msg("configFormAction", ("tab", "agents"), ("item", "explore"), ("action", "reset"), ("confirmed", true)));
            await tab.NextAsync(Type("configSaved"));
            Assert.Null(types()?["explore"]);
        }
        finally { await tab.DisposeAsync(); await server.StopAsync(); }
    }

    [Fact]
    public async Task A_hook_is_added_where_it_is_saved_and_one_missing_its_command_is_refused()
    {
        var (tab, server) = await ConfigTabAsync(TwoProviders, shell: true);
        try
        {
            await tab.SendAsync(Msg("create", ("folder", Work)));
            var id = (await tab.NextAsync(Type("opened")))["sessionId"]!.GetValue<string>();

            await tab.SendAsync(Msg("configFormSave", ("tab", "hooks"), ("item", "+new"), ("sessionId", id),
                ("values", new JsonObject { ["source"] = "User", ["event"] = "PreToolUse", ["type"] = "command", ["command"] = "" })));
            Assert.Contains("needs a command", (await tab.NextAsync(Type("configRefused")))["errors"]!.ToJsonString(), StringComparison.Ordinal);

            await tab.SendAsync(Msg("configFormSave", ("tab", "hooks"), ("item", "+new"), ("sessionId", id),
                ("values", new JsonObject { ["source"] = "User", ["event"] = "PreToolUse", ["type"] = "command", ["command"] = "echo guarded", ["matcher"] = "run_shell" })));
            await tab.NextAsync(Type("configSaved"));
            await tab.SendAsync(Msg("configForm", ("tab", "hooks"), ("sessionId", id)));
            var form = await tab.NextAsync(Type("configForm"));
            Assert.Contains(form["items"]!.AsArray(), i => i!["sub"]!.GetValue<string>().Contains("run_shell", StringComparison.Ordinal));
        }
        finally { await tab.DisposeAsync(); await server.StopAsync(); }
    }

    // ---- the Project tab: F8's, in the browser ----------------------------------------------------------------------

    private async Task<(Tab Tab, HttpServer Server, string SessionId)> ProjectTabAsync()
    {
        Directory.CreateDirectory(Path.Combine(Work, ".git"));
        var (tab, server) = await ConfigTabAsync(TwoProviders);
        await tab.SendAsync(Msg("create", ("folder", Work)));
        var id = (await tab.NextAsync(Type("opened")))["sessionId"]!.GetValue<string>();
        return (tab, server, id);
    }

    [Fact]
    public async Task The_project_tab_is_offered_and_lists_what_the_repo_brings_with_its_memory()
    {
        var (tab, server, id) = await ProjectTabAsync();
        try
        {
            new CxAgent.Core.Memory.MemoryStore(_paths.ConfigDir, Work)
                .Write(new("deploy-target", "d", CxAgent.Core.Memory.MemoryScope.Project, "b"));
            await tab.SendAsync(Msg("configForm", ("tab", "project"), ("sessionId", id)));
            var form = await tab.NextAsync(Type("configForm"));
            var text = form.ToJsonString();

            Assert.Contains(form["tabs"]!.AsArray(), t => t!["id"]!.GetValue<string>() == "project");
            foreach (var kind in Core.Sessions.ProjectSummary.Kinds) Assert.Contains($"\"{kind}\"", text, StringComparison.Ordinal);
            Assert.Contains("deploy-target", text, StringComparison.Ordinal);
            Assert.Contains("\"trust\"", text, StringComparison.Ordinal);
        }
        finally { await tab.DisposeAsync(); await server.StopAsync(); }
    }

    [Fact]
    public async Task The_project_tab_s_trust_is_saved_as_trust_says()
    {
        var (tab, server, id) = await ProjectTabAsync();
        try
        {
            await tab.SendAsync(Msg("configFormSave", ("tab", "project"), ("sessionId", id), ("values", new JsonObject { ["trust"] = "false" })));
            await tab.NextAsync(Type("configSaved"));
            Assert.Equal(Core.Permissions.TrustState.Untrusted, new PermissionRulesStore(_paths).GetTrust(Work));
            Assert.False(_manager.Sessions.Single(s => s.Id == id).Policy!.FolderTrusted);
        }
        finally { await tab.DisposeAsync(); await server.StopAsync(); }
    }

    [Fact]
    public async Task The_web_tab_is_read_only_and_shows_no_password()
    {
        var (tab, server) = await ConfigTabAsync("""
            { "providers": { "local": { "kind": "ollama", "model": "m" } }, "defaultProvider": "local",
              "web": { "users": { "me": "hunter2" }, "allow": { "config": true } } }
            """);
        try
        {
            await tab.SendAsync(Msg("configForm", ("tab", "web")));
            var form = await tab.NextAsync(Type("configForm"));
            Assert.True(form["readOnly"]!.GetValue<bool>());
            Assert.Contains("me", form.ToJsonString(), StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", form.ToJsonString(), StringComparison.Ordinal);
            await tab.SendAsync(Msg("configFormSave", ("tab", "web"), ("values", new JsonObject { ["login"] = "off" })));
            await tab.NextAsync(Type("error"));
        }
        finally { await tab.DisposeAsync(); await server.StopAsync(); }
    }

    // ---- review fixes ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_editor_never_opens_cxagent_s_own_config_folder_even_under_a_root_or_with_open_anywhere()
    {
        // A ROOT ABOVE THE CONFIG FOLDER — `cxagent web` started in ~ — must not hand a login config.json: its keys,
        // the users' hashes, and the switches it could turn on for itself.
        File.WriteAllText(ConfigPath, TwoProviders);
        var (server, _) = await ServerWithAsync(Core.Web.WebAccess.Default with { OpenAnywhere = true }, root: _root);
        try
        {
            await using var tab = await Tab.OpenAsync(server.Bound.Port);
            await tab.SendAsync(Msg("create", ("folder", Work)));
            var id = (await tab.NextAsync(Type("opened")))["sessionId"]!.GetValue<string>();
            await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/open " + ConfigPath)));
            Assert.Contains("cxagent's own", (await tab.NextAsync(Type("notice")))["text"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.DoesNotContain(tab.Seen, m => m["type"]?.GetValue<string>() == "openedFile");
        }
        finally { await server.StopAsync(); }
    }

    // ---- memory -----------------------------------------------------------------------------------------------------

    private CxAgent.Core.Memory.MemoryStore Memory => new(_paths.ConfigDir, Work);

    [Fact]
    public async Task A_note_opens_by_name_though_its_folder_is_cxagent_s_own_and_never_by_path()
    {
        Memory.Write(new("deploy-target", "Where it deploys", CxAgent.Core.Memory.MemoryScope.Project, "fly.io"));
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;

        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/memory open deploy-target")));
        var file = await tab.NextAsync(Type("openedFile"));
        Assert.Contains("fly.io", file["text"]!.GetValue<string>(), StringComparison.Ordinal);

        // THE SAME FILE BY ITS PATH is still refused: the name is the only way in.
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/open " + Memory.PathOf("deploy-target"))));
        await tab.NextAsync(Type("notice"));
        Assert.Single(tab.Seen, m => m["type"]?.GetValue<string>() == "openedFile");
    }

    [Theory]
    [InlineData("../../config")]
    [InlineData("config.json")]
    public async Task A_name_that_is_not_a_note_reaches_nothing(string name)
    {
        File.WriteAllText(ConfigPath, TwoProviders);
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("memoryOpen", ("sessionId", id), ("name", name)));
        Assert.Contains("no note", (await tab.NextAsync(Type("notice")))["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(tab.Seen, m => m["type"]?.GetValue<string>() == "openedFile");
    }

    [Fact]
    public async Task Forgetting_a_note_asks_and_deletes_only_when_confirmed()
    {
        Memory.Write(new("old-fact", "Wrong now", CxAgent.Core.Memory.MemoryScope.System, "x"));
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;

        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "/memory forget old-fact")));
        var ask = await tab.NextAsync(Type("confirmForget"));
        Assert.Contains("Wrong now", ask["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotNull(Memory.Find("old-fact"));

        await tab.SendAsync(Msg("memoryForget", ("sessionId", id), ("name", "old-fact"), ("confirm", true)));
        await tab.NextAsync(m => m["type"]?.GetValue<string>() == "panel" && m["memory"] is JsonArray { Count: 0 });
        Assert.Null(Memory.Find("old-fact"));
    }

    [Fact]
    public async Task The_panel_lists_the_notes_by_name_scope_and_description()
    {
        Memory.Write(new("prefers-tabs", "Indentation", CxAgent.Core.Memory.MemoryScope.System, "tabs"));
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        var panel = await tab.NextAsync(m => m["type"]?.GetValue<string>() == "panel" && m["memory"] is JsonArray { Count: > 0 });
        var note = panel["memory"]![0]!;
        Assert.Equal("prefers-tabs", note["name"]!.GetValue<string>());
        Assert.Equal("system", note["scope"]!.GetValue<string>());
        Assert.Equal("Indentation", note["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task No_part_of_the_web_block_is_shown_or_changed_by_a_browser_s_text_save()
    {
        // LOGIN, BIND AND PORT TOO, not only the switches: a login turning the login off, or listening on every
        // interface, has widened its own access as surely as one turning on the shell.
        File.WriteAllText(ConfigPath, ConfigWithSecrets);
        var before = JsonNode.Parse(File.ReadAllText(ConfigPath))!["web"]!.ToJsonString();
        var (server, _) = await ServerWithAsync(Core.Web.WebAccess.Default with { Config = true });
        try
        {
            await using var tab = await Tab.OpenAsync(server.Bound.Port);
            await tab.SendAsync(Msg("getConfig"));
            var text = (await tab.NextAsync(Type("config")))["text"]!.GetValue<string>();
            Assert.DoesNotContain("\"web\"", text, StringComparison.Ordinal);

            var edited = JsonNode.Parse(text)!.AsObject();
            edited["web"] = new JsonObject { ["login"] = "off", ["bind"] = "0.0.0.0", ["port"] = 1 };
            await tab.SendAsync(Msg("saveConfig", ("text", edited.ToJsonString())));
            await tab.NextAsync(Type("configSaved"));
            Assert.Equal(before, JsonNode.Parse(File.ReadAllText(ConfigPath))!["web"]!.ToJsonString());
        }
        finally { await server.StopAsync(); }
    }

    private const string ConfigWithServerSecrets = """
        {
          "providers": { "local": { "kind": "openai-compatible", "model": "m1", "baseUrl": "http://127.0.0.1:9/v1", "apiKey": "sk-secret-1234" } },
          "defaultProvider": "local",
          "mcp": { "cloud": { "command": ["srv", "--api-key", "flag-secret-5678"], "env": { "AWS_SECRET_ACCESS_KEY": "aws-secret-9999" } } }
        }
        """;

    [Fact]
    public async Task Secrets_under_other_names_and_after_a_flag_are_hidden_too()
    {
        File.WriteAllText(ConfigPath, ConfigWithServerSecrets);
        var (server, _) = await ServerWithAsync(Core.Web.WebAccess.Default with { Config = true });
        try
        {
            await using var tab = await Tab.OpenAsync(server.Bound.Port);
            await tab.SendAsync(Msg("getConfig"));
            var text = (await tab.NextAsync(Type("config")))["text"]!.GetValue<string>();
            Assert.DoesNotContain("aws-secret-9999", text, StringComparison.Ordinal);
            Assert.DoesNotContain("flag-secret-5678", text, StringComparison.Ordinal);

            // SENT BACK UNCHANGED, each is written back as it was.
            await tab.SendAsync(Msg("saveConfig", ("text", text)));
            await tab.NextAsync(Type("configSaved"));
            var saved = File.ReadAllText(ConfigPath);
            Assert.Contains("aws-secret-9999", saved, StringComparison.Ordinal);
            Assert.Contains("flag-secret-5678", saved, StringComparison.Ordinal);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task A_placeholder_pasted_somewhere_else_stays_the_placeholder()
    {
        // A SECRET COMES BACK ONLY WHERE IT WAS TAKEN FROM: pasted into a description, it would otherwise be written
        // there in clear and shown on the next read, under a name that is not secret.
        File.WriteAllText(ConfigPath, ConfigWithSecrets);
        var (server, _) = await ServerWithAsync(Core.Web.WebAccess.Default with { Config = true });
        try
        {
            await using var tab = await Tab.OpenAsync(server.Bound.Port);
            await tab.SendAsync(Msg("getConfig"));
            var edited = JsonNode.Parse((await tab.NextAsync(Type("config")))["text"]!.GetValue<string>())!.AsObject();
            var placeholder = edited["providers"]!["local"]!["apiKey"]!.GetValue<string>();
            edited["providers"]!["local"]!["description"] = placeholder;
            await tab.SendAsync(Msg("saveConfig", ("text", edited.ToJsonString())));
            await tab.NextAsync(Type("configSaved"));
            var saved = JsonNode.Parse(File.ReadAllText(ConfigPath))!["providers"]!["local"]!;
            Assert.Equal(placeholder, saved["description"]!.GetValue<string>());
            Assert.Equal("sk-secret-1234", saved["apiKey"]!.GetValue<string>());
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task A_text_save_over_a_file_changed_since_it_was_read_is_refused()
    {
        File.WriteAllText(ConfigPath, ConfigWithSecrets);
        var (server, _) = await ServerWithAsync(Core.Web.WebAccess.Default with { Config = true });
        try
        {
            await using var tab = await Tab.OpenAsync(server.Bound.Port);
            await tab.SendAsync(Msg("getConfig"));
            var text = (await tab.NextAsync(Type("config")))["text"]!.GetValue<string>();
            File.WriteAllText(ConfigPath, ConfigWithSecrets.Replace("\"m1\"", "\"m9\"", StringComparison.Ordinal));
            await tab.SendAsync(Msg("saveConfig", ("text", text)));
            Assert.Contains("changed since", (await tab.NextAsync(Type("configRefused")))["errors"]!.ToJsonString(), StringComparison.Ordinal);
            Assert.Contains("\"m9\"", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
        }
        finally { await server.StopAsync(); }
    }

    [Theory]
    [InlineData("/mcp edit")]
    [InlineData("/mcp login github")]
    [InlineData("/hooks edit")]
    [InlineData("/agents edit")]
    [InlineData("/agents open scout")]
    [InlineData("/plugin browse")]
    [InlineData("/sessions export 2")]
    [InlineData("/sandbox edit")]
    public void A_verb_that_opens_a_terminal_window_or_acts_on_the_host_is_refused_whatever_the_switches(string command)
    {
        var all = new Core.Web.WebAccess(Shell: true, Config: true, Plugins: true, OpenAnywhere: true);
        Assert.NotNull(CxAgent.Web.WebCommands.Refusal(command, all));
    }

    [Theory]
    [InlineData("/mcp reload")]
    [InlineData("/hooks review")]
    public void A_verb_that_changes_what_runs_needs_config(string command)
    {
        Assert.NotNull(CxAgent.Web.WebCommands.Refusal(command, Core.Web.WebAccess.Default));
        Assert.Null(CxAgent.Web.WebCommands.Refusal(command, Core.Web.WebAccess.Default with { Config = true }));
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/mcp show github")]
    [InlineData("/hooks")]
    [InlineData("/agents show scout")]
    [InlineData("/agents send scout look again")]
    [InlineData("/tasks stop 42")]
    [InlineData("/plugin load calculator")]
    public void The_reading_and_sending_verbs_are_allowed(string command) =>
        Assert.Null(CxAgent.Web.WebCommands.Refusal(command, Core.Web.WebAccess.Default));

    [Fact]
    public void A_plugin_s_secret_setting_is_described_not_sent()
    {
        // AN API TOKEN IN A PLUGIN'S SETTINGS IS A KEY: the marketplace is on by default, so it must not reach the page.
        using var doc = System.Text.Json.JsonDocument.Parse("""{ "apiToken": "tok-secret-4321", "server": "csharp-ls" }""");
        var row = new CxAgent.UI.PluginRow("p", CxAgent.UI.PluginRowSection.Installed, "",
            Configured: new PluginConfig("p.dll") { Settings = doc.RootElement.Clone() });
        var token = CxAgent.Web.WebPlugins.Setting(row, "apiToken", "the token");
        Assert.True(token["secret"]!.GetValue<bool>());
        Assert.Equal("", token["value"]!.GetValue<string>());
        Assert.DoesNotContain("tok-secret-4321", token.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("4321", token["saved"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("csharp-ls", CxAgent.Web.WebPlugins.Setting(row, "server", "the server")["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_message_too_large_is_refused_out_loud_and_the_tab_stays_connected()
    {
        var (tab, id) = await CreatedAsync();
        await using var _ = tab;
        await tab.SendAsync(Msg("say", ("sessionId", id), ("text", "x"),
            ("images", new JsonArray(new JsonObject { ["name"] = "huge.png", ["data"] = new string('A', 25 * 1024 * 1024) }))));
        Assert.Contains("larger than", (await tab.NextAsync(Type("error")))["message"]!.GetValue<string>(), StringComparison.Ordinal);
        await tab.SendAsync(Msg("list"));
        await tab.NextAsync(Type("sessions"));
    }

    [Fact]
    public async Task A_hook_moved_by_a_change_elsewhere_is_not_the_one_removed()
    {
        var (tab, server) = await ConfigTabAsync(TwoProviders, shell: true);
        try
        {
            await tab.SendAsync(Msg("create", ("folder", Work)));
            var id = (await tab.NextAsync(Type("opened")))["sessionId"]!.GetValue<string>();
            await tab.SendAsync(Msg("configFormSave", ("tab", "hooks"), ("item", "+new"), ("sessionId", id),
                ("values", new JsonObject { ["source"] = "User", ["event"] = "PreToolUse", ["type"] = "command", ["command"] = "echo first" })));
            await tab.NextAsync(Type("configSaved"));
            var form = await tab.NextAsync(Type("configForm"));
            var first = form["items"]!.AsArray().Single()!["id"]!.GetValue<string>();

            // ANOTHER HOOK TAKES ITS PLACE, written elsewhere — the terminal's F8, a hand edit.
            await tab.SendAsync(Msg("configFormSave", ("tab", "hooks"), ("item", "+new"), ("sessionId", id),
                ("values", new JsonObject { ["source"] = "User", ["event"] = "PostToolUse", ["type"] = "command", ["command"] = "echo second" })));
            await tab.NextAsync(Type("configSaved"));
            var hooksFile = ConfigPath;
            var root = JsonNode.Parse(File.ReadAllText(hooksFile))!.AsObject();
            var hooks = root["hooks"]!.AsObject();
            var reordered = new JsonObject();
            foreach (var key in hooks.Select(k => k.Key).Reverse().ToList()) reordered[key] = hooks[key]!.DeepClone();
            root["hooks"] = reordered;
            File.WriteAllText(hooksFile, root.ToJsonString());

            await tab.SendAsync(Msg("configFormAction", ("tab", "hooks"), ("item", first), ("action", "remove"), ("confirmed", true), ("sessionId", id)));
            var answer = await tab.NextAsync(m => m["type"]?.GetValue<string>() is "configRefused" or "configSaved");
            var text = File.ReadAllText(hooksFile);
            if (answer["type"]!.GetValue<string>() == "configSaved")
                Assert.DoesNotContain("echo first", text, StringComparison.Ordinal);
            else
                Assert.Contains("changed since", answer["errors"]!.ToJsonString(), StringComparison.Ordinal);
            Assert.Contains("echo second", text, StringComparison.Ordinal);
        }
        finally { await tab.DisposeAsync(); await server.StopAsync(); }
    }

    // ---- a command a browser saves runs as you: it needs the shell switch too ---------------------------------------

    [Fact]
    public async Task A_command_hook_or_a_local_mcp_server_needs_the_shell_switch_as_well_as_config()
    {
        var (tab, server) = await ConfigTabAsync(TwoProviders);
        try
        {
            await tab.SendAsync(Msg("create", ("folder", Work)));
            var id = (await tab.NextAsync(Type("opened")))["sessionId"]!.GetValue<string>();
            await tab.SendAsync(Msg("configFormSave", ("tab", "hooks"), ("item", "+new"), ("sessionId", id),
                ("values", new JsonObject { ["source"] = "User", ["event"] = "PreToolUse", ["type"] = "command", ["command"] = "echo x" })));
            Assert.Contains("web.allow.shell", (await tab.NextAsync(Type("configRefused")))["errors"]!.ToJsonString(), StringComparison.Ordinal);

            await tab.SendAsync(Msg("configFormSave", ("tab", "mcp"), ("item", "+new"),
                ("values", new JsonObject { ["name"] = "local", ["command"] = "npx srv" })));
            Assert.Contains("web.allow.shell", (await tab.NextAsync(Type("configRefused")))["errors"]!.ToJsonString(), StringComparison.Ordinal);

            // A REMOTE SERVER RUNS NOTHING HERE.
            await tab.SendAsync(Msg("configFormSave", ("tab", "mcp"), ("item", "+new"),
                ("values", new JsonObject { ["name"] = "remote", ["url"] = "https://mcp.example.com" })));
            await tab.NextAsync(Type("configSaved"));

            var text = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
            text["mcp"]!["sneaky"] = new JsonObject { ["command"] = new JsonArray("sh", "-c", "id") };
            await tab.SendAsync(Msg("getConfig"));
            var shown = JsonNode.Parse((await tab.NextAsync(Type("config")))["text"]!.GetValue<string>())!.AsObject();
            shown["mcp"]!["sneaky"] = new JsonObject { ["command"] = new JsonArray("sh", "-c", "id") };
            await tab.SendAsync(Msg("saveConfig", ("text", shown.ToJsonString())));
            Assert.Contains("web.allow.shell", (await tab.NextAsync(Type("configRefused")))["errors"]!.ToJsonString(), StringComparison.Ordinal);
        }
        finally { await tab.DisposeAsync(); await server.StopAsync(); }
    }
}
