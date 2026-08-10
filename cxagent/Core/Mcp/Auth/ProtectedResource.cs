using System.Net.Http.Headers;
using System.Text.Json;

namespace CxAgent.Core.Mcp.Auth;

/// <summary>What an MCP server's 401 says about how to authorize against it (RFC 9728).</summary>
/// <param name="Resource">The canonical URI of the server itself — the RFC 8707 <c>resource</c>.</param>
/// <param name="AuthorizationServers">Who can issue tokens for it, in the order the document listed.</param>
public sealed record ProtectedResourceMetadata(
    string Resource,
    IReadOnlyList<string> AuthorizationServers);

/// <summary>Where to send a user, and where to redeem the code they come back with (RFC 8414).</summary>
public sealed record AuthorizationServerMetadata(
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? RegistrationEndpoint,
    IReadOnlyList<string> CodeChallengeMethods)
{
    /// <summary>
    /// Whether the server advertises S256 PKCE.
    ///
    /// <para>OAuth 2.1 §7.5.2 makes PKCE a MUST for clients, so we send a challenge regardless — this
    /// only says whether the other end admits to understanding it, which is worth reporting when a
    /// flow fails for reasons that look unrelated.</para>
    /// </summary>
    public bool SupportsS256 => CodeChallengeMethods.Contains("S256", StringComparer.Ordinal);
}

/// <summary>
/// The discovery half of MCP authorization: turning a 401 into the two documents that say where to
/// authorize.
///
/// <para>Separated from the flow that uses them because it is independently testable — no browser, no
/// callback listener, no tokens. A 401 arrives, a header names a metadata document, that document
/// names an authorization server, and that server's own document names its endpoints. Every step can
/// fail, and each failure needs to say WHICH step it was.</para>
///
/// <para>NOTHING HERE THROWS. Discovery failing means "this server cannot be authorized against",
/// which the caller reports as a status — not an exception that ends a turn.</para>
/// </summary>
public static class ProtectedResource
{
    /// <summary>
    /// The metadata URL from a <c>WWW-Authenticate: Bearer resource_metadata="…"</c> header, or null.
    ///
    /// <para>Parsed rather than assumed. The well-known path can be derived from the server's own
    /// URL, but RFC 9728 §5.1 has the server SAY where its document is, and a server whose metadata
    /// lives somewhere else is entitled to be believed.</para>
    /// </summary>
    public static string? MetadataUrlFrom(HttpResponseHeaders headers)
    {
        foreach (var challenge in headers.WwwAuthenticate)
        {
            if (!string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)) continue;
            if (challenge.Parameter is not { } parameters) continue;

            // resource_metadata="https://…", among other comma-separated auth-params.
            foreach (var part in parameters.Split(','))
            {
                var trimmed = part.Trim();
                if (!trimmed.StartsWith("resource_metadata", StringComparison.OrdinalIgnoreCase)) continue;

                var eq = trimmed.IndexOf('=');
                if (eq < 0) continue;

                var value = trimmed[(eq + 1)..].Trim().Trim('"');
                if (value.Length > 0) return value;
            }
        }
        return null;
    }

    /// <summary>Fetches the RFC 9728 document a 401 pointed at.</summary>
    public static async Task<(ProtectedResourceMetadata? Metadata, string? Error)> FetchResourceAsync(
        HttpClient http, string url, CancellationToken ct)
    {
        var (root, error) = await FetchJsonAsync(http, url, ct);
        if (error is not null) return (null, $"could not read protected-resource metadata: {error}");

        var resource = String(root!.Value, "resource");
        if (resource is null)
            return (null, "the protected-resource metadata names no 'resource'");

        var servers = Array(root.Value, "authorization_servers");
        if (servers.Count == 0)
            return (null, $"the protected-resource metadata for '{resource}' names no authorization server");

        return (new ProtectedResourceMetadata(resource, servers), null);
    }

    /// <summary>
    /// Fetches an authorization server's RFC 8414 metadata.
    ///
    /// <para>The well-known path is inserted after the ORIGIN and before any path the issuer carries
    /// (RFC 8414 §3.1) — <c>https://host/tenant</c> becomes
    /// <c>https://host/.well-known/oauth-authorization-server/tenant</c>, not the naive concatenation.
    /// Getting this wrong works for every issuer without a path and fails for every multi-tenant
    /// one.</para>
    /// </summary>
    public static async Task<(AuthorizationServerMetadata? Metadata, string? Error)> FetchAuthorizationServerAsync(
        HttpClient http, string issuer, CancellationToken ct)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri))
            return (null, $"'{issuer}' is not a usable authorization server URL");

        var path = uri.AbsolutePath.TrimEnd('/');
        var url = $"{uri.Scheme}://{uri.Authority}/.well-known/oauth-authorization-server{path}";

        var (root, error) = await FetchJsonAsync(http, url, ct);
        if (error is not null)
            return (null, $"could not read authorization server metadata from {url}: {error}");

        var authorize = String(root!.Value, "authorization_endpoint");
        var token = String(root.Value, "token_endpoint");
        if (authorize is null || token is null)
            return (null, $"{url} is missing an authorization_endpoint or token_endpoint");

        return (new AuthorizationServerMetadata(
            String(root.Value, "issuer") ?? issuer,
            authorize,
            token,
            String(root.Value, "registration_endpoint"),
            Array(root.Value, "code_challenge_methods_supported")), null);
    }

    private static async Task<(JsonElement? Root, string? Error)> FetchJsonAsync(
        HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return (null, $"HTTP {(int)response.StatusCode}");

            var text = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            return (doc.RootElement.Clone(), null);
        }
        catch (Exception ex)
        {
            // A DNS failure, a TLS problem, a body that is not JSON. All of them mean the same thing
            // to the caller — discovery did not work — and none of them should escape as an exception.
            return (null, ex.Message);
        }
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static IReadOnlyList<string> Array(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return [];

        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                list.Add(s);
        return list;
    }
}
