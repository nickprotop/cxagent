using CxAgent.Core.Llm;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// Pure transforms over the configured MCP servers, plus the interactive editor.
///
/// <para>Shaped after <see cref="ProviderCatalogEditor"/> rather than inventing a second idiom: the
/// problem is the same one already solved there — a list of named instances with add, edit and
/// remove, driven by <see cref="FlowDialogs.ChooseAsync"/> and returning a NEW settings record
/// instead of mutating one. The transforms are separated from the UI for the same reason: the flow
/// needs a live window, the decisions do not.</para>
///
/// <para>CONFIG ONLY, NO LIVE STATE. A row is name, command and enabled — never connected/failed.
/// The dialog takes no runtime state, and a server the user just typed in has no connection to
/// report by definition. What is actually live belongs to the session panel and <c>/mcp</c>; showing
/// it in both would duplicate state across two surfaces and let them drift.</para>
/// </summary>
public static class McpServerEditor
{
    public static ProviderSettings AddOrReplace(
        ProviderSettings existing, string name, McpServerConfig cfg)
    {
        var servers = new Dictionary<string, McpServerConfig>(existing.McpServers) { [name] = cfg };
        return existing with { McpServers = servers };
    }

    public static ProviderSettings RemoveServer(ProviderSettings existing, string name)
    {
        var servers = new Dictionary<string, McpServerConfig>(existing.McpServers);
        return servers.Remove(name) ? existing with { McpServers = servers } : existing;
    }

    /// <summary>
    /// Flips a server on or off WITHOUT touching its command.
    ///
    /// <para>The common case is "not now", and the reason people never switch a server back on is
    /// having to retype an npx command line from memory. Deleting is a separate, deliberate act.</para>
    /// </summary>
    public static ProviderSettings SetEnabled(ProviderSettings existing, string name, bool enabled)
    {
        if (!existing.McpServers.TryGetValue(name, out var cfg)) return existing;
        return AddOrReplace(existing, name, cfg with { Enabled = enabled });
    }

    /// <summary>
    /// Why this command line cannot be used, or null when it can.
    ///
    /// <para>Rejected HERE, where the user is looking at the field and can fix it, rather than at
    /// load time on the next launch — where it becomes a warning about a server that silently does
    /// not appear.</para>
    /// </summary>
    public static string? Validate(string? command) =>
        string.IsNullOrWhiteSpace(command) ? "A command is required, for example: npx -y some-mcp-server"
                                           : null;

    /// <summary>
    /// Splits a typed command line into argv on whitespace.
    ///
    /// <para>NO SHELL, AND NO QUOTE HANDLING. argv goes straight to the process, so a command line is
    /// only ever a list of words — introducing quoting rules here would imply a shell we deliberately
    /// do not run, and the difference would surface as an argument silently arriving with quotes
    /// still attached.</para>
    /// </summary>
    public static IReadOnlyList<string> ParseCommand(string command) =>
        command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The command as one line, for showing back in an edit field.</summary>
    public static string FormatCommand(IReadOnlyList<string> command) => string.Join(' ', command);

    /// <summary>
    /// One row per configured server, each keeping its NAME beside the display line.
    ///
    /// <para>The name is kept separately so the editor never has to parse it back out of formatted
    /// text — a server named with the separator in it would otherwise be recovered wrongly, the same
    /// trap <see cref="ProviderCatalogEditor.DescribeRows"/> avoids.</para>
    /// </summary>
    public static IReadOnlyList<(string Name, string Line)> DescribeRows(ProviderSettings settings) =>
        settings.McpServers
            .Select(kv => (kv.Key,
                $"{kv.Key} — {FormatCommand(kv.Value.Command)}"
                    + (kv.Value.Enabled ? "" : "  (disabled)")))
            .ToList();

    /// <summary>
    /// The per-server action menu. Returns the updated settings, or null when nothing changed.
    /// </summary>
    internal static async Task<ProviderSettings?> EditServerAsync(
        ConsoleWindowSystem ws, Window? parent, ProviderSettings settings, string name,
        CancellationToken ct)
    {
        if (!settings.McpServers.TryGetValue(name, out var cfg)) return null;

        var toggle = cfg.Enabled ? "Disable" : "Enable";
        var action = await FlowDialogs.ChooseAsync(
            ws, parent, name, ["Change command…", toggle, "Remove"], ct);
        if (action is null) return null;

        switch (action)
        {
            case "Change command…":
            {
                var line = await FlowDialogs.AskAsync(
                    ws, parent, "Command", $"Command for '{name}':", FormatCommand(cfg.Command), ct);
                if (line is null) return null;
                if (Validate(line) is not null) return null;

                var parsed = ParseCommand(line);
                if (parsed.Count == 0 || FormatCommand(parsed) == FormatCommand(cfg.Command)) return null;
                return AddOrReplace(settings, name, cfg with { Command = parsed });
            }

            case "Enable":
                return SetEnabled(settings, name, true);

            case "Disable":
                return SetEnabled(settings, name, false);

            case "Remove":
            {
                var confirm = await FlowDialogs.ChooseAsync(
                    ws, parent, $"Remove '{name}'?", ["Remove"], ct);
                return confirm is not null ? RemoveServer(settings, name) : null;
            }

            default:
                return null;
        }
    }

    /// <summary>Adds a server: a name, then a command line. Null when either is dismissed.</summary>
    internal static async Task<ProviderSettings?> AddServerAsync(
        ConsoleWindowSystem ws, Window? parent, ProviderSettings settings, CancellationToken ct)
    {
        var name = await FlowDialogs.AskAsync(
            ws, parent, "Add MCP server", "Name (prefixes this server's tools):", "", ct);
        if (string.IsNullOrWhiteSpace(name)) return null;

        var line = await FlowDialogs.AskAsync(
            ws, parent, "Add MCP server", $"Command for '{name.Trim()}':",
            "npx -y ", ct);
        if (line is null || Validate(line) is not null) return null;

        var parsed = ParseCommand(line);
        if (parsed.Count == 0) return null;

        return AddOrReplace(settings, name.Trim(), new McpServerConfig(parsed));
    }
}
