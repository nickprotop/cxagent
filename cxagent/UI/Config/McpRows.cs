using CxAgent.Core.Llm;
using CxAgent.Core.Mcp;

namespace CxAgent.UI.Config;

/// <summary>What a configured MCP server is doing now, as the manager reports it.</summary>
public enum McpState { Running, Failed, NeedsLogin, Off, NotStarted }

/// <summary>One server on the MCP rail.</summary>
/// <param name="Name">The server name.</param>
/// <param name="Target">Its command line, or its URL.</param>
/// <param name="State">What it is doing now.</param>
/// <param name="Reason">Why it failed, when it did.</param>
/// <param name="Tools">How many tools it offers.</param>
public sealed record McpRow(string Name, string Target, McpState State, string? Reason, int Tools);

/// <summary>
/// The MCP rail: the file's servers, each with the state the running manager reports.
///
/// <para>READ, NEVER COPIED. The state comes from <see cref="McpManager.Statuses"/> every time the rail
/// is built, so the dialog cannot drift from <c>/mcp</c> and the session panel, which read the same.</para>
/// </summary>
public static class McpRows
{
    public static IReadOnlyList<McpRow> For(
        IReadOnlyDictionary<string, McpServerConfig> configured, IReadOnlyList<McpServerStatus> statuses) =>
        [.. configured.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
        {
            var status = statuses.FirstOrDefault(s => s.Name == kv.Key);
            var state = !kv.Value.Enabled ? McpState.Off
                : status is null ? McpState.NotStarted
                : status.NeedsAuth ? McpState.NeedsLogin
                : status.Error is not null ? McpState.Failed
                : McpState.Running;
            return new McpRow(kv.Key, Target(kv.Value), state, state == McpState.Failed ? status!.Error : null, status?.ToolCount ?? 0);
        })];

    /// <summary>The command as one line, quoting a word with a space in it; or the URL.</summary>
    public static string Target(McpServerConfig cfg) =>
        cfg.IsRemote ? cfg.Url! : string.Join(" ", cfg.Command.Select(w => w.Contains(' ') ? $"\"{w}\"" : w));
}
