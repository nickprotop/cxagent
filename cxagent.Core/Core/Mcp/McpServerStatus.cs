namespace CxAgent.Core.Mcp;

/// <summary>
/// What to say about one configured MCP server, for the surfaces that report state rather than
/// change it — the session panel and <c>/mcp</c>.
///
/// <para>ONE TYPE FOR BOTH, so the panel and the command can never disagree about whether a server is
/// alive. They differ in how much they show, not in what they believe: the panel is ~24 columns and
/// shows a name and a count; <c>/mcp</c> has a whole transcript line and shows the error too.</para>
///
/// <para>A FAILED SERVER IS A STATUS, NOT AN ABSENCE. Omitting it would be indistinguishable from
/// never having configured it — and the user did configure it, so silence is the one outcome that
/// leaves them nothing to act on.</para>
/// </summary>
/// <param name="Name">The configured name, which also prefixes its tools.</param>
/// <param name="Enabled">False when switched off in config — deliberate, and reported as such.</param>
/// <param name="ToolCount">How many tools it offered. Zero on a server that never handshook.</param>
/// <param name="Error">Why it is not usable, or null when it is.</param>
/// <param name="NeedsAuth">Whether the failure is a missing login — the one error a user can fix with /mcp.</param>
public sealed record McpServerStatus(string Name, bool Enabled, int ToolCount, string? Error,
    bool NeedsAuth = false)
{
    /// <summary>True when the server handshook and offered tools — the only state in which its tools
    /// are actually callable this session.</summary>
    public bool IsConnected => Enabled && Error is null && ToolCount > 0;
}
