using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using SharpConsoleUI;
using static CxAgent.Core.Llm.ProviderKindCatalog;

namespace CxAgent.UI;

/// <summary>
/// Pure catalog transforms plus the interactive editor. The transforms are separated from the UI so
/// multi-instance behaviour is unit-testable — before this existed, SetupWizard.BuildSettings built
/// a single-entry dictionary, so the UI could never produce the multi-instance catalog the loader
/// had always supported (two OpenRouter accounts plus a local endpoint, say).
///
/// Nothing here keys on kind. Several instances of the SAME kind are a first-class case, so every
/// lookup, suggestion, and removal goes through the instance NAME.
/// </summary>
public static class ProviderCatalogEditor
{
    /// <summary>An empty catalog — the baseline for a first run and for tests.</summary>
    public static ProviderSettings EmptyCatalog() => new(
        new Dictionary<string, ProviderInstanceConfig>(), null,
        Array.Empty<string>(), new Dictionary<string, RoutingTarget>());

    public static ProviderSettings AddOrReplace(
        ProviderSettings existing, string name, ProviderInstanceConfig cfg, bool makeDefault)
    {
        var providers = new Dictionary<string, ProviderInstanceConfig>(existing.Providers) { [name] = cfg };
        // First instance always becomes default: leaving defaultProvider null would make the loader
        // reject the file the UI just wrote.
        var def = makeDefault || existing.DefaultProvider is null ? name : existing.DefaultProvider;
        return existing with { Providers = providers, DefaultProvider = def };
    }

    public static ProviderSettings RemoveInstance(ProviderSettings existing, string name)
    {
        var providers = new Dictionary<string, ProviderInstanceConfig>(existing.Providers);
        if (!providers.Remove(name)) return existing;

        // Ordinal-first rather than Keys.FirstOrDefault(): dictionary enumeration order of a fresh
        // copy is arbitrary, so which instance inherited the default was unpredictable and untestable
        // beyond the two-instance case. Any survivor is valid; a STATED rule is reproducible.
        var def = existing.DefaultProvider == name
            ? providers.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault()
            : existing.DefaultProvider;

        // routing/allowedProviders name instances too, and the loader validates them the same way, so
        // stale entries there are just as fatal as a stale role binding.
        var routing = existing.Routing
            .Where(kv => kv.Value.Provider != name)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var allowed = existing.AllowedProviders.Where(p => p != name).ToList();

        return existing with
        {
            Providers = providers,
            DefaultProvider = def,
            Routing = routing,
            AllowedProviders = allowed,
        };
    }

    /// <summary>
    /// Points <c>defaultProvider</c> at <paramref name="name"/>, ignoring names not in the catalog —
    /// writing an unknown default would produce a config the loader rejects at next startup.
    /// </summary>
    public static ProviderSettings SetDefault(ProviderSettings existing, string name) =>
        existing.Providers.ContainsKey(name) ? existing with { DefaultProvider = name } : existing;

    public static string SuggestName(ProviderSettings existing, ProviderPreset preset)
    {
        if (!existing.Providers.ContainsKey(preset.Id)) return preset.Id;
        for (int i = 2; ; i++)
        {
            var candidate = $"{preset.Id}-{i}";
            if (!existing.Providers.ContainsKey(candidate)) return candidate;
        }
    }

    /// <summary>
    /// One display line per configured instance. The preset a configured instance came from is NOT
    /// persisted (ProviderInstanceConfig records what was configured, not which preset produced it),
    /// so it is inferred for display by matching kind + baseUrl — "openrouter-main — OpenRouter" reads
    /// far better than "openrouter-main — openai-compatible". The inference is presentational only: a
    /// miss simply degrades to showing the raw kind, and nothing downstream depends on it.
    /// </summary>
    public static IReadOnlyList<string> Describe(ProviderSettings settings) =>
        DescribeRows(settings).Select(r => r.Line).ToList();

    /// <summary>
    /// The same rows, each keeping its instance NAME alongside the display line. The editor selects on
    /// these so it never has to parse a name back out of formatted text — an instance whose name
    /// contained the " — " separator would otherwise be recovered wrongly.
    /// </summary>
    public static IReadOnlyList<(string Name, string Line)> DescribeRows(ProviderSettings settings) =>
        settings.Providers
            .Select(kv =>
            {
                var label = PresetLabel(kv.Value);
                var mark = kv.Key == settings.DefaultProvider ? "  (default)" : "";
                return (kv.Key, $"{kv.Key} — {label} — {kv.Value.Model}{mark}");
            })
            .ToList();

    private static string PresetLabel(ProviderInstanceConfig cfg) =>
        Presets.FirstOrDefault(p => p.Kind == cfg.Kind && p.BaseUrl == cfg.BaseUrl)?.DisplayName
        ?? cfg.Kind;

    /// <summary>
    /// The per-instance action menu (Change model / key / endpoint / Make default / Remove with its
    /// role-unbind confirm) — drives the same flow for a click on a single instance row on the
    /// Providers page. Returns the updated settings, or <c>null</c> when nothing changed (dismissed,
    /// or a no-op choice).
    /// </summary>
    internal static async Task<ProviderSettings?> EditInstanceAsync(
        ConsoleWindowSystem ws, Window? parent, ProviderSettings settings, string name, CancellationToken ct)
    {
        if (!settings.Providers.ContainsKey(name)) return null;

        // Key rotation and a moved endpoint are routine. Without these, the only route was
        // Remove + Add — and RemoveInstance (correctly) unbinds every role pointing at the
        // instance, so rotating a key would silently cost the user all their role bindings.
        // Rename is deliberately NOT offered: it needs the same role-REBINDING care as removal
        // (every Target naming the old key must follow it), which is Task 8's territory.
        var action = await FlowDialogs.ChooseAsync(
            ws, parent, name,
            new[] { "Change model…", "Change API key…", "Change endpoint…", "Make default", "Remove" }, ct);
        if (action is null) return null;

        switch (action)
        {
            case "Change model…":
                return await ChangeModelAsync(ws, parent, settings, name, ct);

            case "Change API key…":
            {
                // Masked, and never pre-filled with the current key: echoing a stored secret back
                // into an editable field is a disclosure the user did not ask for.
                var key = await MaskedAskAsync(ws, parent, $"New API key for '{name}':", ct);
                if (string.IsNullOrWhiteSpace(key)) return null;
                return AddOrReplace(
                    settings, name, settings.Providers[name] with { ApiKey = key }, makeDefault: false);
            }

            case "Change endpoint…":
            {
                var cfg = settings.Providers[name];
                var url = await FlowDialogs.AskAsync(
                    ws, parent, "Endpoint", $"Base URL for '{name}':", cfg.BaseUrl, ct);
                if (string.IsNullOrWhiteSpace(url) || url.Trim() == cfg.BaseUrl) return null;
                return AddOrReplace(settings, name, cfg with { BaseUrl = url.Trim() }, makeDefault: false);
            }

            case "Make default":
                return settings.DefaultProvider != name ? SetDefault(settings, name) : null;

            case "Remove":
            {
                var confirm = await FlowDialogs.ChooseAsync(
                    ws, parent, $"Remove '{name}'?",
                    new[] { "Remove" }, ct);
                return confirm is not null ? RemoveInstance(settings, name) : null;
            }

            default:
                return null;
        }
    }

    private static async Task<ProviderSettings?> ChangeModelAsync(
        ConsoleWindowSystem ws, Window? parent, ProviderSettings settings, string name, CancellationToken ct)
    {
        var cfg = settings.Providers[name];
        var model = await PickModelAsync(ws, parent, name, cfg, cfg.Model, ct);
        if (string.IsNullOrWhiteSpace(model) || model.Trim() == cfg.Model) return null;

        var updated = AddOrReplace(settings, name, cfg with { Model = model.Trim() }, makeDefault: false);

        return updated;
    }

    private static async Task<string?> PickModelAsync(
        ConsoleWindowSystem ws, Window? parent, string name, ProviderInstanceConfig cfg,
        string? initial, CancellationToken ct)
    {
        var throwaway = new ProviderSettings(
            new Dictionary<string, ProviderInstanceConfig> { [name] = cfg },
            name, Array.Empty<string>(), new Dictionary<string, RoutingTarget>());

        try
        {
            var provider = ProviderRegistry.Build(throwaway).Default;
            return await ModelPicker.PickAsync(ws, parent, provider, initial, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider that cannot even be constructed (bad url, unknown kind) must not take the
            // editor down — fall back to typing the model id, same as an endpoint that cannot list.
            // The reason is surfaced rather than swallowed: dropping to free-text with no explanation
            // leaves the user unable to tell that enumeration was even attempted. The probe step sets
            // this precedent.
            return await FlowDialogs.AskAsync(
                ws, parent, "Model", $"Could not list models: {ex.Message}\nModel id:", initial, ct);
        }
    }

    private static async Task<string?> MaskedAskAsync(
        ConsoleWindowSystem ws, Window? parent, string message, CancellationToken ct)
    {
        var result = await SharpConsoleUI.Flows.Flow.Run(ws, parent, async ctx =>
            await ctx.Show(new MaskedPromptStep(message), "Credentials",
                SharpConsoleUI.Flows.FlowButtons.None),
            cancellationToken: ct);

        return result.Completed ? result.Value : null;
    }
}
