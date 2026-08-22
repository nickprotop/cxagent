using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Sessions;

/// <summary>
/// One compression, presented as a job row — the single implementation behind every route that
/// compresses a conversation.
///
/// <para>THREE ROUTES HAD DRIFTED. Compression was reachable from the per-turn pressure check inside
/// <see cref="Agents.Agent"/>, from <c>AgentHost</c>'s between-goals check, and from the
/// <c>/compress</c> command in <c>AppBootstrap</c> — and only the first had ever been given the job
/// row. The other two each printed a single line of prose AFTER the work finished, so a user who
/// typed <c>/compress</c> watched nothing happen for several seconds and then saw one sentence. The
/// row, the spinner, the summary in an expandable body and the red-on-truncation treatment all
/// existed; two of the three callers simply could not reach them.</para>
///
/// <para>Threshold policy stays with the CALLERS, deliberately. The per-turn check measures pressure;
/// <c>/compress</c> was asked outright and must not re-derive a threshold whose whole point it
/// bypasses. This type answers only "run one, and show it".</para>
/// </summary>
public static class CompressionRun
{
    /// <summary>
    /// One compression, as one value.
    ///
    /// <para>NINE PARAMETERS WAS THE SIGNAL. RunAsync reached nine when the skill-tool flag arrived,
    /// and the repo's own rule says to stop at the fourth and ask whether the group wants a name.
    /// It does, and there are two of them: WHAT to compress and HOW to report it, which is also why
    /// the list kept growing — every reporting need added a parameter beside the work.</para>
    ///
    /// <para>THE TYPES REPEAT, which is the case the rule exists for: <c>agentId</c> and
    /// <c>title</c> are both <c>string</c> and adjacent. Transposed, this compiles cleanly and files
    /// the row under a title-shaped id.</para>
    /// </summary>
    /// <param name="Context">The agent's context, mutated in place.</param>
    /// <param name="Provider">The provider used to write the summary.</param>
    /// <param name="SkillToolOffered">
    /// Whether <c>skill</c> is available. False drops the "call skill again" remedy from the
    /// compaction notice — naming a withheld tool sends the model to spend a turn discovering it.
    /// </param>
    public readonly record struct CompressionWork(
        AgentContext Context, ILlmProvider Provider, bool SkillToolOffered = true);

    /// <summary>
    /// Where one compression is reported.
    ///
    /// <para>Separate from <see cref="CompressionWork"/> because these are the parameters that grew:
    /// a row, an id, a title, a meter, a callback — five ways of saying the same thing to different
    /// audiences, none of which the compressor itself needs.</para>
    /// </summary>
    /// <param name="Jobs">Where the row is rendered.</param>
    /// <param name="AgentId">The goal the row belongs to.</param>
    /// <param name="Title">
    /// The row's display name. The caller writes it because only the caller knows WHY this ran — the
    /// pressure reading that tripped a threshold, or that the user asked.
    /// </param>
    /// <param name="Meter">Records the summarising call's own usage. Unmetered provider calls are
    /// this project's recurring rot pattern, and housekeeping is not exempt.</param>
    /// <param name="Compressed">
    /// Invoked with (messages before, messages after) once a compression really happened, so the
    /// context readout can stop presenting its last measurement as current AND say what changed.
    /// Optional: callers with no UI to notify pass null.
    /// </param>
    public readonly record struct CompressionReport(
        IToolObserver Jobs, string AgentId, string Title,
        Action<LlmUsage>? Meter = null, Action<int, int>? Compressed = null);

    /// <summary>Compresses one context, rendering the work as a job row.</summary>
    /// <remarks>
    /// <para>NEVER THROWS for the same reason the per-turn caller never did: compression failing must
    /// not end a session that is otherwise working. <see cref="SessionCompressor"/> falls back to
    /// truncation on a provider error and reports which happened, so the row can be honest about it.</para>
    /// </remarks>
    public static async Task<SessionCompressor.CompressResult> RunAsync(
        CompressionWork work, CompressionReport report, CancellationToken ct)
    {
        // A JOB ROW, not a bare system line, because compression IS work: it takes a work.Provider call of
        // its own, runs for seconds, and rewrites the conversation. As a row it gets the machinery
        // every other tool call already has — a spinner while it runs, without which seconds of
        // silence read as a hang — a one-line summary when it lands, red if it degrades to
        // truncation, and an expandable body.
        //
        // THE EXPANDABLE BODY IS THE POINT. Compression is the one operation that silently discards
        // what the model knew. With the summary shown nowhere the user sees that memory shrank but
        // never what survived; putting it in the body makes a lossy step auditable.
        var job = new Job
        {
            Id = Helpers.UlidGenerator.NewId(),
            PlanLocalId = "compress",
            AgentId = report.AgentId,
            PluginType = "compress",
            DisplayName = report.Title,
            State = JobState.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow,
        };
        report.Jobs.ToolsChanged(new[] { job });

        var started = DateTimeOffset.UtcNow;
        var before = work.Context.Count;

        // CHARACTERS, NOT MESSAGES, for what the user is told. Compression replaces the older half
        // with ONE summary, so the COUNT barely moves and can be identical: a two-message
        // conversation removes one and inserts one, and the status bar read "report.Compressed 2→2 msgs" —
        // a true statement that tells the user nothing and looks like a no-op. What actually shrank
        // is the text, often by an order of magnitude, and that is the number worth showing.
        var charsBefore = work.Context.TotalChars();

        var result = await SessionCompressor.CompressAsync(work.Context, work.Provider, ct, report.Meter, work.SkillToolOffered);

        // The summary the compressor wrote, which is now the first message. Read back rather than
        // returned, so the compressor's contract stays "shrink this work.Context" rather than growing a
        // reporting channel for one caller.
        var summary = work.Context.Count > 0 ? work.Context.Messages[0].Content : "";

        // TRUNCATION IS A FAILURE, and shows red. It means the summarising call did not answer and
        // the oldest half was dropped unread — recoverable, but the user should see that it happened
        // rather than find the gap later.
        // A DECLINE IS NOT A FAILURE. Nothing broke and nothing was lost — the model had nothing
        // useful to say and the conversation was left alone — so it reports as a completed step that
        // did no work, not as red. A truncating fallback IS a failure: it cost history.
        job.State = result.Summarised || result.Declined ? JobState.Succeeded : JobState.Failed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.Result = new JobResult
        {
            Success = result.Summarised,
            ExitCode = result.Summarised ? 0 : -1,
            Duration = DateTimeOffset.UtcNow - started,
            // THE TWO FAILURES ARE NOT THE SAME. A THROWN summarisation falls back to truncation and
            // costs history; a DECLINED one returned no usable summary and cost nothing, leaving the
            // conversation exactly as it was. Reporting both as "dropped the oldest messages unread"
            // told the user they had lost something they still had.
            ErrorMessage = result.Summarised
                ? null
                : result.Declined
                    ? "the model returned no usable summary — nothing was report.Compressed, nothing was lost"
                    : "summarisation failed — dropped the oldest messages unread",
            Output = new Dictionary<string, object?>
            {
                ["content"] = $"{before} messages → {work.Context.Count}\n\n{summary}",
            },
        };
        report.Jobs.ToolUpdated(job);

        // KEYED ON Summarised, NOT ON THE MESSAGE COUNT — an easy trap for anything reporting on a
        // compression. Compression replaces the older half with ONE summary, so a two-message
        // conversation removes one and inserts one and ends at the count it started with, while its
        // content shrank from a full turn to a couple of hundred characters. Counting therefore
        // reports "nothing happened" for a real compression, and the work.Context reading stays marked
        // fresh when it no longer describes the conversation at all.
        //
        // Summarised is the compressor's own answer to "did I do the work", which is the question.
        if (result.Summarised) report.Compressed?.Invoke(charsBefore, work.Context.TotalChars());

        return result;
    }

}
