using System.Net;
using System.Text;

namespace CxAgent.Core.Mcp.Auth;

/// <summary>
/// A one-shot loopback HTTP server that catches the browser's redirect.
///
/// <para>LOOPBACK, NOT A CUSTOM SCHEME. OAuth 2.1 §8.4.2 prefers <c>http://127.0.0.1:{port}</c> for
/// native apps: the port is claimed by this process for the seconds the login lasts, where a custom
/// URI scheme is registered machine-wide and any other application can claim it.</para>
///
/// <para>127.0.0.1 EXPLICITLY, never <c>localhost</c> — that name can resolve to ::1 or be redirected
/// by a hosts file, and the redirect URI has to match what was registered byte for byte.</para>
///
/// <para>ONE REQUEST AND DONE. It stops as soon as the redirect arrives, so nothing is left listening
/// after a login: an abandoned listener is an open port on the user's machine for the life of the
/// session.</para>
/// </summary>
public sealed class CallbackListener : IDisposable
{
    private readonly HttpListener _listener = new();

    /// <summary>The redirect URI to register — the address the browser will return to.</summary>
    public string RedirectUri { get; }

    public CallbackListener(int port = 0)
    {
        if (port == 0) port = FreePort();

        RedirectUri = $"http://127.0.0.1:{port}/callback";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
    }

    /// <summary>
    /// Waits for the browser, and returns the query string it arrived with.
    ///
    /// <para>Null on timeout, which is the ordinary outcome of a user who closed the tab or never
    /// finished. The caller reports that as a login that did not complete; there is nothing to
    /// retry automatically, because the next attempt needs a new authorization URL anyway.</para>
    /// </summary>
    public async Task<string?> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            var contextTask = _listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, cts.Token));
            if (completed != contextTask) return null;

            var context = await contextTask;
            var query = context.Request.Url?.Query ?? "";

            // THE USER SEES A PAGE, not a blank tab or a connection error. They just approved
            // something and were sent back; "you can close this" is the difference between a finished
            // task and one they are unsure about.
            var succeeded = query.Contains("code=", StringComparison.Ordinal);
            var body = succeeded
                ? "<html><body style=\"font-family:sans-serif;padding:2rem\">"
                  + "<h2>Signed in</h2><p>You can close this tab and return to cxagent.</p></body></html>"
                : "<html><body style=\"font-family:sans-serif;padding:2rem\">"
                  + "<h2>Sign-in failed</h2><p>Return to cxagent for the reason.</p></body></html>";

            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = succeeded ? 200 : 400;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, ct);
            context.Response.Close();

            return query;
        }
        catch (Exception)
        {
            // A cancelled wait or a listener closed under us. Both mean the login did not complete,
            // which is a result rather than something to throw at a command handler.
            return null;
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch (Exception) { }
        try { _listener.Close(); } catch (Exception) { }
    }
}
