using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using CxAgent.Core.Web;
using CxAgent.Hosting;
using CxAgent.Web;
using Xunit;

namespace CxAgent.Tests;

/// <summary>`cxagent web` as a whole: its flags, when it refuses to start, its login routes, and that stopping it lets
/// every session go.</summary>
public sealed class WebServerHostTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("webhost-").FullName;
    private readonly AppPaths _paths;
    private readonly SessionManager _manager;
    private readonly PromptHub _prompts = new();
    private readonly ComposerSessionOpener _opener;
    private WebServerHost? _host;

    public WebServerHostTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "cfg"));
        _paths.EnsureCreated();
        Directory.CreateDirectory(Work);
        _manager = SessionManager.Create(_paths);
        _manager.Holder = LockHolder.Web;
        var rules = new PermissionRulesStore(_paths);
        rules.SetTrust(Work, TrustState.Trusted);
        var composer = new SessionComposer(new ComposerServices(_manager, _paths, rules, null, null) { Prompts = _prompts });
        _opener = new ComposerSessionOpener(composer, ResolvedConfig.ForTesting(new MockLlmProvider("m")), AgentMode.Single);
    }

    private string Work => Path.Combine(_root, "work");

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        _manager.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    private async Task<WebServerHost> StartAsync(WebSettings settings)
    {
        _host = await WebServerHost.StartAsync(new WebHostRequest(_paths, settings with { Port = 0 }, _manager, _prompts, _opener, Work));
        return _host;
    }

    private static WebSettings Always => WebSettings.Default with
    {
        Login = LoginMode.Always,
        Users = new Dictionary<string, string> { ["nick"] = "pw" },
    };

    private static async Task<string> Raw(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var s = client.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes(request));
        client.Client.Shutdown(SocketShutdown.Send);
        using var reader = new StreamReader(s);
        return await reader.ReadToEndAsync();
    }

    private static string Post(int port, string path, string body, string? origin = "default", string? cookie = null)
    {
        var o = origin == "default" ? $"http://localhost:{port}" : origin;
        return $"POST {path} HTTP/1.1\r\nHost: localhost:{port}\r\n"
            + (o is null ? "" : $"Origin: {o}\r\n")
            + (cookie is null ? "" : $"Cookie: cxagent_web={cookie}\r\n")
            + $"Content-Type: application/x-www-form-urlencoded\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
    }

    private static string? CookieOf(string response)
    {
        var line = response.Split("\r\n").FirstOrDefault(l => l.StartsWith("Set-Cookie: cxagent_web=", StringComparison.Ordinal));
        return line?["Set-Cookie: cxagent_web=".Length..].Split(';')[0];
    }

    // ---- flags ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_reads_port_bind_self_signed_config_dir()
    {
        var o = WebCli.Parse(["--port", "6001", "--bind", "0.0.0.0", "--self-signed", "--config-dir", "/x"], out var error);
        Assert.Null(error);
        Assert.Equal(new WebCliOptions(6001, "0.0.0.0", true, "/x"), o);
    }

    [Theory]
    [InlineData("--nope")]
    [InlineData("--port")]
    [InlineData("--port", "x")]
    [InlineData("--port", "70000")]
    public void Parse_rejects_bad_flags(params string[] args)
    {
        Assert.Null(WebCli.Parse(args, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Flag_overrides_config()
    {
        var effective = WebCli.Effective(WebSettings.Default with { Port = 7000, Bind = "127.0.0.1" }, new WebCliOptions(7001, null, true, null));
        Assert.Equal(7001, effective.Port);
        Assert.Equal("127.0.0.1", effective.Bind);
        Assert.True(effective.SelfSigned);
    }

    // ---- starting ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Remote_auto_without_users_refuses_to_start_and_says_how()
    {
        var refused = await Assert.ThrowsAsync<WebStartRefused>(() => StartAsync(WebSettings.Default with { Bind = "0.0.0.0" }));
        Assert.Contains("cxagent web passwd", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bind_that_is_not_an_address_is_refused() =>
        await Assert.ThrowsAsync<WebStartRefused>(() => StartAsync(WebSettings.Default with { Bind = "localhost" }));

    [Fact]
    public async Task Loopback_starts_and_serves_index()
    {
        var host = await StartAsync(WebSettings.Default);
        var response = await Raw(host.Url.Port, $"GET / HTTP/1.1\r\nHost: localhost:{host.Url.Port}\r\nConnection: close\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", response, StringComparison.Ordinal);
        Assert.Empty(host.Warnings);
    }

    [Fact]
    public async Task Remote_plain_http_warns_and_banners()
    {
        var host = await StartAsync(Always with { Bind = "0.0.0.0" });
        Assert.Contains(host.Warnings, w => w.Contains("plain HTTP", StringComparison.Ordinal));
        Assert.Contains(host.Banners, b => b.Contains("plain HTTP", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_off_beyond_this_machine_warns()
    {
        var host = await StartAsync(WebSettings.Default with { Bind = "0.0.0.0", Login = LoginMode.Off });
        Assert.Contains(host.Warnings, w => w.Contains("web.login is off", StringComparison.Ordinal));
    }

    // ---- routes --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_login_an_unknown_host_is_refused()
    {
        var host = await StartAsync(WebSettings.Default);
        var response = await Raw(host.Url.Port, $"GET / HTTP/1.1\r\nHost: evil.example:{host.Url.Port}\r\nConnection: close\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 421", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_required_serves_the_login_page_until_logged_in()
    {
        var host = await StartAsync(Always);
        var port = host.Url.Port;
        var page = await Raw(port, $"GET / HTTP/1.1\r\nHost: localhost:{port}\r\nConnection: close\r\n\r\n");
        Assert.Contains("name=\"password\"", page, StringComparison.Ordinal);

        var login = await Raw(port, Post(port, "/login", "user=nick&password=pw"));
        Assert.StartsWith("HTTP/1.1 303", login, StringComparison.Ordinal);
        var cookie = CookieOf(login);
        Assert.NotNull(cookie);

        var index = await Raw(port, $"GET / HTTP/1.1\r\nHost: localhost:{port}\r\nCookie: cxagent_web={cookie}\r\nConnection: close\r\n\r\n");
        Assert.DoesNotContain("name=\"password\"", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_password_redirects_back_with_failed()
    {
        var host = await StartAsync(Always);
        var response = await Raw(host.Url.Port, Post(host.Url.Port, "/login", "user=nick&password=no"));
        Assert.StartsWith("HTTP/1.1 303", response, StringComparison.Ordinal);
        Assert.Contains("Location: /?login=failed", response, StringComparison.Ordinal);
        Assert.Null(CookieOf(response));
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData(null)]
    public async Task A_login_from_another_page_is_refused(string? origin)
    {
        var host = await StartAsync(Always);
        var response = await Raw(host.Url.Port, Post(host.Url.Port, "/login", "user=nick&password=pw", origin));
        Assert.StartsWith("HTTP/1.1 403", response, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("user=nick&user=ana&password=pw")]
    [InlineData("user=nick&password=%ZZ")]
    [InlineData("user=nick")]
    public async Task A_malformed_login_form_is_400(string body)
    {
        var host = await StartAsync(Always);
        Assert.StartsWith("HTTP/1.1 400", await Raw(host.Url.Port, Post(host.Url.Port, "/login", body)), StringComparison.Ordinal);
    }

    private static async Task<ClientWebSocket> ConnectAsync(int port, string? cookie)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", $"http://localhost:{port}");
        if (cookie is not null) ws.Options.SetRequestHeader("Cookie", $"cxagent_web={cookie}");
        await ws.ConnectAsync(new Uri($"ws://localhost:{port}/ws"), CancellationToken.None);
        return ws;
    }

    [Fact]
    public async Task The_socket_needs_the_cookie_when_login_is_required()
    {
        var host = await StartAsync(Always);
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectAsync(host.Url.Port, null));
        var cookie = CookieOf(await Raw(host.Url.Port, Post(host.Url.Port, "/login", "user=nick&password=pw")));
        using var ws = await ConnectAsync(host.Url.Port, cookie);
        Assert.Equal(WebSocketState.Open, ws.State);
    }

    [Fact]
    public async Task Logout_closes_that_login_s_open_sockets()
    {
        var host = await StartAsync(Always);
        var port = host.Url.Port;
        var cookie = CookieOf(await Raw(port, Post(port, "/login", "user=nick&password=pw")))!;
        using var ws = await ConnectAsync(port, cookie);

        var logout = await Raw(port, Post(port, "/logout", "", cookie: cookie));
        Assert.Contains("Max-Age=0", logout, StringComparison.Ordinal);

        var buffer = new byte[1 << 16];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (ws.State == WebSocketState.Open) await ws.ReceiveAsync(buffer, timeout.Token);
        }
        catch (WebSocketException) { }
        Assert.NotEqual(WebSocketState.Open, ws.State);
    }

    [Fact]
    public async Task Stopping_releases_every_session_s_lock()
    {
        var host = await StartAsync(WebSettings.Default);
        var session = _opener.Create(Work);
        var agent = session.SessionId!;
        Assert.NotNull(SessionLock.Probe(_paths.LocksDir, agent));

        await host.DisposeAsync();
        _host = null;
        Assert.Null(SessionLock.Probe(_paths.LocksDir, agent));
    }

    // ---- passwd --------------------------------------------------------------------------------------------------

    [Fact]
    public void Passwd_writes_a_hash_and_keeps_other_keys()
    {
        var config = Path.Combine(_paths.ConfigDir, "config.json");
        File.WriteAllText(config, """{ "providers": { "x": { "kind": "ollama", "model": "m" } }, "web": { "port": 6000, "users": { "ana": "pw" } }, "future": 1 }""");

        WebCli.SetPassword(config, "nick", "secret");

        var root = JsonNode.Parse(File.ReadAllText(config))!;
        Assert.Equal(1, root["future"]!.GetValue<int>());
        Assert.Equal(6000, root["web"]!["port"]!.GetValue<int>());
        Assert.Equal("pw", root["web"]!["users"]!["ana"]!.GetValue<string>());
        var stored = root["web"]!["users"]!["nick"]!.GetValue<string>();
        Assert.StartsWith("pbkdf2:", stored, StringComparison.Ordinal);
        Assert.True(CxAgent.Web.Auth.PasswordCheck.Matches(stored, "secret"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(config));
    }
}
