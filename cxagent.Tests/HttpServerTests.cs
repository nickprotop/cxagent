using System.Net;
using System.Net.Sockets;
using System.Text;
using CxAgent.Web.Http;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The web server over a real loopback socket: what every response carries, and what it serves.</summary>
public sealed class HttpServerTests : IAsyncLifetime
{
    private HttpServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = new HttpServer(new HttpServerOptions(IPAddress.Loopback, 0), async x =>
        {
            if (StaticAssets.TryGet(x.Request.Path, out var body, out var type))
                await x.RespondAsync(200, body, type);
            else
                await x.RespondAsync(404, Encoding.UTF8.GetBytes("not found"));
        });
        await _server.StartAsync();
    }

    public async Task DisposeAsync() => await _server.StopAsync();

    private async Task<string> Raw(string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _server.Bound.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        client.Client.Shutdown(SocketShutdown.Send);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public void Bound_reports_actual_port() => Assert.NotEqual(0, _server.Bound.Port);

    [Fact]
    public async Task Serves_index_with_security_headers()
    {
        var response = await Raw("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200 OK", response, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/html; charset=utf-8", response, StringComparison.Ordinal);
        Assert.Contains("X-Content-Type-Options: nosniff", response, StringComparison.Ordinal);
        // SAME-ORIGIN, NOT NO-REFERRER: under no-referrer a browser sends "Origin: null" on the login form's own POST,
        // which the Origin check then refuses.
        Assert.Contains("Referrer-Policy: same-origin", response, StringComparison.Ordinal);
        Assert.Contains("X-Frame-Options: DENY", response, StringComparison.Ordinal);
        Assert.Contains("Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'", response, StringComparison.Ordinal);
        Assert.Contains("<html", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_path_404() =>
        Assert.StartsWith("HTTP/1.1 404", await Raw("GET /nope HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"), StringComparison.Ordinal);

    [Fact]
    public async Task A_refused_request_gets_its_status_and_the_connection_closes() =>
        Assert.StartsWith("HTTP/1.1 501", await Raw("POST / HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\n"), StringComparison.Ordinal);

    [Fact]
    public async Task Two_requests_on_one_connection_both_answered()
    {
        var response = await Raw("GET /nope HTTP/1.1\r\nHost: h\r\n\r\nGET / HTTP/1.1\r\nHost: h\r\nConnection: close\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 404", response, StringComparison.Ordinal);
        Assert.Contains("HTTP/1.1 200 OK", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Close_among_other_connection_options_closes()
    {
        var response = await Raw("GET /nope HTTP/1.1\r\nHost: h\r\nConnection: keep-alive, close\r\n\r\nGET / HTTP/1.1\r\nHost: h\r\n\r\n");
        Assert.DoesNotContain("200 OK", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connections_beyond_the_cap_are_refused()
    {
        await using var server = new HttpServer(new HttpServerOptions(IPAddress.Loopback, 0),
            x => x.RespondAsync(200, Encoding.UTF8.GetBytes("ok"))) { MaxConnections = 2 };
        await server.StartAsync();
        var held = new List<TcpClient>();
        for (var i = 0; i < 2; i++)
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, server.Bound.Port);
            held.Add(c);
        }
        await Task.Delay(200);
        using var third = new TcpClient();
        await third.ConnectAsync(IPAddress.Loopback, server.Bound.Port);
        var read = await third.GetStream().ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, read);
        foreach (var c in held) c.Dispose();
    }

    [Fact]
    public async Task A_header_value_with_a_line_break_is_never_written()
    {
        var x = new HttpExchange(new HttpRequest("GET", "/", "", new Dictionary<string, string>(), [], IPAddress.Loopback), new MemoryStream(), false);
        await Assert.ThrowsAsync<ArgumentException>(() => x.RespondHeadAsync(200, [new("X-Echo", "a\r\nSet-Cookie: x=1")]));
    }

    [Fact]
    public async Task Stop_closes_open_connections()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _server.Bound.Port);
        await _server.StopAsync();
        var buffer = new byte[16];
        var read = await client.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, read);
    }
}
