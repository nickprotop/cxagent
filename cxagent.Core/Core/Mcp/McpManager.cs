using CxAgent.Core.Llm;

namespace CxAgent.Core.Mcp;

/// <summary>
/// Owns the running MCP servers for the life of the session, and can swap them without a restart.
///
/// <para>WHY THIS EXISTS: config used to be read once at startup, so a server added in Settings did
/// nothing until the app was restarted — the user changed a setting, saw no error, and got no
/// server. Everything needed to avoid that was already in place: the agent asks the toolset for
/// tools and instructions on EVERY prompt, so replacing the toolset's servers is enough for the next
/// turn to see them. This is the piece that does the replacing.</para>
///
/// <para>The fleet belongs to the SESSION, not to any one <c>AgentHost</c> — an F5 provider swap
/// rebuilds the host and must not kill the servers.</para>
/// </summary>
public sealed class McpManager : IAsyncDisposable
{
    private readonly Permissions.IPermissionGate _gate;
    private readonly List<IMcpConnection> _servers = [];

    /// <summary>Serialises reloads. Two concurrent ones would race to dispose each other's servers,
    /// and the loser's processes would survive as orphans.</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>What the agent reads every prompt. One instance for the session's whole life, so the
    /// reference handed to each <c>AgentHost</c> stays valid across a reload.</summary>
    public McpToolset Toolset { get; }

    /// <summary>The last reload's per-server messages, for the transcript and <c>/mcp</c>.</summary>
    public IReadOnlyList<string> Messages { get; private set; } = [];

    /// <summary>What config asked for, as of the last reload.</summary>
    public IReadOnlyDictionary<string, McpServerConfig> Configured { get; private set; } =
        new Dictionary<string, McpServerConfig>();

    /// <param name="accessToken">
    /// A stored OAuth token for a server, by name — supplied by the caller that owns the token store,
    /// so this type never touches disk.
    /// </param>
    /// <param name="gate">Every MCP call passes through this before it runs.</param>
    public McpManager(Permissions.IPermissionGate gate, Func<string, string?>? accessToken = null)
    {
        _gate = gate;
        _accessToken = accessToken;
        Toolset = new McpToolset([], gate);
    }

    private readonly Func<string, string?>? _accessToken;

    /// <summary>The live servers, for anything that needs to ask one something directly.</summary>
    public IReadOnlyList<IMcpConnection> Servers => _servers;

    /// <summary>
    /// Where a server said its authorization metadata lives, if it answered 401 and said.
    ///
    /// <para>Read from the server rather than derived, so <c>/mcp login</c> does not have to guess a
    /// well-known path the server is entitled to have moved.</para>
    /// </summary>
    public string? AuthMetadataUrlFor(string name) =>
        (_servers.FirstOrDefault(s => s.Name == name) as McpHttpClient)?.AuthMetadataUrl;

    /// <summary>
    /// Stops everything currently running and starts what the given config describes.
    ///
    /// <para>A FULL RESTART OF THE FLEET, not a diff. Working out which entries changed means
    /// comparing commands, environments, working directories, urls and headers — and getting that
    /// wrong leaves a server running with stale settings, which is indistinguishable from the new
    /// ones having been applied. Restarting everything is slower and always correct; these are
    /// subprocesses and HTTP handshakes, not something a user does in a loop.</para>
    /// </summary>
    public async Task ReloadAsync(IReadOnlyDictionary<string, McpServerConfig> configured,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            foreach (var server in _servers)
                try { await server.DisposeAsync(); } catch (Exception) { /* it is going away */ }
            _servers.Clear();

            var result = await McpLauncher.StartAsync(configured, ct, _accessToken);
            _servers.AddRange(result.Servers);

            Configured = configured;
            Messages = result.Messages;

            // The toolset is REPLACED, not rebuilt: the agent holds this exact instance, and handing
            // it a new object would leave it reading the old one for the rest of the session.
            Toolset.Replace(result.Servers);
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// One row per configured server, pairing what was asked for against what is running.
    ///
    /// <para>Both halves are needed: the live clients know tool counts and errors, but a server that
    /// failed to start was disposed and is not among them, while config knows what should exist and
    /// nothing about whether it does.</para>
    /// </summary>
    public IReadOnlyList<McpServerStatus> Statuses()
    {
        var list = new List<McpServerStatus>();
        foreach (var (name, cfg) in Configured)
        {
            var client = _servers.FirstOrDefault(c => c.Name == name);
            list.Add(new McpServerStatus(
                name,
                cfg.Enabled,
                client?.Tools.Count ?? 0,
                !cfg.Enabled ? null
                    : client is null ? "did not start (see the messages above)"
                    : client.Error,
                // A 401 is carried through as its own state: the server is fine, it is waiting to be
                // logged in to, and calling that a failure sends someone to check their config.
                NeedsAuth: (client as McpHttpClient)?.NeedsAuth ?? false));
        }
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers)
            try { await server.DisposeAsync(); } catch (Exception) { /* best effort */ }
        _servers.Clear();
        _lock.Dispose();
    }
}
