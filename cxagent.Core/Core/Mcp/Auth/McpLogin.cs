namespace CxAgent.Core.Mcp.Auth;

/// <summary>
/// Drives one <c>/mcp login</c>: discovery, the browser handoff, the callback, and the exchange.
///
/// <para>THE ONLY PLACE A BROWSER OPENS, and only because the user typed the command. Everything
/// else — a 401 during a turn, a token that expired mid-task — sets a status and stops. An agent
/// that opens a browser on its own initiative, while its user may be away from the machine, is
/// asking for credentials at a moment nobody chose.</para>
///
/// <para>NO TOKEN IS EVER RETURNED IN A MESSAGE. Everything this produces is shown in a transcript;
/// the tokens go straight to the <see cref="TokenStore"/> and the caller gets prose.</para>
/// </summary>
public static class McpLogin
{
    /// <summary>How long to wait for someone to finish in the browser. Generous: a first login may
    /// mean creating an account, and a timeout that expires mid-signup wastes the whole attempt.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    /// <summary>What happened, in words fit for the transcript.</summary>
    /// <param name="Succeeded">Whether tokens were stored.</param>
    /// <param name="Message">What to tell the user — never containing a token.</param>
    /// <param name="AuthorizationUrl">The URL to open, for a caller that shows it as a fallback.</param>
    public sealed record Result(bool Succeeded, string Message, string? AuthorizationUrl = null);

    /// <summary>
    /// Runs the whole flow for one server.
    /// </summary>
    /// <param name="openBrowser">
    /// How to hand the URL to a browser. Injected rather than called directly so this is testable
    /// without one actually opening — and so a headless caller can print the URL instead.
    /// </param>
    /// <param name="http">The client the OAuth exchanges go through.</param>
    /// <param name="tokens">Where the resulting token is stored.</param>
    /// <param name="serverName">Which MCP server this login is for.</param>
    /// <param name="metadataUrl">The authorization-server metadata document to start from.</param>
    /// <param name="clientId">The registered client id.</param>
    /// <param name="clientSecret">Its secret, or null for a public client.</param>
    /// <param name="timeout">How long to wait for the user to finish in the browser.</param>
    /// <param name="ct">Cancels the login.</param>
    public static async Task<Result> RunAsync(
        HttpClient http, TokenStore tokens, string serverName, string metadataUrl,
        string clientId, string? clientSecret, Action<string> openBrowser,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var (resource, error) = await ProtectedResource.FetchResourceAsync(http, metadataUrl, ct);
        if (resource is null) return new Result(false, $"Cannot log in to '{serverName}': {error}");

        // RFC 9728 §7.6 leaves the choice to the client when several are listed. Taking the first and
        // SAYING SO: an invisible choice is one nobody can question when it turns out wrong.
        var issuer = resource.AuthorizationServers[0];
        var note = resource.AuthorizationServers.Count > 1
            ? $" (of {resource.AuthorizationServers.Count} listed)" : "";

        var (server, asError) = await ProtectedResource.FetchAuthorizationServerAsync(http, issuer, ct);
        if (server is null) return new Result(false, $"Cannot log in to '{serverName}': {asError}");

        // The listener is opened BEFORE the browser, or a fast redirect arrives at a closed port.
        using var listener = new CallbackListener();

        var request = OAuthFlow.CreateRequest(server, resource.Resource, clientId, listener.RedirectUri);

        openBrowser(request.AuthorizationUrl);

        var query = await listener.WaitAsync(timeout ?? DefaultTimeout, ct);
        if (query is null)
            return new Result(false,
                $"Login to '{serverName}' timed out — nothing came back from the browser.",
                request.AuthorizationUrl);

        var (code, redirectError) = OAuthFlow.ReadRedirect(query, request.State);
        if (code is null) return new Result(false, $"Login to '{serverName}' failed: {redirectError}");

        var (issued, exchangeError) = await OAuthFlow.ExchangeAsync(
            http, server, request, code, resource.Resource, clientId, clientSecret, ct);
        if (issued is null) return new Result(false, $"Login to '{serverName}' failed: {exchangeError}");

        tokens.Save(serverName, issued);

        // The MESSAGE, not the token. This string is going into a transcript that gets scrolled
        // through, screenshotted and pasted into issues.
        return new Result(true, $"Logged in to '{serverName}' via {issuer}{note}.");
    }

    /// <summary>
    /// A valid access token for a server, refreshing it first when it is due.
    ///
    /// <para>A FAILED REFRESH FORGETS THE TOKENS rather than retrying. The refresh token is dead —
    /// revoked, expired, or for a client that no longer exists — and keeping it would mean every
    /// subsequent call pays a doomed round trip before failing the same way. Forgetting turns it into
    /// "you are not logged in", which is exactly what it is and points at the fix.</para>
    /// </summary>
    public static async Task<string?> ValidTokenAsync(
        HttpClient http, TokenStore tokens, string serverName, string metadataUrl,
        string clientId, string? clientSecret, CancellationToken ct)
    {
        var stored = tokens.Get(serverName);
        if (stored is null) return null;
        if (!stored.NeedsRefresh) return stored.AccessToken;
        if (stored.RefreshToken is null)
        {
            tokens.Remove(serverName);
            return null;
        }

        var (resource, _) = await ProtectedResource.FetchResourceAsync(http, metadataUrl, ct);
        if (resource is null) return stored.AccessToken;   // cannot refresh; the old one may still work

        var (server, _) = await ProtectedResource.FetchAuthorizationServerAsync(
            http, resource.AuthorizationServers[0], ct);
        if (server is null) return stored.AccessToken;

        var (refreshed, _) = await OAuthFlow.RefreshAsync(
            http, server, stored.RefreshToken, resource.Resource, clientId, clientSecret, ct);

        if (refreshed is null)
        {
            tokens.Remove(serverName);
            return null;
        }

        // ROTATION IS HANDLED by storing whatever came back: OAuth 2.1 requires public clients'
        // refresh tokens to rotate, so keeping the old one would work exactly once.
        tokens.Save(serverName, refreshed);
        return refreshed.AccessToken;
    }
}
