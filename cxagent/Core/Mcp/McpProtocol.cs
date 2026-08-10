namespace CxAgent.Core.Mcp;

/// <summary>
/// Which MCP revisions this client speaks, and how a disagreement is settled.
///
/// <para>ONE LIST FOR BOTH TRANSPORTS. stdio and HTTP negotiate the same way and must not drift into
/// supporting different sets — a server reachable one way and refused the other, for no reason the
/// user could see, is the kind of difference nobody thinks to test.</para>
///
/// <para>THE LEGACY ERA ONLY, deliberately. Every revision here uses the <c>initialize</c> handshake.
/// <c>2026-07-28</c> removed it — per-request <c>_meta</c>, <c>server/discover</c>, and an
/// <c>UnsupportedProtocolVersionError</c> instead of a counter-offer — and the spec's own
/// compatibility matrix says a legacy client against a modern server simply FAILS, with no
/// fall-forward. That is a second protocol era and a plan of its own; what this file makes us is a
/// well-behaved legacy client, which is what essentially every deployed server speaks today.</para>
/// </summary>
public static class McpProtocol
{
    /// <summary>
    /// Every revision we can speak, newest first.
    ///
    /// <para>All three are the same handshake with more required of the client at each step (RFC 9728
    /// discovery, resource indicators, the version header), and none of that changes how a TOOL call
    /// is framed — which is all this client does. So accepting an older one is honest rather than
    /// merely tolerant.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> Supported =
    [
        "2025-11-25",
        "2025-06-18",
        "2025-03-26",
        "2024-11-05",
    ];

    /// <summary>
    /// What we ASK for. The spec: "the client MUST send a protocol version it supports. This SHOULD
    /// be the LATEST version supported by the client."
    /// </summary>
    public static string Latest => Supported[0];

    /// <summary>Whether a version the server named is one we can actually speak.</summary>
    public static bool IsSupported(string? version) =>
        version is not null && Supported.Contains(version, StringComparer.Ordinal);

    /// <summary>
    /// Settles the handshake: the version to use, or an error explaining why we are disconnecting.
    ///
    /// <para>A server that names nothing is taken at ours. The field is required, but refusing a
    /// working server over a missing string would trade real capability for pedantry.</para>
    /// </summary>
    public static (string? Version, string? Error) Negotiate(string? offered)
    {
        if (string.IsNullOrWhiteSpace(offered)) return (Latest, null);
        if (IsSupported(offered)) return (offered, null);

        // NAMES BOTH SIDES. "unsupported protocol version" alone tells the user nothing they can act
        // on; the two versions together say whether to upgrade cxagent or downgrade the server.
        return (null,
            $"the server requires MCP protocol '{offered}', which this client does not support "
            + $"(it speaks {string.Join(", ", Supported)})");
    }
}
