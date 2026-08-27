namespace CxAgent.Core.Llm;

/// <summary>
/// The configured plugins, by the name they are keyed under in <c>config.json</c>.
///
/// <para>ITS OWN TYPE, NOT A MEMBER OF <see cref="ProviderCatalog"/>, because the catalog is fixed
/// for the process and these are not. Plugins arrive and leave mid-session by design — that is what
/// <c>/plugin load</c> and <c>/plugin unwire</c> already do — so their entries move with them, while
/// a provider swapped live would mean rebuilding an HttpClient, re-resolving a model and re-wiring
/// the runner. A mutable member inside a record described as fixed is how the next reader concludes
/// they may rebind the rest of it.</para>
///
/// <para>REPLACED, NEVER EDITED. <see cref="With"/> and <see cref="Without"/> return a new instance,
/// so a session holding the old one keeps reading exactly what it read before. That matters most for
/// a session mid-turn: its model is reading a tool list built from this, and editing in place would
/// move it under the model's feet.</para>
/// </summary>
public sealed class PluginEntries
{
    /// <summary>Empty, for a config with no plugins block — which is most of them.</summary>
    public static readonly PluginEntries None = new(new Dictionary<string, PluginConfig>());

    /// <summary>COPIED, NOT ADOPTED. An embedder hands `AgentConfig.Plugins` — a live
    /// `Dictionary` they still hold a reference to — so keeping it by reference would let them edit
    /// this set from outside and make "replaced, never edited" false for the one caller most likely
    /// to rely on it.</summary>
    public PluginEntries(IReadOnlyDictionary<string, PluginConfig> entries) =>
        All = new Dictionary<string, PluginConfig>(entries, StringComparer.Ordinal);

    public IReadOnlyDictionary<string, PluginConfig> All { get; }

    /// <summary>This set with one entry added or replaced, keyed by NAME — two names may point at
    /// one binary, as <c>config.sample.json</c> ships, and only the name separates them.</summary>
    public PluginEntries With(string name, PluginConfig entry)
    {
        var next = new Dictionary<string, PluginConfig>(All, StringComparer.Ordinal) { [name] = entry };
        return new PluginEntries(next);
    }

    /// <summary>This set without one name. Removing a name that is not present returns an equivalent
    /// set rather than failing — the caller's goal is already true.</summary>
    public PluginEntries Without(string name)
    {
        if (!All.ContainsKey(name)) return this;

        var next = new Dictionary<string, PluginConfig>(All, StringComparer.Ordinal);
        next.Remove(name);
        return new PluginEntries(next);
    }
}
