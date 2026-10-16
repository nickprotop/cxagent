namespace CxAgent.Core.Config;

/// <summary>
/// What a saved edit changed, for deciding which running sessions must be re-wired.
/// </summary>
/// <param name="Instances">Instances whose provider object changes: edited, renamed (old name) or removed.</param>
/// <param name="Renamed">Old name → new name.</param>
/// <param name="Everyone">
/// Every session re-wires: the reviewer changed (each session builds its classifier at wire time), the
/// default instance or its <c>maxConcurrentAgents</c> changed (it sizes each session's sub-agent
/// semaphore), or an instance an agent type names changed (types capture their provider at wire time).
/// </param>
/// <param name="Note">One line for each re-wired tab: what changed.</param>
public sealed record ConfigChange(
    IReadOnlySet<string> Instances, IReadOnlyDictionary<string, string> Renamed, bool Everyone, string Note)
{
    /// <summary>
    /// Instances whose MODEL OR ENDPOINT changed — the edits a tab's prompt cache does not survive. Said by
    /// whoever made the edit, because only the edit knows: a rename or a rotated key builds a provider
    /// object that differs in every field a comparison could check, yet sends the same request to the
    /// same server. A tab on one of these is moved as the user chooses; on any other edited instance it
    /// is swapped in place.
    /// </summary>
    public IReadOnlySet<string> ModelChanged { get; init; } = new HashSet<string>();

    /// <summary>Whether a session on <paramref name="instanceName"/> must be re-wired.</summary>
    public bool Affects(string? instanceName) =>
        Everyone || (instanceName is not null && Instances.Contains(instanceName));

    /// <summary>The name a session on <paramref name="instanceName"/> runs under after this change.</summary>
    public string? Follow(string? instanceName) =>
        instanceName is not null && Renamed.TryGetValue(instanceName, out var to) ? to : instanceName;
}
