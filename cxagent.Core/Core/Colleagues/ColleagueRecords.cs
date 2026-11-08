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

    /// <summary>The first waiting round, while the distiller keeps refusing it; 0 when nothing is failing.</summary>
    public long FailingRound { get; init; }

    /// <summary>How many drains in a row refused <see cref="FailingRound"/>.</summary>
    public int Failures { get; init; }

    /// <summary>Rounds stepped past without being learned from — still in the past, word for word.</summary>
    public IReadOnlyList<SkippedRound> Skipped { get; init; } = [];

    /// <summary>
    /// What distilling has cost it, every process's calls together — kept HERE, beside the experience it paid for, so the
    /// library's total is the colleague's own and not whichever usage history one machine happens to hold.
    /// </summary>
    public DistillTotals Spend { get; init; } = new();

    /// <summary>
    /// Frozen by the user: it works and its rounds are kept, but none is distilled — the user's lever for keeping a good
    /// experience as it is (2026-11-08).
    /// </summary>
    public bool Frozen { get; init; }

    /// <summary>How many rounds its past held when it was frozen — the stretch lived frozen starts after this one.</summary>
    public long FrozenAfter { get; init; }

    /// <summary>
    /// Stretches lived frozen, which are never distilled. KEPT AS RANGES BESIDE THE WATERMARK, NOT BY MOVING IT: rounds
    /// waiting from before a freeze are still to be learned from, and they come before the stretch.
    /// </summary>
    public IReadOnlyList<SeqRange> Unlearned { get; init; } = [];
}

/// <summary>Rounds <paramref name="From"/> to <paramref name="Through"/> of a past, both included.</summary>
public sealed record SeqRange(long From, long Through)
{
    public bool Holds(long seq) => seq >= From && seq <= Through;

    public long Count => Through - From + 1;
}

/// <summary>Calls to the distiller and the tokens they took.</summary>
public sealed record DistillTotals
{
    public long Calls { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }

    /// <summary>These totals with one more call.</summary>
    public DistillTotals Add(DistillSpend call) => this with
    {
        Calls = Calls + 1,
        InputTokens = InputTokens + call.InputTokens,
        OutputTokens = OutputTokens + call.OutputTokens,
    };
}

/// <summary>One call to the distiller, as it was paid for.</summary>
/// <param name="Colleague">Whose rounds it distilled.</param>
/// <param name="ModelId">The model that answered, as the provider names it.</param>
/// <param name="InputTokens">What it read.</param>
/// <param name="OutputTokens">What it wrote.</param>
/// <param name="StartedAt">When the call began.</param>
/// <param name="DurationMs">How long it took.</param>
public sealed record DistillSpend(string Colleague, string? ModelId, int InputTokens, int OutputTokens, DateTimeOffset StartedAt, long DurationMs);

/// <summary>A round the distiller kept refusing and was stepped past.</summary>
/// <param name="Seq">Its place in the past.</param>
/// <param name="Why">The last refusal.</param>
/// <param name="At">When it was stepped past.</param>
public sealed record SkippedRound(long Seq, string Why, DateTimeOffset At);

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

    /// <summary>
    /// Folders the round read outside the session's codebase and every workplace — named to the distiller, so what was
    /// learned there is not filed under the session's codebase. Empty for rounds kept before it was recorded.
    /// </summary>
    public IReadOnlyList<string> ReadElsewhere { get; init; } = [];
}

/// <summary>What a colleague has distilled: its core, and one text per codebase key.</summary>
public sealed record Experience(string Core, IReadOnlyDictionary<string, string> Codebases);
