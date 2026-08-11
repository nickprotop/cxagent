using CxAgent.Core.Llm;

namespace CxAgent.Core.Agent;

/// <summary>
/// One resolved sub-agent type: everything a child needs that a type decides.
///
/// <para>SEPARATE FROM <see cref="AgentTypeConfig"/>, which is what the FILE said. This is what it
/// MEANS after the provider name has been looked up and the window found — so the factory reads a
/// resolved value rather than re-doing config work on every spawn, and nothing downstream has to know
/// that a provider was ever named by a string.</para>
/// </summary>
/// <param name="Name">What the model asked for, and what the row and errors show.</param>
/// <param name="Briefing">Empty for <c>general</c>. Becomes the child's briefing — the highest-authority
/// text in its prompt.</param>
/// <param name="Provider">The resolved instance, or null for the parent's.</param>
/// <param name="ContextWindow">
/// That instance's window. TRAVELS WITH THE PROVIDER and is never taken from the session: a child
/// given one provider and another's window sees IsUnderPressure as permanently false, never compacts,
/// and dies on an overflow. Null is legal and means unknown.
/// </param>
/// <param name="MaxTurns">Null inherits the session ceiling. Zero is unbounded.</param>
public sealed record AgentType(
    string Name,
    string Briefing,
    ILlmProvider? Provider,
    int? ContextWindow,
    int? MaxTurns);

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
    /// The instance catalog, for resolving a type's provider name. Null when a session has none (the
    /// mock path), in which case every type runs on the parent's provider.
    /// </param>
    public AgentTypeCatalog(
        IReadOnlyDictionary<string, AgentTypeConfig> configured,
        ProviderRegistry? providers)
    {
        // GENERAL FIRST, so config's own `general` overwrites it below rather than being rejected —
        // an override is a legitimate choice and should not need a different mechanism than any
        // other type.
        _types[DefaultTypeName] = new AgentType(DefaultTypeName, "", null, null, null);

        foreach (var (name, cfg) in configured)
        {
            ILlmProvider? provider = null;
            int? window = null;

            // THE PROVIDER AND ITS WINDOW, RESOLVED TOGETHER. Config validation already rejected a
            // name that is not configured, so a miss here means the registry and the settings
            // disagree — degrade to the parent's rather than throwing at spawn time.
            if (cfg.Provider is { } instance && providers is not null
                && providers.TryGet(instance, out var resolved))
            {
                provider = resolved;
                providers.InstanceWindows.TryGetValue(instance, out window);
            }

            _types[name] = new AgentType(name, cfg.Briefing, provider, window, cfg.MaxTurns);
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
