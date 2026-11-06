using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using CxAgent.Hosting;
using CxAgent.UI;
using CxAgent.Web;
using CxAgent.Web.Http;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The terminal serving its own sessions: --web on the command line, sessions that never unload while the
/// terminal holds them, and tabs told when sharing stops.</summary>
public sealed class TuiWebTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tuiweb-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public void CommandLine_parses_web_flags()
    {
        var o = CommandLine.Parse(["--web", "--port", "6001", "--self-signed"]);
        Assert.Null(o.Error);
        Assert.Equal(new WebCliOptions(6001, null, true, null), o.Web);
        Assert.Null(CommandLine.Parse([]).Web);
    }

    [Theory]
    [InlineData("--port", "6001")]
    [InlineData("--bind", "0.0.0.0")]
    [InlineData("--self-signed")]
    public void A_web_flag_without_web_is_an_error(params string[] args) =>
        Assert.Contains("only applies with --web", CommandLine.Parse(args).Error, StringComparison.Ordinal);

    [Fact]
    public void Web_with_print_is_an_error() =>
        Assert.Contains("--web", CommandLine.Parse(["--web", "-p", "hi"]).Error, StringComparison.Ordinal);

    private sealed class KeepingOpener(ISessionOpener inner) : ISessionOpener
    {
        public bool UnloadsIdle => false;
        public Session Create(string folder) => inner.Create(folder);
        public Session Resume(SessionSnapshot snapshot) => inner.Resume(snapshot);
        public Task<bool> CloseAsync(Session session) => inner.CloseAsync(session);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Idle_unload_is_off_when_the_terminal_holds_the_sessions()
    {
        var paths = new AppPaths(Path.Combine(_root, "cfg"));
        paths.EnsureCreated();
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        using var manager = SessionManager.Create(paths);
        var prompts = new PromptHub();
        var rules = new PermissionRulesStore(paths);
        rules.SetTrust(work, TrustState.Trusted);
        var composer = new SessionComposer(new ComposerServices(manager, paths, rules, null, null) { Prompts = prompts });
        var opener = new KeepingOpener(new ComposerSessionOpener(composer, ResolvedConfig.ForTesting(new MockLlmProvider("m")), AgentMode.Single));
        var clock = new Clock();
        var hub = new WebSessionHub(new WebHubServices(manager, prompts, new FolderSessionStore(paths), paths, [_root], opener, clock));

        opener.Create(work);
        clock.Now += TimeSpan.FromDays(1);
        hub.CheckIdle();

        Assert.Single(manager.Sessions);
    }

    [Fact]
    public async Task Stopping_the_sharing_tells_every_tab_then_closes_it()
    {
        var paths = new AppPaths(Path.Combine(_root, "cfg"));
        paths.EnsureCreated();
        using var manager = SessionManager.Create(paths);
        var prompts = new PromptHub();
        var composer = new SessionComposer(new ComposerServices(manager, paths, new PermissionRulesStore(paths), null, null) { Prompts = prompts });
        var hub = new WebSessionHub(new WebHubServices(manager, prompts, new FolderSessionStore(paths), paths, [_root],
            new ComposerSessionOpener(composer, ResolvedConfig.ForTesting(new MockLlmProvider("m")), AgentMode.Single), TimeProvider.System));
        await using var server = new HttpServer(new HttpServerOptions(IPAddress.Loopback, 0), async x =>
        {
            if (await WebSocketUpgrade.AcceptAsync(x) is { } ws) await hub.ServeAsync(ws, null, CancellationToken.None);
        });
        await server.StartAsync();

        using var tab = new ClientWebSocket();
        tab.Options.SetRequestHeader("Origin", $"http://localhost:{server.Bound.Port}");
        await tab.ConnectAsync(new Uri($"ws://localhost:{server.Bound.Port}/ws"), CancellationToken.None);
        await Task.Delay(200);

        await hub.DisconnectAllAsync("The terminal stopped sharing.");

        var seen = new List<string>();
        var buffer = new byte[1 << 16];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (tab.State == WebSocketState.Open)
            {
                var r = await tab.ReceiveAsync(buffer, timeout.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                seen.Add(Encoding.UTF8.GetString(buffer, 0, r.Count));
            }
        }
        catch (WebSocketException) { }

        Assert.Contains(seen, m => JsonNode.Parse(m)?["type"]?.GetValue<string>() == "banner"
                                   && m.Contains("stopped sharing", StringComparison.Ordinal));
        Assert.NotEqual(WebSocketState.Open, tab.State);
    }
}
