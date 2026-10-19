using CxAgent.Core.Llm;

namespace CxAgent.Core.Agents;

/// <summary>
/// One resolved sub-agent type: everything a child needs that a type decides.
///
/// <para>SEPARATE FROM <see cref="AgentTypeConfig"/>, which is what the FILE said. This is what it
/// MEANS after the instance name has been looked up and the window found — so the factory reads a
/// resolved value rather than re-doing config work on every spawn, and nothing downstream has to know
/// that an instance was ever named by a string.</para>
///
/// <para>ENTRIES, NOT MODELS. What <c>agents.&lt;type&gt;.provider</c> lists are <c>providers</c>
/// entries — each an endpoint bound to one model with one window and one thinking level — never a model
/// on its own. That is what makes a choice between them meaningful: two entries can serve the SAME model
/// with different windows or thinking, so <c>small</c> and <c>local</c> are a real choice even when the
/// model behind them is identical, and the window travels with the entry rather than being guessed at.</para>
/// </summary>
/// <summary>
/// WHERE a type runs — the provider it resolved to, that provider's window, and the `providers` entry
/// it came from.
///
/// <para>A RECORD BECAUSE THESE THREE ARE ONE FACT, and they are consumed as one:
/// <c>SubAgentFactory</c> takes Provider and ContextWindow together (`:240`), because a child given
/// one provider and another's window never sees pressure, never compacts, and dies on an overflow.
/// InstanceName is the same resolution seen from the spend side.</para>
///
/// <para>AND BECAUSE THE LIST HAD REACHED SEVEN. AgentType was Name, Briefing, Provider,
/// ContextWindow, MaxTurns, InstanceName, and then a Description — four consecutive nullables at the
/// end, two of them <c>string?</c>. Transpose InstanceName and Description and it compiles cleanly
/// while attributing a session's spend to a sentence. The rule this repo keeps is to notice that at
/// the fourth parameter, not at the seventh; this is the correction.</para>
/// </summary>
/// <param name="Provider">The resolved instance, or null for the parent's.</param>
/// <param name="ContextWindow">
/// That instance's window. TRAVELS WITH THE PROVIDER and is never taken from the session: a child
/// given one provider and another's window sees IsUnderPressure as permanently false, never compacts,
/// and dies on an overflow. Null is legal and means unknown.
/// </param>
/// <param name="InstanceName">
/// Which `providers` entry <see cref="Provider"/> came from, for spend attribution.
///
/// <para>Resolved here because this is where config's instance NAME is still in hand — the
/// driver itself does not carry one, and two entries can serve the same model.</para>
/// </param>
public sealed record TypeRouting(
    ILlmProvider? Provider = null,
    int? ContextWindow = null,

    string? InstanceName = null)
{
    /// <summary>A type that runs wherever its parent does, which is the common case.</summary>
    public static readonly TypeRouting Inherited = new();

    /// <summary>The entry's description, shown beside it where the model chooses. Null when unset.</summary>
    public string? Description { get; init; }

    /// <summary>`name:model`, the form config and the model use — the label spend and a child's row show.
    /// Empty for <see cref="Inherited"/>, which names no entry.</summary>
    public string Label => InstanceName is { Length: > 0 } name && Provider is { } p ? $"{name}:{p.ModelId}" : "";
}

/// <summary>Where a spawn will run, or why it may not: exactly one of the two is set.</summary>
public sealed record ProviderPick(TypeRouting? Routing, string? Refusal);

/// <summary>One named way of doing delegated work: what it is told, when to pick it, and where it
/// runs.</summary>
/// <param name="Name">What the model asked for, and what the row and errors show.</param>
/// <param name="Briefing">Empty for <c>general</c>. Becomes the child's briefing — the
/// highest-authority text in its prompt.</param>
/// <param name="Routing">Where it runs — see <see cref="TypeRouting"/>.</param>
/// <param name="MaxTurns">Null inherits the session ceiling. Zero is unbounded.</param>
/// <param name="Description">
/// WHEN to choose this type, and what comes back — the parent's one line in the spawn tool's catalog.
/// See <see cref="Llm.AgentTypeConfig.Description"/> for why it is written rather than derived from
/// the briefing. Null when the config did not say.
/// </param>
/// <param name="WritesAPlanFile">
/// This type's deliverable is a file whose path the spawner names. See
/// <see cref="AgentTypeDefinition.WritesAPlanFile"/> for why it is declared rather than
/// detected. A type defined in config never sets it: the mechanism hands out a path and then
/// contradicts the child's answer when nothing is there, which is only honest for a briefing
/// that told the child to write one.
/// </param>
public sealed record AgentType(
    string Name,
    string Briefing,
    TypeRouting Routing,
    int? MaxTurns = null,
    string? Description = null,
    bool WritesAPlanFile = false)
{
    /// <summary>
    /// Which tools children of this type are offered, as written in config. Null when unset, which
    /// is today's behaviour: the child inherits its parent's selection unchanged.
    ///
    /// <para>A NAMED MEMBER for the reason this record's own summary gives — the positional list
    /// had already reached seven with four consecutive nullables, and an eighth would be worse.</para>
    /// </summary>
    public Jobs.ToolSelection? Tools { get; init; }

    /// <summary>Every entry this type may run on, in config's order — the first is <see cref="Routing"/>,
    /// the default. Empty when config listed none: the type runs on the session's model.</summary>
    public IReadOnlyList<TypeRouting> Offers { get; init; } = [];

    /// <summary>Why this type cannot run, or null. Spawns of it are refused with this reason.</summary>
    public string? Disabled { get; init; }

    /// <summary>
    /// Where a spawn asking for <paramref name="requested"/> runs. Names match ignoring case — exact case
    /// first, for two entries differing only in case — and NEVER by prefix: a model guessing a prefix
    /// that lands on another, possibly paid, entry is the costly mistake. A `:model` part splits on the
    /// first colon and must be the entry's own model.
    /// </summary>
    public ProviderPick Pick(string? requested)
    {
        if (Disabled is { } why) return new(null, $"{Name} is disabled: {why}.");
        if (string.IsNullOrWhiteSpace(requested)) return new(Routing, null);
        if (Offers.Count == 0) return new(null, $"{Name} offers no choice of provider; omit 'provider'.");

        var asked = requested.Trim();
        var (name, model) = Llm.AgentTypeConfig.SplitEntry(asked,
            n => Offers.Any(o => string.Equals(o.InstanceName, n, StringComparison.OrdinalIgnoreCase)));
        bool ModelFits(TypeRouting o) => model is null || string.Equals(o.Provider?.ModelId, model, StringComparison.OrdinalIgnoreCase);

        var offer = Offers.FirstOrDefault(o => o.InstanceName == name && ModelFits(o))
                 ?? Offers.FirstOrDefault(o => string.Equals(o.InstanceName, name, StringComparison.OrdinalIgnoreCase) && ModelFits(o));
        return offer is not null
            ? new(offer, null)
            : new(null, $"{Name} does not run on '{asked}'. Its providers: {string.Join(", ", Offers.Select(o => o.Label))}.");
    }

    /// <summary>Where this type runs, flattened for readers that want one field. The routing record
    /// is the truth; these exist so call sites do not all have to say <c>Routing.</c>.</summary>
    public ILlmProvider? Provider => Routing.Provider;
    public int? ContextWindow => Routing.ContextWindow;
    public string? InstanceName => Routing.InstanceName;
}

/// <summary>
/// The catalog a spawn resolves a type name against.
///
/// <para>THERE IS ALWAYS A <c>general</c>, whether or not config mentions one. It collapses two spawn
/// paths into one: without it, "no type given" and "type given" diverge at every consumer, and a bare
/// spawn becomes a special case rather than simply being <c>general</c>. It also means the catalog is
/// never empty, so the tool description never has to say "valid types: (none)" and an error can always
/// name something.</para>
///
/// <para>CONFIG MAY OVERRIDE IT. Otherwise there is no way to give the default child a standing
/// instruction without renaming it, and "everything I spawn should know X" is exactly what a
/// project-level briefing is for. What must not happen is <c>general</c> acquiring a briefing by
/// ACCIDENT — hence an empty one by default, and today's child prompt byte-for-byte.</para>
/// </summary>
public sealed class AgentTypeCatalog
{
    /// <summary>The name a spawn resolves to when it names none.</summary>
    public const string DefaultTypeName = "general";

    private readonly Dictionary<string, AgentType> _types = new(StringComparer.Ordinal);

    /// <summary>Every type, in a stable order — <c>general</c> first, then config's, so the tool
    /// description does not reshuffle between runs and churn the prompt-cache prefix.</summary>
    public IReadOnlyList<AgentType> All { get; }

    /// <param name="configured">Types from config, already validated. Empty is the common case.</param>
    /// <param name="providers">
    /// The instance catalog, for resolving each provider a type lists. Null when a session has none (the
    /// mock path): every type then runs on the parent's provider, a listed one included.
    /// </param>
    public AgentTypeCatalog(
        IReadOnlyDictionary<string, AgentTypeConfig> configured,
        ProviderRegistry? providers)
    {
        // GENERAL FIRST, so config's own `general` overwrites it below rather than being rejected —
        // an override is a legitimate choice and should not need a different mechanism than any
        // other type.
        _types[DefaultTypeName] = new AgentType(DefaultTypeName, "", TypeRouting.Inherited);

        // THE SHIPPED TYPES, PRESENT WITHOUT CONFIG. Sourcing them from config.sample.json instead
        // would leave a fresh install with `general` and nothing else while the docs describe five
        // types, and every briefing fix would reach only whoever re-copied the sample. Seeded before
        // config's loop so a configured entry of the same name refines this one (provider, maxTurns)
        // rather than replacing it.
        foreach (var t in BuiltinAgentTypes.All)
            _types[t.Name] = new AgentType(t.Name, t.Briefing, TypeRouting.Inherited,
                t.DefaultMaxTurns, t.Description, t.WritesAPlanFile);

        foreach (var (name, cfg) in configured)
        {
            // EVERY LISTED ENTRY, RESOLVED WITH ITS WINDOW AND DESCRIPTION. A name the registry lacks
            // disables the type rather than rerouting it — config parsing checked file-written names, so
            // a miss here is a registry that disagrees with its settings, and a type silently running
            // somewhere else is the outcome a provider list exists to rule out.
            var offers = new List<TypeRouting>();
            var disabled = cfg.Disabled;
            IReadOnlyList<string> listed = cfg.Providers
                ?? (cfg.Disabled is null && cfg.Provider is { } single ? new[] { single } : Array.Empty<string>());
            // NO CATALOG AT ALL IS THE MOCK PATH: nothing to resolve against, so the list is no reason to
            // refuse the type — it runs on the parent's, as every type does there.
            if (providers is null) listed = Array.Empty<string>();
            foreach (var instance in listed)
            {
                if (providers is null || !providers.TryGet(instance, out var resolved))
                {
                    disabled ??= $"provider '{instance}' is not a configured provider";
                    continue;
                }
                providers.InstanceWindows.TryGetValue(instance, out var window);
                providers.InstanceDescriptions.TryGetValue(instance, out var described);
                offers.Add(new TypeRouting(resolved, window, instance) { Description = described });
            }
            if (disabled is not null) offers.Clear();
            var routing = offers.Count > 0 ? offers[0] : TypeRouting.Inherited;

            // A BUILT-IN NAME KEEPS ITS SHIPPED TEXT and takes only what config may decide: where it
            // runs, and what it may spend. Config parsing already blanked the briefing and warned;
            // reading it here as authoritative would put the drift back.
            var builtin = BuiltinAgentTypes.Find(name);
            _types[name] = builtin is null
                // A NAME THAT IS NOT SHIPPED MUST BRING ITS OWN TEXT, and an empty one is what a
                // config that omitted it produces. The catalog says nothing was configured rather
                // than inventing a line — see AgentTypeConfig.Briefing.
                ? new AgentType(name, cfg.Briefing ?? "",
                    routing, cfg.MaxTurns, cfg.Description)
                    { Tools = cfg.Tools, Offers = offers, Disabled = disabled }
                // A BUILT-IN KEEPS ITS SHIPPED TEXT AND TAKES CONFIG'S TOOLS. Unlike briefing and
                // description, which are shipped text the code depends on, a toolset is a property
                // of this deployment — the user's fact, not ours.
                : new AgentType(name, builtin.Briefing,
                    routing,
                    cfg.MaxTurns ?? builtin.DefaultMaxTurns, builtin.Description,
                    builtin.WritesAPlanFile)
                    { Tools = cfg.Tools, Offers = offers, Disabled = disabled };
        }

        All = [_types[DefaultTypeName],
               .. _types.Where(kv => kv.Key != DefaultTypeName)
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => kv.Value)];
    }

    /// <summary>The names a caller may use, for an error message that says what IS valid rather than
    /// only that something was not.</summary>
    public string Names => string.Join(", ", All.Select(t => t.Name));

    /// <summary>
    /// Resolves a name, or null when nothing matches.
    ///
    /// <para>A null or blank name is <c>general</c> — that is what makes a bare spawn ordinary rather
    /// than special. An UNRECOGNISED name returns null so the caller can refuse it: silently
    /// substituting the default would mean the user's briefing did not apply and nobody was told,
    /// which is the same class of silent-wrong as a mode that quietly stays single.</para>
    /// </summary>
    public AgentType? Resolve(string? name) =>
        string.IsNullOrWhiteSpace(name) ? _types[DefaultTypeName]
        : _types.TryGetValue(name.Trim(), out var type) ? type
        : null;
}
