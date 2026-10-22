using CxAgent.Core.Llm;

namespace CxAgent.UI.Config;

/// <summary>One instance on the Providers rail.</summary>
/// <param name="Name">The instance name.</param>
/// <param name="Model">The model it is set to.</param>
/// <param name="Preset">The preset it matches, for display; its kind when it matches none.</param>
/// <param name="IsDefault">New sessions start on it.</param>
/// <param name="IsReviewer">It reviews actions in auto mode.</param>
/// <param name="Tabs">How many open tabs are on it.</param>
/// <param name="Unsaved">It has edits not yet saved.</param>
public sealed record ProviderRow(string Name, string Model, string Preset, bool IsDefault, bool IsReviewer, int Tabs, bool Unsaved);

/// <summary>The rail's rows, from the file and the open tabs.</summary>
public static class ProviderRows
{
    /// <summary>One row per instance, by name — the order the file has no say in, so a stable one.</summary>
    /// <param name="settings">The file, as the loader read it.</param>
    /// <param name="tabsByInstance">How many open tabs each instance has.</param>
    /// <param name="unsaved">The instance with unsaved edits, or null.</param>
    public static IReadOnlyList<ProviderRow> For(
        ProviderSettings settings, IReadOnlyDictionary<string, int> tabsByInstance, string? unsaved) =>
        [.. settings.Providers
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ProviderRow(
                kv.Key, kv.Value.Model, PresetLabel(kv.Value),
                kv.Key == settings.DefaultProvider, kv.Key == settings.Classifier,
                tabsByInstance.TryGetValue(kv.Key, out var tabs) ? tabs : 0,
                kv.Key == unsaved))];

    /// <summary>
    /// The preset an instance matches by kind and endpoint. The file records what was configured, not
    /// which card it came from, so this is presentation only: a miss shows the kind.
    /// </summary>
    public static string PresetLabel(ProviderInstanceConfig cfg) =>
        ProviderKindCatalog.Presets.FirstOrDefault(p => p.Kind == cfg.Kind && p.BaseUrl == cfg.BaseUrl)?.DisplayName
        ?? cfg.Kind;

    /// <summary>
    /// The row's second line: its roles in words, then how many tabs use it, then its model. Words, not
    /// ★ and ◆: a mark needs a legend nobody has, and the Roles block above names the same two roles.
    /// </summary>
    public static string Describe(ProviderRow row)
    {
        var parts = new List<string>();
        if (row.IsDefault) parts.Add("default");
        if (row.IsReviewer) parts.Add("reviewer");
        if (row.Tabs > 0) parts.Add($"{row.Tabs} tab{(row.Tabs == 1 ? "" : "s")}");
        parts.Add(row.Model);
        return string.Join(" · ", parts);
    }
}
