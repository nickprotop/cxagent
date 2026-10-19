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
/// default passes on, the reviewer is cleared, an agent type's default passes to its next provider or it
/// loses one choice or it runs on the session's model — so nothing changes that the user was not shown.</para>
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
        {
            if (!cfg.Lists(name)) continue;
            // WHAT THE TYPE RUNS ON AFTERWARDS, said before the user confirms — the next default can be
            // a paid provider, and finding that out from a bill is the worst way.
            // BY THE LIST AS WRITTEN, which a disabled type still has: removing its bad first entry can
            // ENABLE it, on a default the user should see first.
            string NameIn(string w) => AgentTypeConfig.SplitEntry(w, settings.Providers.ContainsKey).Name;
            var written = cfg.Written ?? cfg.Providers ?? (cfg.Provider is { } single ? [single] : []);
            var rest = written.Select(NameIn).Where(n => n != name).Distinct().ToList();
            var wasDefault = written.Count > 0 && NameIn(written[0]) == name;
            var next = rest.Count == 0 ? null
                : settings.Providers.TryGetValue(rest[0], out var c) ? $"{rest[0]}:{c.Model}" : rest[0];
            lines.Add(next is null ? $"agent type '{type}' → runs on the session's model"
                : wasDefault ? $"agent type '{type}' → its default becomes '{next}'"
                : $"agent type '{type}' → no longer offers '{name}'");
        }
        if (lines.Count == 0) lines.Add("Nothing else in config.json names it.");
        return new ProviderRemoval(false, lines);
    }
}
