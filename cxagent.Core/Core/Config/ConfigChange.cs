namespace CxAgent.Core.Config;

/// <summary>
/// What a saved edit changed, for deciding what each running session does about it: nothing, a refresh
/// in place, or a move onto the new instance.
/// </summary>
/// <param name="Instances">Instances whose provider object changes: edited, renamed (old name) or removed.
/// A tab on one is swapped in place, or moved as the user chooses when it is in <see cref="ModelChanged"/>.</param>
/// <param name="Renamed">Old name → new name.</param>
/// <param name="Everyone">
/// Every session refreshes in place, with no new host: the reviewer changed (set on each session's
/// policy), the default instance or its <c>maxConcurrentAgents</c> changed (each session's sub-agent
/// limit), or agent types or an instance one names changed (where each session's children run).
/// </param>
/// <param name="Note">One line for each tab the change reaches: what changed.</param>
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

    /// <summary>The name a session on <paramref name="instanceName"/> runs under after this change.</summary>
    public string? Follow(string? instanceName) =>
        instanceName is not null && Renamed.TryGetValue(instanceName, out var to) ? to : instanceName;
}
