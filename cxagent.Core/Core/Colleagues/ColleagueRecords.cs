using CxAgent.Core.Models;

namespace CxAgent.Core.Colleagues;

/// <summary>
/// Who a colleague is — <c>colleague.json</c>. Everything here is small and rewritten whole; what grows (the past) and
/// what is distilled (the experience) live in files of their own beside it.
/// </summary>
public sealed record ColleagueRecord
{
    /// <summary>Its name as the user gave it — what the prompt lists and the handle it works under.</summary>
    public required string Name { get; init; }

    /// <summary>The agent type it was kept as, resolved against the catalog as configured when it is brought in.</summary>
    public required string Type { get; init; }

    /// <summary>The provider entry it runs on by default — the one it was kept on — or null for the session's.</summary>
    public string? Instance { get; init; }

    /// <summary>The entry distilling uses, or null for <see cref="Instance"/>.</summary>
    public string? DistillInstance { get; init; }

    public required DateTimeOffset Created { get; init; }

    /// <summary>One line, rewritten by every distilling so the prompt's list stays true as it grows.</summary>
    public string Description { get; init; } = "";

    /// <summary>The folders it has worked in as a session's folder, one per codebase.</summary>
    public IReadOnlyList<Workplace> Workplaces { get; init; } = [];

    /// <summary>
    /// The last round folded into its experience. A WATERMARK, NOT A FLAG PER ROUND: the past is appended and never
    /// edited, so "distilled" cannot be written onto a round — every round after this one is still to be distilled, in
    /// order, which is also how a failed round is folded in with the next.
    /// </summary>
    public long DistilledThrough { get; init; }

    /// <summary>
    /// Bumped by every distilling that changed the experience — how an incarnation that started from an earlier
    /// version learns, at the end of its round, that it has grown elsewhere.
    /// </summary>
    public long ExperienceVersion { get; init; }

    /// <summary>The session folders its latest growth came from, most recent first — named in the "grown" line.</summary>
    public IReadOnlyList<string> GrownIn { get; init; } = [];
}

/// <summary>A folder a colleague worked in as a session's folder.</summary>
/// <param name="Codebase">The codebase key the folder belongs to — see <see cref="Colleagues.Codebase.Key"/>.</param>
/// <param name="Folder">The folder itself, as the session had it.</param>
/// <param name="Sessions">How many sessions it has worked there.</param>
/// <param name="Last">When it last did.</param>
public sealed record Workplace(string Codebase, string Folder, int Sessions, DateTimeOffset Last);

/// <summary>One round of a colleague's past: from the ask it received to the answer it gave, word for word.</summary>
/// <param name="Seq">Its place in the past, from 1 — assigned when it is appended.</param>
/// <param name="Session">The session it was lived in.</param>
/// <param name="Folder">That session's folder.</param>
/// <param name="Started">When the round began.</param>
/// <param name="Ended">When it ended.</param>
/// <param name="Messages">Everything said in it.</param>
public sealed record PastRound(long Seq, string Session, string Folder, DateTimeOffset Started, DateTimeOffset Ended,
    IReadOnlyList<ChatMessage> Messages)
{
    /// <summary>Other workplaces whose files the round read — distilled into their codebases too.</summary>
    public IReadOnlyList<string> AlsoRead { get; init; } = [];
}

/// <summary>What a colleague has distilled: its core, and one text per codebase key.</summary>
public sealed record Experience(string Core, IReadOnlyDictionary<string, string> Codebases);
