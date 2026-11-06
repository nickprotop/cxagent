using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace CxAgent.Web.Http;

/// <summary>
/// The RFC 6455 handshake by hand, then the runtime's own WebSocket over the same connection.
///
/// <para>THE ORIGIN MUST BE THIS SERVER. A browser sends its login cookie with a WebSocket upgrade from ANY page, and
/// a WebSocket is not subject to the same-origin rules a fetch is — so without this check, any site the user visits
/// could open a socket to cxagent with their session and drive an agent that runs commands on the machine.</para>
/// </summary>
public static class WebSocketUpgrade
{
    private const string Magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>The <c>Sec-WebSocket-Accept</c> value for a client's key.</summary>
    public static string AcceptKey(string secWebSocketKey) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(secWebSocketKey + Magic)));

    /// <summary>
    /// Whether the page asking came from this server: its <c>Origin</c>'s host and port are the request's
    /// <c>Host</c>. A missing or opaque origin is refused — a browser always sends one, and anything else is not a
    /// browser page of ours.
    /// </summary>
    public static bool OriginMatchesHost(HttpRequest request)
    {
        if (request.Header("Origin") is not { } origin || request.Header("Host") is not { } host) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        return string.Equals(uri.Authority, host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Completes the handshake and returns the socket; null after answering 400 (not a WebSocket request) or 403
    /// (another site's page). The caller owns the socket until it returns from its handler.
    /// </summary>
    public static async Task<WebSocket?> AcceptAsync(HttpExchange exchange)
    {
        var r = exchange.Request;
        if (r.Method != "GET"
            || !string.Equals(r.Header("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase)
            || r.Header("Connection")?.Contains("upgrade", StringComparison.OrdinalIgnoreCase) != true
            || r.Header("Sec-WebSocket-Version") != "13"
            || r.Header("Sec-WebSocket-Key") is not { Length: > 0 } key)
        {
            await exchange.RespondAsync(400, Encoding.UTF8.GetBytes("Not a WebSocket request.")).ConfigureAwait(false);
            return null;
        }
        if (!OriginMatchesHost(r))
        {
            await exchange.RespondAsync(403, Encoding.UTF8.GetBytes("This page is not cxagent's.")).ConfigureAwait(false);
            return null;
        }

        await exchange.RespondHeadAsync(101,
        [
            new("Upgrade", "websocket"),
            new("Connection", "Upgrade"),
            new("Sec-WebSocket-Accept", AcceptKey(key)),
        ]).ConfigureAwait(false);
        exchange.Upgrade();

        // A PING EVERY 20 SECONDS keeps a phone's connection from being dropped as idle by whatever sits in between.
        return WebSocket.CreateFromStream(exchange.Stream,
            new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
    }
}
