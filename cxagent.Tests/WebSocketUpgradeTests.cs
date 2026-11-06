using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using CxAgent.Web.Http;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Turning an HTTP request into a WebSocket: the handshake, and the cross-site upgrades it refuses.</summary>
public sealed class WebSocketUpgradeTests : IAsyncLifetime
{
    private HttpServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = new HttpServer(new HttpServerOptions(IPAddress.Loopback, 0), async x =>
        {
            if (x.Request.Path != "/ws")
            {
                await x.RespondAsync(404, Encoding.UTF8.GetBytes("no"));
                return;
            }
            if (await WebSocketUpgrade.AcceptAsync(x) is not { } ws) return;

            // ECHO, until the client closes.
            var buffer = new byte[1024];
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    break;
                }
                await ws.SendAsync(buffer.AsMemory(0, r.Count), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        });
        await _server.StartAsync();
    }

    public async Task DisposeAsync() => await _server.StopAsync();

    private static HttpRequest Request(params (string Name, string Value)[] headers) =>
        new("GET", "/ws", "", headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase), [], IPAddress.Loopback);

    [Fact]
    public void Accept_key_matches_rfc_example() =>
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", WebSocketUpgrade.AcceptKey("dGhlIHNhbXBsZSBub25jZQ=="));

    [Theory]
    [InlineData("http://localhost:5180", "localhost:5180", true)]
    [InlineData("https://box.lan:5180", "box.lan:5180", true)]
    [InlineData("http://evil.example", "localhost:5180", false)]
    [InlineData("http://localhost:5180.evil.example", "localhost:5180", false)]
    [InlineData("null", "localhost:5180", false)]
    public void Origin_must_be_this_host(string origin, string host, bool ok) =>
        Assert.Equal(ok, WebSocketUpgrade.OriginMatchesHost(Request(("Origin", origin), ("Host", host))));

    [Fact]
    public void Missing_origin_is_refused() =>
        Assert.False(WebSocketUpgrade.OriginMatchesHost(Request(("Host", "localhost:5180"))));

    private async Task<string> RawUpgrade(string origin)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _server.Bound.Port);
        var s = client.GetStream();
        var host = $"localhost:{_server.Bound.Port}";
        await s.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET /ws HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + $"Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\nOrigin: {origin}\r\n\r\n"));
        var buffer = new byte[2048];
        var n = await s.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.ASCII.GetString(buffer, 0, n);
    }

    [Fact]
    public async Task Cross_origin_upgrade_is_403() =>
        Assert.StartsWith("HTTP/1.1 403", await RawUpgrade("http://evil.example"), StringComparison.Ordinal);

    [Fact]
    public async Task Same_origin_upgrade_is_101() =>
        Assert.StartsWith("HTTP/1.1 101", await RawUpgrade($"http://localhost:{_server.Bound.Port}"), StringComparison.Ordinal);

    [Fact]
    public async Task Echo_round_trip_over_real_socket()
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", $"http://localhost:{_server.Bound.Port}");
        await ws.ConnectAsync(new Uri($"ws://localhost:{_server.Bound.Port}/ws"), CancellationToken.None);
        await ws.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[64];
        var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal("hello", Encoding.UTF8.GetString(buffer, 0, r.Count));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }
}
