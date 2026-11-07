using System.Net;
using System.Text;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.Core.Web;
using CxAgent.Hosting;
using CxAgent.Web.Auth;
using CxAgent.Web.Http;

namespace CxAgent.Web;

/// <summary>What a web server is started with — the same for <c>cxagent web</c> and <c>/web</c> in a terminal.</summary>
/// <param name="Paths">The config folder: logs, locks, the kept certificate.</param>
/// <param name="Settings">The effective settings, flags already applied.</param>
/// <param name="Manager">The process's sessions.</param>
/// <param name="Prompts">Where questions are raced between front ends.</param>
/// <param name="Opener">How a browser's new or resumed session comes alive.</param>
/// <param name="LaunchFolder">The folder the server started in — the root when none is configured.</param>
public sealed record WebHostRequest(
    AppPaths Paths,
    WebSettings Settings,
    SessionManager Manager,
    PromptHub Prompts,
    ISessionOpener Opener,
    string LaunchFolder)
{
    /// <summary>Time, for login tokens and idle unloading.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>The server will not start as configured; the message says what to change.</summary>
public sealed class WebStartRefused(string message) : Exception(message);

/// <summary>
/// One running web server: the listener, the login gate, the session hub, and the routes between them.
///
/// <para>THE ROUTES ARE FEW ON PURPOSE. Static files are served to anyone — they are the same for everyone and hold
/// nothing. Everything that reaches a session travels over <c>/ws</c>, and that is where the login is checked; the
/// only other doors are logging in and out.</para>
/// </summary>
public sealed class WebServerHost : IAsyncDisposable
{
    private readonly WebHostRequest _request;
    private readonly LoginGate _gate;
    private readonly IReadOnlySet<string> _knownHosts;
    private readonly CancellationTokenSource _stop = new();
    private HttpServer? _server;
    private Task? _idle;
    private Task? _panels;

    private WebServerHost(WebHostRequest request, LoginGate gate, IReadOnlySet<string> knownHosts, WebSessionHub hub)
    {
        _request = request;
        _gate = gate;
        _knownHosts = knownHosts;
        Hub = hub;
    }

    /// <summary>Where a browser reaches it.</summary>
    public Uri Url { get; private set; } = null!;

    /// <summary>What the console should say at start beyond the URL — the risks this setup carries.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>What every tab shows at the top.</summary>
    public IReadOnlyList<string> Banners { get; private set; } = [];

    /// <summary>The sessions' browser side.</summary>
    public WebSessionHub Hub { get; }

    /// <summary>Starts a server, or refuses with what to change.</summary>
    /// <exception cref="WebStartRefused">The settings would start a server that is open, or unreachable.</exception>
    public static async Task<WebServerHost> StartAsync(WebHostRequest r)
    {
        var s = r.Settings;

        // AN ADDRESS, NOT A NAME. Whether a login is required depends on whether the server is reachable from other
        // machines, and only a parsed address says that for certain — "localhost" can resolve elsewhere.
        if (!IPAddress.TryParse(s.Bind, out var bind))
            throw new WebStartRefused($"web.bind must be an IP address such as 127.0.0.1 or 0.0.0.0 — '{s.Bind}' is not.");
        var loopback = IPAddress.IsLoopback(bind);

        var gate = new LoginGate(s, loopback, r.Clock);
        if (gate.CanStart() == StartRefusal.NoUsersForLogin)
            throw new WebStartRefused(loopback
                ? "web.login is always, but no web.users are set. Add one with: cxagent web passwd <name>"
                : "web.login is auto and the server listens beyond this machine, but no web.users are set. Add one with: cxagent web passwd <name>");

        var known = HostCheck.Known(bind, null);
        var roots = WebSessionHub.ExpandRoots(s.Roots, r.LaunchFolder);
        var hub = new WebSessionHub(new WebHubServices(r.Manager, r.Prompts, new FolderSessionStore(r.Paths), r.Paths, roots, r.Opener, r.Clock)
        {
            IdleMinutes = s.IdleMinutes,
        });
        var host = new WebServerHost(r, gate, known, hub);

        var warnings = new List<string>();
        if (!loopback && !s.SelfSigned)
            warnings.Add("Serving plain HTTP beyond this machine: passwords and replies travel unencrypted. Use --self-signed, or put nginx/Caddy in front.");
        if (gate.ShowsNoLoginBanner)
            warnings.Add("web.login is off: anyone who reaches this port gets an agent that runs commands on this machine.");
        host.Warnings = warnings;
        host.Banners = warnings;
        hub.SetBanners(warnings);

        var certificate = s.SelfSigned
            ? SelfSignedCertificate.LoadOrCreate(r.Paths, ["localhost", Environment.MachineName],
                bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any)
                    ? [.. known.Select(n => IPAddress.TryParse(n, out var a) ? a : null).OfType<IPAddress>()]
                    : [IPAddress.Loopback, bind])
            : null;

        host._server = new HttpServer(new HttpServerOptions(bind, s.Port, certificate), host.HandleAsync);
        try
        {
            await host._server.StartAsync().ConfigureAwait(false);
        }
        catch (System.Net.Sockets.SocketException e)
        {
            throw new WebStartRefused($"Could not listen on {s.Bind}:{s.Port} — {e.Message}");
        }

        var shown = loopback ? "localhost" : bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any) ? Environment.MachineName : bind.ToString();
        host.Url = new Uri($"{(certificate is null ? "http" : "https")}://{shown}:{host._server.Bound.Port}/");

        if (r.Opener.UnloadsIdle && s.IdleMinutes > 0) host._idle = host.IdleLoopAsync();
        host._panels = host.PanelLoopAsync();
        return host;
    }

    /// <summary>
    /// Stops serving: every tab is told and disconnected, and when this server opened the sessions itself, they end
    /// the way closing the terminal ends its own — so every lock is let go.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Hub.DisconnectAllAsync("The server stopped.").ConfigureAwait(false);
        }
        finally
        {
            Hub.DetachAll();
            if (_server is not null) await _server.StopAsync().ConfigureAwait(false);
        }
        if (_idle is not null) { try { await _idle.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        if (_panels is not null) await _panels.ConfigureAwait(false);
        if (_request.Opener.UnloadsIdle) _request.Manager.Shutdown(TimeSpan.FromSeconds(15));
    }

    // THE SIDE PANELS, refreshed each second; a tab is sent its panel only when it changed.
    private async Task PanelLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
                try { Hub.PushPanels(); }
                catch (Exception) { /* a session closing mid-read; the next tick reads what is left */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task IdleLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), _request.Clock, _stop.Token).ConfigureAwait(false);
            Hub.CheckIdle();
        }
    }

    private async Task HandleAsync(HttpExchange x)
    {
        var r = x.Request;

        // DNS REBINDING — see HostCheck. With a login, a page from elsewhere has no cookie; without one, the Host is
        // the only thing it cannot fake.
        if (!_gate.LoginRequired && !HostCheck.IsKnown(r.Header("Host"), _knownHosts))
        {
            await x.RespondAsync(421, Encoding.UTF8.GetBytes("Unknown host. Reach this server by its own address.")).ConfigureAwait(false);
            return;
        }

        var token = LoginGate.TokenFromCookieHeader(r.Header("Cookie"));
        var user = _gate.UserFor(token);
        var allowed = !_gate.LoginRequired || user is not null;

        switch (r.Method, r.Path)
        {
            case ("GET", "/ws"):
                if (!allowed)
                {
                    await x.RespondAsync(401, Encoding.UTF8.GetBytes("Log in first.")).ConfigureAwait(false);
                    return;
                }
                if (await WebSocketUpgrade.AcceptAsync(x).ConfigureAwait(false) is { } socket)
                    await Hub.ServeAsync(socket, user, token, _stop.Token).ConfigureAwait(false);
                return;

            case ("POST", "/login"):
                await LoginAsync(x).ConfigureAwait(false);
                return;

            case ("POST", "/logout"):
                if (!WebSocketUpgrade.OriginMatchesHost(r))
                {
                    await x.RespondAsync(403, Encoding.UTF8.GetBytes("This page is not cxagent's.")).ConfigureAwait(false);
                    return;
                }
                if (token is not null)
                {
                    _gate.Logout(token);
                    await Hub.DisconnectTokenAsync(token).ConfigureAwait(false);
                }
                await Redirect(x, "/", LoginGate.ClearedCookie(x.Secure)).ConfigureAwait(false);
                return;

            case ("GET", _):
                // THE APP'S PAGE ONLY TO A LOGGED-IN BROWSER; the login page otherwise. The scripts and styles are the
                // same for everyone and hold nothing.
                var path = r.Path is "/" or "/index.html" && !allowed ? "/login.html" : r.Path;
                if (StaticAssets.TryGet(path, out var body, out var type))
                    await x.RespondAsync(200, body, type).ConfigureAwait(false);
                else
                    await x.RespondAsync(404, Encoding.UTF8.GetBytes("Not found.")).ConfigureAwait(false);
                return;

            default:
                await x.RespondAsync(405, Encoding.UTF8.GetBytes("Not allowed.")).ConfigureAwait(false);
                return;
        }
    }

    private async Task LoginAsync(HttpExchange x)
    {
        // ONLY FROM THIS SERVER'S OWN PAGE. Another site could otherwise post guesses from the victim's browser — locking
        // the owner out under the victim's address — or log the victim into an account of the attacker's choosing.
        if (!WebSocketUpgrade.OriginMatchesHost(x.Request))
        {
            await x.RespondAsync(403, Encoding.UTF8.GetBytes("This page is not cxagent's.")).ConfigureAwait(false);
            return;
        }
        if (FormBody.Parse(x.Request.Body) is not { } form
            || !form.TryGetValue("user", out var user) || !form.TryGetValue("password", out var password))
        {
            await x.RespondAsync(400, Encoding.UTF8.GetBytes("Malformed login form.")).ConfigureAwait(false);
            return;
        }

        // THE WAIT IS SAID, NOT SERVED: holding the request open for it would hold a connection per attempt.
        var result = _gate.Login(user, password, x.Request.Remote);
        if (result.Token is { } issued)
            await Redirect(x, "/", LoginGate.Cookie(issued, x.Secure)).ConfigureAwait(false);
        else
            await Redirect(x, result.RetryAfter > TimeSpan.Zero ? "/?login=wait" : "/?login=failed", null).ConfigureAwait(false);
    }

    private static Task Redirect(HttpExchange x, string location, string? cookie)
    {
        List<KeyValuePair<string, string>> headers = [new("Location", location)];
        if (cookie is not null) headers.Add(new("Set-Cookie", cookie));
        return x.RespondAsync(303, ReadOnlyMemory<byte>.Empty, headers: headers);
    }
}

/// <summary>
/// A login form's body, read strictly: <c>application/x-www-form-urlencoded</c>, each field once, valid UTF-8.
///
/// <para>STRICT, so there is exactly one reading of which user was checked — a lenient parser that took the first of
/// two <c>user</c> fields while something in front took the last would disagree with it.</para>
/// </summary>
internal static class FormBody
{
    public static Dictionary<string, string>? Parse(byte[] body)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var text = Encoding.ASCII.GetString(body);
        if (text.Any(c => c > '~')) return null;
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) return null;
            if (Decode(pair[..eq]) is not { } name || Decode(pair[(eq + 1)..]) is not { } value) return null;
            if (!fields.TryAdd(name, value)) return null;
        }
        return fields;
    }

    private static string? Decode(string s)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '+') bytes.Add((byte)' ');
            else if (c == '%')
            {
                if (i + 2 >= s.Length || !byte.TryParse(s.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)) return null;
                bytes.Add(b);
                i += 2;
            }
            else bytes.Add((byte)c);
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString([.. bytes]);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
