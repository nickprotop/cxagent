namespace CxAgent.Core.Mcp;

using System.Text.Json;

/// <summary>
/// One MCP server, however we reach it.
///
/// <para>This exists so <see cref="McpToolset"/> depends on what a server OFFERS rather than on how
/// it is spoken to. Today the only implementation is <see cref="McpClient"/> over stdio; an HTTP
/// transport is a second implementation and nothing downstream of this interface changes when it
/// arrives.</para>
///
/// <para>It is also what lets the toolset's tests drive a fake instead of a subprocess. The wire is
/// <see cref="McpClient"/>'s risk and is tested against a real process; naming, collision and
/// dispatch are pure logic, and testing those through a live pipe would only make the failures
/// slower and less specific.</para>
///
/// <para>NOTHING HERE THROWS. A server that is broken reports it through <see cref="Error"/> and
/// offers no tools; the session carries on without them.</para>
/// </summary>
public interface IMcpServer
{
    /// <summary>The configured name, which prefixes every tool this server offers.</summary>
    string Name { get; }

    /// <summary>The server's own usage prose from <c>initialize</c>, or null if it sent none.</summary>
    string? Instructions { get; }

    /// <summary>Why the server is not usable, or null while it is.</summary>
    string? Error { get; }

    /// <summary>The tools this server currently offers, empty when it has none or cannot say.</summary>
    IReadOnlyList<McpToolDef> Tools { get; }

    /// <summary>Runs one tool and returns its output as text for the model to read.</summary>
    Task<string> CallToolAsync(string tool, JsonElement arguments, CancellationToken ct);
}

/// <summary>
/// A server that has to be brought up before it can be used, and taken down afterwards.
///
/// <para>Separate from <see cref="IMcpServer"/> because the two audiences differ:
/// <see cref="McpToolset"/> only ever ASKS a server things and must not be able to start or stop
/// one, while the launcher and the session own the lifetime. Splitting them is what stops a
/// tool-dispatch path from accidentally killing a subprocess.</para>
/// </summary>
public interface IMcpConnection : IMcpServer, IAsyncDisposable
{
    /// <summary>Brings the server up and completes the handshake. False means it is not usable.</summary>
    Task<bool> StartAsync(CancellationToken ct);

    /// <summary>Asks the server what it offers, refreshing <see cref="IMcpServer.Tools"/>.</summary>
    Task<IReadOnlyList<McpToolDef>> ListToolsAsync(CancellationToken ct);
}
