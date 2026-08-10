using CxAgent.Core.Llm;

namespace CxAgent.Core.Mcp;

/// <summary>
/// Starts the configured MCP servers and hands back the ones that came up.
///
/// <para>NOTHING HERE THROWS AND NOTHING BLOCKS THE APP. A server that will not start, hangs, or
/// dies during its handshake costs its own tools and nothing else — the same contract
/// <see cref="McpClient"/> holds on the wire, applied to the fleet.</para>
/// </summary>
public static class McpLauncher
{
    /// <summary>What came up, and what to tell the user about what did not.</summary>
    public sealed record Result(
        IReadOnlyList<McpClient> Servers,
        IReadOnlyList<string> Messages);

    /// <summary>
    /// Starts every enabled server CONCURRENTLY and lists their tools.
    ///
    /// <para>Concurrently because these are independent subprocesses and one slow npm download would
    /// otherwise delay every server behind it — serial startup makes total wait the SUM of the
    /// slowest, which is how a two-server config takes a minute to become usable.</para>
    ///
    /// <para>A server is only kept if it both started AND listed its tools. One that connects but
    /// cannot say what it offers is not usable, and keeping it would put a permanently empty server
    /// in the panel with no explanation.</para>
    /// </summary>
    public static async Task<Result> StartAsync(
        IReadOnlyDictionary<string, McpServerConfig> configured, CancellationToken ct)
    {
        var servers = new List<McpClient>();
        var messages = new List<string>();

        // A disabled server is a deliberate off switch, not a failure: no attempt, and nothing said
        // about it. Saying "skipped" for something the user switched off is noise.
        var enabled = configured.Where(kv => kv.Value.Enabled).ToList();
        if (enabled.Count == 0) return new Result(servers, messages);

        var started = await Task.WhenAll(enabled.Select(async kv =>
        {
            var (name, cfg) = (kv.Key, kv.Value);
            var client = new McpClient(
                name,
                [.. cfg.Command],
                cfg.TimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                cfg.Environment,
                cfg.WorkingDirectory);

            try
            {
                if (!await client.StartAsync(ct))
                {
                    // The server's OWN error text — "npx: command not found" is something the user
                    // can fix in seconds, and a generic "failed to start" is not.
                    await client.DisposeAsync();
                    return (Client: (McpClient?)null,
                            Message: $"MCP server '{name}' did not start: {client.Error ?? "unknown error"}");
                }

                var tools = await client.ListToolsAsync(ct);
                if (tools.Count == 0)
                {
                    await client.DisposeAsync();
                    return (Client: (McpClient?)null,
                            Message: $"MCP server '{name}' started but offers no tools"
                                   + (client.Error is null ? "" : $": {client.Error}"));
                }

                return (Client: (McpClient?)client, Message: (string?)null);
            }
            catch (Exception ex)
            {
                // Belt and braces. McpClient is written not to throw, but this runs at startup and a
                // surprise here would take the app down before the first paint — the one failure
                // this whole design exists to prevent.
                await client.DisposeAsync();
                return (Client: (McpClient?)null, Message: $"MCP server '{name}' failed: {ex.Message}");
            }
        }));

        // IN CONFIGURED ORDER, not whichever finished first. Task.WhenAll preserves the order of the
        // tasks it was given, so this is already the order `enabled` was in — which keeps the tool
        // list, and therefore the prompt-cache prefix, independent of a startup race.
        foreach (var (client, message) in started)
        {
            if (client is not null) servers.Add(client);
            if (message is not null) messages.Add(message);
        }

        return new Result(servers, messages);
    }
}
