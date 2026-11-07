namespace CxAgent.UI;

/// <summary>One fact of a plugin's detail: STATE, NEEDS, CONTRACT, TOOLS — or, with an empty key, a second line under
/// the one before it.</summary>
/// <param name="Key">The label, or empty for a continuation.</param>
/// <param name="Value">What it says.</param>
public sealed record PluginFact(string Key, string Value);

/// <summary>
/// The facts a plugin's detail shows — the state, what it needs, its contract and whether its tools ask — for F2's
/// details pane and a browser's marketplace alike, so the two describe a plugin in the same words.
/// </summary>
public static class PluginFacts
{
    /// <summary>
    /// The facts for <paramref name="row"/>, each only when there is something true to say, so an undocumented plugin
    /// shows a short block rather than empty labels.
    /// </summary>
    public static IReadOnlyList<PluginFact> For(PluginRow row, PluginManagerInputs inputs)
    {
        var facts = new List<PluginFact> { new("STATE", StateLine(row)) };

        if (row.Catalog is { RequiresDescription: { Length: > 0 } needs } entry)
        {
            facts.Add(new("NEEDS", needs));
            if (entry.RequiresInstall is { Length: > 0 } install) facts.Add(new("", install));

            // ONLY WHEN THE DESCRIPTION DOES NOT ALREADY NAME IT — csharp-lsp's own description is "csharp-ls on PATH",
            // and printing "default: csharp-ls" under that is a stutter.
            if (entry.RequiresDefault is { Length: > 0 } fallback && !needs.Contains(fallback, StringComparison.Ordinal))
                facts.Add(new("", $"default: {fallback}"));
        }

        if (ContractOf(row, inputs) is { } contract) facts.Add(new("CONTRACT", contract.ToString()));
        if (row.Catalog is { } catalogued && ToolsLine(catalogued.Tools) is { } tools) facts.Add(new("TOOLS", tools));
        return facts;
    }

    /// <summary>State · folder · what config says — the whole placement story in one line, because
    /// each third can surprise independently: a loaded plugin can be disabled in config for the
    /// next start, and a project copy can shadow a global one.</summary>
    public static string StateLine(PluginRow row)
    {
        var parts = new List<string>();
        if (row.State.Length > 0) parts.Add(row.State);
        if (row.Folder is { } folder) parts.Add(folder);
        // WHAT THE ENTRY SAYS, NOT "enabled"/"disabled". `enabled` decides auto-load and nothing
        // else — it no longer forbids a hand load — so calling the plugin "disabled" describes a
        // veto that does not exist.
        //
        // AND SAID FOR AN UNCONFIGURED PLUGIN TOO. Having no entry is a different situation from
        // having one that says no, and the line went silent on exactly the case where "there is no
        // entry" is what the reader needs — leaving two different rows reading identically.
        // Folder is the "it is on disk here" fact, so it is also the test for whether a config
        // line is worth printing: a catalog-only row has nothing to say about config yet.
        //
        // NOT SAID TWICE. An unconfigured row's State already ends in "not in config" (the rail
        // shows the same text), so appending it again printed "not in config · global · not in
        // config". The suffix is only added where State has not already said it.
        if (row.Folder is not null)
        {
            if (row.Configured is { } config)
                parts.Add(config.Enabled ? "in config, auto-load" : "in config, no auto-load");
            else if (!row.State.Contains("not in config", StringComparison.Ordinal))
                parts.Add("not in config");
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "not installed";
    }

    /// <summary>The installed sidecar's contract when there is one, else the catalog's claim —
    /// the sidecar wins because it describes the binary actually on disk.</summary>
    public static int? ContractOf(PluginRow row, PluginManagerInputs inputs)
    {
        if (inputs.InstalledContracts.TryGetValue(row.Name, out var installed))
            return installed;

        return row.Catalog is { PluginContract: > 0 } entry ? entry.PluginContract : null;
    }

    /// <summary>
    /// The tool summary, one line — so gating is visible without opening anything. A user who never
    /// looks further still learns whether these tools ask before running.
    /// </summary>
    public static string? ToolsLine(IReadOnlyList<CatalogTool> tools)
    {
        if (tools.Count == 0) return null;

        var asks = tools.Count(t => string.Equals(t.Gated, "true", StringComparison.OrdinalIgnoreCase));
        var decides = tools.Count(t => string.Equals(t.Gated, "dynamic", StringComparison.OrdinalIgnoreCase));
        var never = tools.Count - asks - decides;

        if (decides == tools.Count)
            return tools.Count == 1 ? "1, decides per call whether to ask"
                                    : $"{tools.Count}, all decide per call whether to ask";
        if (asks == tools.Count)
            return tools.Count == 1 ? "1, asks before running" : $"{tools.Count}, all ask before running";
        if (never == tools.Count)
            return tools.Count == 1 ? "1, never asks" : $"{tools.Count}, none ask before running";

        var parts = new List<string>();
        if (asks > 0) parts.Add($"{asks} ask first");
        if (decides > 0) parts.Add($"{decides} decide per call");
        if (never > 0) parts.Add($"{never} never ask");
        return $"{tools.Count} · {string.Join(" · ", parts)}";
    }
}
