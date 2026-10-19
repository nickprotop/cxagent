using CxAgent.Core.Agents;
using CxAgent.Core.Config;
using CxAgent.Core.Llm;

namespace CxAgent.UI.Config;

/// <summary>
/// An agent type being edited.
///
/// <para>A BUILT-IN WRITES WHAT AN OVERRIDE MAY SET — provider, max turns, tools — and nothing else:
/// its briefing and description ship with cxagent and are shown, not edited. A type of the user's own
/// writes everything, and must have a briefing.</para>
/// </summary>
public sealed class AgentDraft
{
    private AgentDraft(string name, bool builtin, AgentTypeConfig? original)
    {
        Name = name;
        IsBuiltin = builtin;
        Original = original;
        Description = original?.Description;
        Briefing = original?.Briefing;
        // AS WRITTEN, de-duplicated by the entry each names: a pin (`opus-quick:claude-opus-5-5`) is the
        // user's guard against an entry's model changing under the type, and a reorder must not drop it.
        // AN "all" TYPE HAS NO ROWS — its word is the checkbox. Only then is "all" left out: a DISABLED
        // mixed list (`["local", "all"]`) shows it as a row, so it can be removed.
        AnyProvider = original?.AnyProvider ?? false;
        _originalProviders = (AnyProvider ? [] : original?.Written ?? original?.Providers ?? (original?.Provider is { } p ? [p] : []))
            .Select(w => w.Trim()).DistinctBy(AgentTypeConfig.NameOf, StringComparer.Ordinal).ToList();
        Providers = [.. _originalProviders];
        MaxTurns = original?.MaxTurns;
        var terms = ToolTerms.Read(original?.Tools?.Terms);
        Base = terms.Base;
        Removed = [.. terms.Removed];
        Added = [.. terms.Added];
        _originalTools = original?.Tools?.Terms;
    }

    /// <summary>A shipped type, and its override in config if there is one.</summary>
    public static AgentDraft ForBuiltin(AgentTypeDefinition shipped, AgentTypeConfig? overrideCfg) =>
        new(shipped.Name, builtin: true, overrideCfg) { Shipped = shipped };

    /// <summary>The default child, <c>general</c>, and its override in config if there is one.</summary>
    public static AgentDraft ForGeneral(AgentTypeConfig? overrideCfg) =>
        new(AgentTypeCatalog.DefaultTypeName, builtin: false, overrideCfg) { IsGeneral = true };

    /// <summary>A type of the user's own, as config has it.</summary>
    public static AgentDraft ForOwn(string name, AgentTypeConfig cfg) => new(name, builtin: false, cfg);

    /// <summary>A new type, not in the file yet.</summary>
    public static AgentDraft ForNew(string name) => new(name, builtin: false, null) { IsNew = true };

    public string Name { get; set; }
    public bool IsBuiltin { get; }
    public bool IsNew { get; private init; }

    /// <summary>The default child: provider, turns, tools and an optional standing briefing — no description.</summary>
    public bool IsGeneral { get; private init; }
    public AgentTypeDefinition? Shipped { get; private init; }
    public AgentTypeConfig? Original { get; }

    public string? Description { get; set; }
    public string? Briefing { get; set; }

    /// <summary>The providers it may run on, in order — the first is the default — each as WRITTEN (`name`
    /// or a pinned `name:model`); a new one is added bare. Empty is the session's model. A disabled type
    /// shows its bad entry, so the user can remove it.</summary>
    public List<string> Providers { get; }

    /// <summary>Set to clear a `provider` value that is not a name or a list (it disabled the type and
    /// has no entries to remove): the save then removes the key.</summary>
    public bool ClearBadProvider { get; set; }

    /// <summary>`"provider": "all"`: any provider, chosen at each spawn, no default. When set, the list is
    /// not shown and not written.</summary>
    public bool AnyProvider { get; set; }

    private bool AnyProviderChanged => AnyProvider != (Original?.AnyProvider ?? false);

    /// <summary>Whether <paramref name="name"/> is already listed, in any form.</summary>
    public bool ListsProvider(string name) =>
        Providers.Any(w => w == name || AgentTypeConfig.NameOf(w) == name);

    /// <summary>
    /// Adds a provider to the list by name — or, for an entry that is itself named <c>all</c>, as
    /// <c>all:model</c>. In a type's provider the bare word means every provider: added alone it would
    /// make the type any-provider, and beside another name it would disable the type.
    /// </summary>
    public void AddProvider(string name, string model) =>
        Providers.Add(string.Equals(name, AgentTypeConfig.AnyProviderWord, StringComparison.OrdinalIgnoreCase)
            ? $"{name}:{model}" : name);

    private readonly IReadOnlyList<string> _originalProviders;
    private bool ProvidersChanged => !Providers.SequenceEqual(_originalProviders, StringComparer.Ordinal);
    public int? MaxTurns { get; set; }
    public string Base { get; set; }
    public List<string> Removed { get; }
    public List<string> Added { get; }

    private readonly IReadOnlyList<string>? _originalTools;

    /// <summary>The tools as they would be written, or null for no <c>tools</c> key — the parent's set.</summary>
    private IReadOnlyList<string>? Tools =>
        Base == "inherited" && Removed.Count == 0 && Added.Count == 0
            ? null
            : ToolTerms.Write(new ToolTerms(Base, Removed, Added));

    private bool ToolsChanged =>
        !(Tools ?? []).SequenceEqual(_originalTools ?? [], StringComparer.Ordinal);

    public bool IsDirty =>
        IsNew || AnyProviderChanged || (!AnyProvider && ProvidersChanged) || ClearBadProvider || MaxTurns != Original?.MaxTurns || ToolsChanged
        || (!IsBuiltin && (Blank(Description) != Blank(Original?.Description) || Blank(Briefing) != Blank(Original?.Briefing)));

    /// <summary>The edit: what changed set, what was emptied cleared.</summary>
    public AgentTypeFields ToFields()
    {
        var clear = new HashSet<string>();
        // ANY PROVIDER WINS OVER THE LIST: ticking it empties the rows, which on its own would clear the
        // key — and ApplyTo removes cleared keys AFTER setting them, so "all" would vanish.
        if (!AnyProvider && (ProvidersChanged || ClearBadProvider || AnyProviderChanged) && Providers.Count == 0)
            clear.Add("provider");
        if (MaxTurns is null && Original?.MaxTurns is not null) clear.Add("maxTurns");
        if (ToolsChanged && Tools is null) clear.Add("tools");
        if (!IsBuiltin && Blank(Description) is null && Original?.Description is not null) clear.Add("description");
        if (IsGeneral && Blank(Briefing) is null && Original?.Briefing is not null) clear.Add("briefing");
        return new AgentTypeFields
        {
            Provider = AnyProvider ? (AnyProviderChanged ? AgentTypeConfig.AnyProviderWord : null)
                : ProvidersChanged && Providers.Count == 1 ? Providers[0] : null,
            Providers = !AnyProvider && ProvidersChanged && Providers.Count > 1 ? Providers.ToList() : null,
            MaxTurns = MaxTurns != Original?.MaxTurns ? MaxTurns : null,
            Tools = ToolsChanged ? Tools : null,
            Description = !IsBuiltin && Blank(Description) is { } d && (IsNew || d != Blank(Original?.Description)) ? d : null,
            Briefing = !IsBuiltin && Blank(Briefing) is { } b && (IsNew || b != Blank(Original?.Briefing)) ? b : null,
            Clear = clear,
        };
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
