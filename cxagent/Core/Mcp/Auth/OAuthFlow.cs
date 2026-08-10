using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace CxAgent.Core.Mcp.Auth;

/// <summary>One authorization attempt in progress: what to open, and what to check when it returns.</summary>
/// <param name="AuthorizationUrl">Where to send the user.</param>
/// <param name="CodeVerifier">The PKCE secret, kept back until the token request.</param>
/// <param name="State">The value the redirect must echo, or the result is not ours.</param>
/// <param name="RedirectUri">Where the browser comes back to.</param>
public sealed record AuthorizationRequest(
    string AuthorizationUrl, string CodeVerifier, string State, string RedirectUri);

/// <summary>What a successful exchange yields.</summary>
public sealed record OAuthTokens(
    string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt)
{
    /// <summary>
    /// True once the token is within a minute of expiry.
    ///
    /// <para>A MINUTE OF SLACK, not zero. A token that expires between the check and the request
    /// arriving produces a 401 on work the user is waiting for, and refreshing slightly early costs
    /// one extra round trip nobody notices.</para>
    /// </summary>
    public bool NeedsRefresh => ExpiresAt is { } at && DateTimeOffset.UtcNow >= at.AddSeconds(-60);
}

/// <summary>
/// The OAuth 2.1 authorization-code flow, as MCP requires it.
///
/// <para>THE PURE PARTS ARE SEPARATE FROM THE NETWORK. Building the authorization URL and verifying a
/// redirect are where the security-relevant details live — PKCE, <c>state</c>, the RFC 8707
/// <c>resource</c> — and they are testable without a browser, a listener or a server. Only
/// <see cref="ExchangeAsync"/> and <see cref="RefreshAsync"/> touch HTTP.</para>
///
/// <para>Three client-side MUSTs, each pinned by a test:</para>
/// <list type="bullet">
/// <item>PKCE with S256 (OAuth 2.1 §7.5.2) — the verifier never leaves this process until the token
/// request, so an intercepted authorization code alone is useless.</item>
/// <item>The RFC 8707 <c>resource</c> parameter in BOTH the authorization and token requests,
/// <i>"regardless of whether authorization servers support it"</i> — it binds the token to the MCP
/// server it is for, so a malicious server cannot replay it elsewhere.</item>
/// <item>Tokens NEVER in a query string. They travel in the <c>Authorization</c> header, because
/// query strings land in server logs, proxy logs and browser history.</item>
/// </list>
/// </summary>
public static class OAuthFlow
{
    /// <summary>
    /// Builds the URL to open, and the secrets to check the answer against.
    /// </summary>
    public static AuthorizationRequest CreateRequest(
        AuthorizationServerMetadata server, string resource, string clientId, string redirectUri,
        IReadOnlyList<string>? scopes = null)
    {
        // 32 bytes of cryptographic randomness, base64url. The verifier is the whole point of PKCE:
        // it proves the client redeeming the code is the one that requested it.
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["response_type"] = "code";
        query["client_id"] = clientId;
        query["redirect_uri"] = redirectUri;
        query["state"] = state;
        query["code_challenge"] = challenge;
        query["code_challenge_method"] = "S256";

        // RFC 8707. Sent whether or not the server admits to supporting it — the spec is explicit
        // that this is unconditional, because a client cannot tell in advance and the parameter is
        // ignored where unsupported.
        query["resource"] = resource;

        if (scopes is { Count: > 0 }) query["scope"] = string.Join(' ', scopes);

        var separator = server.AuthorizationEndpoint.Contains('?') ? "&" : "?";
        return new AuthorizationRequest(
            server.AuthorizationEndpoint + separator + query, verifier, state, redirectUri);
    }

    /// <summary>
    /// The authorization code from a redirect, or an error explaining why it is not usable.
    ///
    /// <para>THE STATE IS CHECKED BEFORE THE CODE IS TOUCHED. A redirect that does not echo the value
    /// we generated did not come from the request we made — it is either a stale tab or someone
    /// else's, and redeeming its code would attach a stranger's authorization to this session.</para>
    /// </summary>
    public static (string? Code, string? Error) ReadRedirect(string query, string expectedState)
    {
        var parsed = HttpUtility.ParseQueryString(query.TrimStart('?'));

        var state = parsed["state"];
        if (state is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
            return (null, "the redirect did not match this login attempt (state mismatch); ignored");

        // The server's own refusal — "access_denied" when the user clicked Cancel — is worth
        // repeating verbatim rather than flattening into "login failed".
        if (parsed["error"] is { Length: > 0 } error)
            return (null, parsed["error_description"] is { Length: > 0 } d ? $"{error}: {d}" : error);

        var code = parsed["code"];
        return code is { Length: > 0 } ? (code, null) : (null, "the redirect carried no authorization code");
    }

    /// <summary>Redeems the code for tokens.</summary>
    public static Task<(OAuthTokens? Tokens, string? Error)> ExchangeAsync(
        HttpClient http, AuthorizationServerMetadata server, AuthorizationRequest request,
        string code, string resource, string clientId, string? clientSecret, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = request.RedirectUri,
            ["client_id"] = clientId,
            // The verifier, finally — this is what proves we are the client that asked.
            ["code_verifier"] = request.CodeVerifier,
            // AND HERE TOO. RFC 8707 requires the resource in the token request as well as the
            // authorization request; sending it only in the first is a common way to get a token
            // whose audience is unbound.
            ["resource"] = resource,
        };
        if (clientSecret is { Length: > 0 }) form["client_secret"] = clientSecret;

        return PostTokenAsync(http, server.TokenEndpoint, form, ct);
    }

    /// <summary>Trades a refresh token for a fresh access token.</summary>
    public static Task<(OAuthTokens? Tokens, string? Error)> RefreshAsync(
        HttpClient http, AuthorizationServerMetadata server, string refreshToken,
        string resource, string clientId, string? clientSecret, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
            ["resource"] = resource,
        };
        if (clientSecret is { Length: > 0 }) form["client_secret"] = clientSecret;

        return PostTokenAsync(http, server.TokenEndpoint, form, ct);
    }

    private static async Task<(OAuthTokens? Tokens, string? Error)> PostTokenAsync(
        HttpClient http, string endpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(form), ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // The server's own OAuth error, when it sent one. "invalid_grant" says the code was
                // already used or expired; "HTTP 400" says nothing.
                var detail = TryReadError(body);
                return (null, detail ?? $"HTTP {(int)response.StatusCode} from the token endpoint");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var access = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() : null;
            if (string.IsNullOrEmpty(access))
                return (null, "the token endpoint returned no access_token");

            var refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() : null;

            DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var e)
                                     && e.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.UtcNow.AddSeconds(e.GetInt32()) : null;

            return (new OAuthTokens(access!, refresh, expiresAt), null);
        }
        catch (Exception ex)
        {
            // A token endpoint that is unreachable or answers nonsense is a failed login, never an
            // exception out of a command handler.
            return (null, ex.Message);
        }
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var e) || e.ValueKind != JsonValueKind.String)
                return null;

            var error = e.GetString();
            return doc.RootElement.TryGetProperty("error_description", out var d)
                && d.ValueKind == JsonValueKind.String
                ? $"{error}: {d.GetString()}" : error;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>base64url without padding, per RFC 7636 — the encoding PKCE specifies, and one that
    /// survives a URL without escaping.</summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
