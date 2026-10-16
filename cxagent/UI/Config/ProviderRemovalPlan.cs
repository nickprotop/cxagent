using CxAgent.Core.Llm;

namespace CxAgent.UI.Config;

/// <summary>What removing a provider would do: refused, or each reference and what becomes of it.</summary>
/// <param name="Refused">An open tab is on it.</param>
/// <param name="Lines">The tabs that refuse it, or one line per reference.</param>
public sealed record ProviderRemoval(bool Refused, IReadOnlyList<string> Lines);

/// <summary>
/// Removing a provider, decided before anything is written.
///
/// <para>REFUSED WHILE A TAB IS ON IT: the tab would be left talking to an instance the file no longer
/// has, with nowhere sensible to move it. The user moves those tabs with /model first, which is a choice
/// this dialog cannot make for them.</para>
///
/// <para>OTHERWISE EVERY REFERENCE IS NAMED in the confirmation, with what the edit does to it — the
/// default passes on, the reviewer is cleared, an agent type falls back to the session's provider — so
/// nothing changes that the user was not shown.</para>
/// </summary>
public static class ProviderRemovalPlan
{
    public static ProviderRemoval For(ProviderSettings settings, string name, IReadOnlyList<string> tabsUsingIt)
    {
        if (tabsUsingIt.Count > 0)
            return new ProviderRemoval(true,
                [$"'{name}' is in use by: {string.Join(", ", tabsUsingIt)}. Switch those tabs with /model first."]);

        var lines = new List<string>();
        if (settings.DefaultProvider == name)
        {
            var next = settings.Providers.Keys.Where(k => k != name).Order(StringComparer.Ordinal).FirstOrDefault();
            lines.Add(next is null ? "default → none left" : $"default → passes to '{next}'");
        }
        if (settings.Classifier == name) lines.Add("auto-mode reviewer → cleared; auto mode is not offered");
        foreach (var (type, cfg) in settings.AgentTypes.OrderBy(t => t.Key, StringComparer.Ordinal))
            if (cfg.Provider == name) lines.Add($"agent type '{type}' → uses the session's provider");
        if (lines.Count == 0) lines.Add("Nothing else in config.json names it.");
        return new ProviderRemoval(false, lines);
    }
}
