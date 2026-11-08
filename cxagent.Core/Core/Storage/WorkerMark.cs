using System.Text.Json.Serialization;

namespace CxAgent.Core.Storage;

/// <summary>
/// A line in a parent's history about one of its workers: where it came from, and — for a worker the agent tool did not
/// spawn — how it was spawned, so a resume can rebuild it.
///
/// <para>GENERIC ON PURPOSE. The worker code saves and restores an origin without reading it; the module that wrote it
/// is the only one that knows what <c>colleague:Charting</c> means.</para>
/// </summary>
/// <param name="AgentId">The worker's agent id — what its own history folder is named.</param>
/// <param name="Origin">Where it came from, or null to clear an earlier mark.</param>
public sealed record WorkerMark(
    [property: JsonPropertyName("id")] string AgentId,
    [property: JsonPropertyName("origin")] string? Origin)
{
    /// <summary>Its handle, when this line records a spawn; null when it only re-marks a worker already spawned.</summary>
    [JsonPropertyName("name")] public string? Name { get; init; }

    /// <summary>The agent type it was spawned as.</summary>
    [JsonPropertyName("type")] public string? Type { get; init; }

    /// <summary>The provider entry it was spawned on, or null for its type's own.</summary>
    [JsonPropertyName("provider")] public string? Provider { get; init; }

    /// <summary>The provider entry it was ordered onto, whatever its type offers — see <c>SpawnOrder.Instance</c>.</summary>
    [JsonPropertyName("instance")] public string? Instance { get; init; }

    /// <summary>The label its row showed.</summary>
    [JsonPropertyName("description")] public string? Description { get; init; }

    /// <summary>The folder it was confined to, relative to its parent's, or null for the parent's.</summary>
    [JsonPropertyName("root")] public string? Root { get; init; }

    /// <summary>
    /// The user deleted it here. A resume rebuilds every worker its parent's history names, and without this line it
    /// rebuilt one the user had deleted.
    /// </summary>
    [JsonPropertyName("deleted")] public bool Deleted { get; init; }

    /// <summary>
    /// The user answered "Not this one" when it was offered as a colleague. Kept for a resume, which would otherwise
    /// offer again a worker the user already turned down. A line that says only this changes nothing else about it.
    /// </summary>
    [JsonPropertyName("declined")] public bool Declined { get; init; }
}
