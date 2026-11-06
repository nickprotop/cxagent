using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CxAgent.Web.Http;

/// <summary>Where the server listens, and the certificate that makes it HTTPS when there is one.</summary>
/// <param name="Bind">The address to listen on.</param>
/// <param name="Port">The port; 0 takes any free one, reported by <see cref="HttpServer.Bound"/>.</param>
/// <param name="Certificate">Serve TLS with this certificate; null serves plain HTTP.</param>
public sealed record HttpServerOptions(IPAddress Bind, int Port, X509Certificate2? Certificate = null);

/// <summary>One request and the means to answer it.</summary>
public sealed class HttpExchange(HttpRequest request, Stream stream, bool secure)
{
    /// <summary>The request.</summary>
    public HttpRequest Request { get; } = request;

    /// <summary>The connection, for a handler that takes it over — see <see cref="Upgrade"/>.</summary>
    public Stream Stream { get; } = stream;

    /// <summary>Whether this connection is TLS — what decides a cookie's <c>Secure</c> flag.</summary>
    public bool Secure { get; } = secure;

    /// <summary>
    /// Set by a handler that took the connection over (a WebSocket). The handler returns only when it is done with
    /// the connection; the server then closes it rather than reading another request.
    /// </summary>
    public bool Upgraded { get; private set; }

    /// <summary>Whether a response has been written.</summary>
    public bool Responded { get; private set; }

    /// <summary>Marks the connection as the handler's: nothing more is read from it once the handler returns.</summary>
    public void Upgrade() => Upgraded = true;

    /// <summary>Writes a whole response, with the security headers every response carries.</summary>
    public Task RespondAsync(int status, ReadOnlyMemory<byte> body, string contentType = "text/plain; charset=utf-8",
        IReadOnlyList<KeyValuePair<string, string>>? headers = null) =>
        WriteAsync(status, body, contentType, headers);

    /// <summary>Writes the status line and headers only, for a handler that writes the rest itself (a 101).</summary>
    public async Task RespondHeadAsync(int status, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        Responded = true;
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        foreach (var (name, value) in headers) head.Append(name).Append(": ").Append(value).Append("\r\n");
        head.Append("\r\n");
        await Stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
        await Stream.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteAsync(int status, ReadOnlyMemory<byte> body, string contentType,
        IReadOnlyList<KeyValuePair<string, string>>? extra)
    {
        List<KeyValuePair<string, string>> headers =
        [
            new("Content-Type", contentType),
            new("Content-Length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            .. Security,
            .. extra ?? [],
        ];
        await RespondHeadAsync(status, headers).ConfigureAwait(false);
        await Stream.WriteAsync(body).ConfigureAwait(false);
        await Stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// What every response says about itself.
    ///
    /// <para>THE PAGE RUNS ONLY ITS OWN SCRIPTS. The page shows model output — text a model was told by whatever
    /// it read — so a content policy that forbids inline script, other origins and framing is the backstop behind
    /// the escaping, not a substitute for it.</para>
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> Security =
    [
        new("X-Content-Type-Options", "nosniff"),
        new("Referrer-Policy", "no-referrer"),
        new("X-Frame-Options", "DENY"),
        new("Content-Security-Policy",
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'"),
        new("Cache-Control", "no-store"),
    ];

    private static string Reason(int status) => status switch
    {
        101 => "Switching Protocols",
        200 => "OK",
        303 => "See Other",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        408 => "Request Timeout",
        413 => "Content Too Large",
        431 => "Request Header Fields Too Large",
        501 => "Not Implemented",
        505 => "HTTP Version Not Supported",
        _ => "Status",
    };
}

/// <summary>
/// A small HTTP/1.1 server on the base runtime: a listener, one task per connection, a strict request reader, and
/// TLS when given a certificate.
///
/// <para>NO ASP.NET CORE. It would grow the single-file release by tens of megabytes and make the dotnet tool need a
/// second runtime, for a server whose whole surface is a few static files, a login form and a WebSocket.</para>
/// </summary>
public sealed class HttpServer(HttpServerOptions options, Func<HttpExchange, Task> handle) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _open = [];
    private readonly object _gate = new();
    private TcpListener? _listener;
    private Task? _accepting;

    /// <summary>How long a request may take to arrive — see <see cref="HttpLimits"/>.</summary>
    public HttpLimits Limits { get; init; } = new();

    /// <summary>Where the server actually listens — the real port when 0 was asked for.</summary>
    public IPEndPoint Bound => (IPEndPoint)(_listener ?? throw new InvalidOperationException("Not started.")).LocalEndpoint;

    /// <summary>Starts listening. Throws when the address or port cannot be bound.</summary>
    public Task StartAsync()
    {
        _listener = new TcpListener(options.Bind, options.Port);
        _listener.Start();
        _accepting = AcceptLoopAsync();
        return Task.CompletedTask;
    }

    /// <summary>Stops listening and closes every open connection, upgraded ones included.</summary>
    public async Task StopAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _listener?.Stop();
        TcpClient[] open;
        lock (_gate) open = [.. _open];
        foreach (var c in open) c.Dispose();
        if (_accepting is { } a) await a.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            lock (_gate) _open.Add(client);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            Stream stream = client.GetStream();
            var secure = false;
            if (options.Certificate is { } cert)
            {
                var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                handshake.CancelAfter(Limits.ReadTimeout);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, handshake.Token)
                    .ConfigureAwait(false);
                stream = tls;
                secure = true;
            }

            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
            var reader = new HttpRequestReader(stream, remote, Limits);
            while (!_stop.IsCancellationRequested)
            {
                HttpRequest? request;
                try
                {
                    request = await reader.ReadAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (HttpRefused refused)
                {
                    var x = new HttpExchange(new HttpRequest("", "", "", new Dictionary<string, string>(), [], remote), stream, secure);
                    await x.RespondAsync(refused.Status, Encoding.UTF8.GetBytes(refused.Message),
                        headers: [new("Connection", "close")]).ConfigureAwait(false);
                    return;
                }
                if (request is null) return;

                var exchange = new HttpExchange(request, stream, secure);
                await handle(exchange).ConfigureAwait(false);
                if (exchange.Upgraded) return;
                if (!exchange.Responded)
                    await exchange.RespondAsync(500, Encoding.UTF8.GetBytes("no response")).ConfigureAwait(false);
                if (string.Equals(request.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase)) return;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException
                                      or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            // THE CLIENT WENT AWAY, OR NEVER SPOKE TLS. Nothing to answer, and nobody to answer it to.
        }
        finally
        {
            lock (_gate) _open.Remove(client);
            client.Dispose();
        }
    }
}
