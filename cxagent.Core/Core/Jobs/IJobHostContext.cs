using CxAgent.Core.Models;

namespace CxAgent.Core.Jobs;

/// <summary>
/// The context the host's own job engine and permission gate report through — everything
/// <see cref="IJobContext"/> gives a tool, and what only the host knows or does: when the work
/// truly starts, a call parked on a prompt or under review, who decided it, what its process uses,
/// the tool calls inside it, and the results it depends on.
///
/// <para>NOT THE PLUGIN CONTRACT. These were in it until contract 4, and a plugin could set
/// <see cref="DecidedBy"/> on its own call — badging it "auto-approved". A plugin, an embedder's
/// tool and a test fake are handed <see cref="IJobContext"/>; the host reaches these by asking
/// whether the context it holds is this one, since every report here is best-effort and a context
/// without them simply is not told.</para>
/// </summary>
public interface IJobHostContext : IJobContext
{
    /// <summary>
    /// The work is ACTUALLY STARTING NOW — everything before this was preamble.
    ///
    /// <para>WHY THIS EXISTS. Timing a row from the moment it APPEARS starts the clock before the
    /// permission gate has asked the user anything. Seen live: a shell command whose own timeout fired
    /// at 15s reported <c>failed · 270.8s</c>, because the user spent four minutes deciding whether to
    /// approve it. That number describes how long the HUMAN took while being presented as how long the
    /// COMMAND took — worse than merely wrong, because "270s" invites someone to go looking for a slow
    /// command that does not exist.</para>
    ///
    /// <para>Best-effort, like the other reporting methods: an executor that never calls it just leaves
    /// the row timed from its appearance, and a caller that ignores it loses nothing.</para>
    /// </summary>
    void WorkStarting();

    /// <summary>
    /// Reports that this job has stopped at a permission prompt (true), or resumed (false).
    ///
    /// <para>The agent running the job subscribes and marks itself, so a parent's row can say which
    /// child is parked on approval rather than leaving it looking like one that is merely slow. Only
    /// distinguishable — and only worth reporting — once several children run at once.</para>
    /// </summary>
    void ReportPermissionWait(bool waiting);

    /// <summary>
    /// Reports that auto mode's classifier has started being consulted for this call's permission
    /// gate (true), or has stopped (false) — the verdict landed, or the gate fell through to a user
    /// prompt.
    ///
    /// <para>SEPARATE FROM <see cref="ReportPermissionWait"/>. That one covers a HUMAN prompt sitting
    /// unanswered; this covers the model round trip that happens BEFORE a prompt is ever shown, and
    /// which a stored rule or an in-boundary pass never reaches at all — this fires only on the path
    /// that actually calls the classifier, so an ordinary silent allow never sees it.</para>
    /// </summary>
    void ReportReviewing(bool reviewing);

    /// <summary>
    /// The classifier's verdict on THIS call, once the gate has run — "auto" when a rule the
    /// classifier itself wrote allowed or denied it, null on every ordinary path (a stored rule
    /// from a user's own "always", a silent in-boundary pass, a prompt the user answered).
    ///
    /// <para>THIS OUTLIVED THE PROBLEM IT WAS ADDED FOR, and is kept for a different one. It began
    /// as a side channel: the dispatch paths reduced a gate's JobResult to a bare string before
    /// Agent saw it, so a stamped decider never reached the row (reported live — a classifier
    /// auto-approved <c>du -sh . 2>&amp;1 | tail -1</c>, the DB and <c>/stats</c> recorded it, the row
    /// rendered plain "done"). <see cref="ToolOutcome"/> ended that severing; what this now carries
    /// is the TIMING, which the result never could.</para>
    ///
    /// <para>ON THE CONTEXT rather than a new dictionary or a tuple, for the reason
    /// <see cref="IJobContext.Requester"/> already gives: JobContext is built per tool call, which is the
    /// granularity a decider needs — one gate outcome per call, read once, by the one caller
    /// (Agent.InvokeAndShowAsync) that closes the row right after the dispatch chain returns.</para>
    ///
    /// <para>WRITTEN AT DECISION TIME, BY THE GATE WRAPPER, and that timing is the point rather
    /// than an implementation detail. The classifier rules at the gate, BEFORE the tool runs; the
    /// row that shows the badge is already on screen by then. Setting this is therefore a REPORT,
    /// not a stash — <c>JobContext</c> raises an event from the setter and the agent stamps the
    /// live Job — which is what lets a badge appear while a minutes-long command is still running,
    /// and lets an auto-DENIED call be badged at all, since it never executes to produce a result.
    /// See <see cref="Models.Job.DecidedBy"/>.</para>
    ///
    /// <para>NOT THREAD-SAFE, and does not need to be: tool calls within one turn run sequentially
    /// through the dispatch chain, so nothing else can write it between the gate that reports it
    /// and the agent that stamps the row.</para>
    /// </summary>
    string? DecidedBy { get; set; }

    /// <summary>
    /// Reports a CPU/memory sample for the job's underlying work (e.g. a monitored child
    /// process). Not every executor has a process to sample — implementations must treat this
    /// as best-effort telemetry, never load-bearing for job completion.
    /// </summary>
    void ReportResources(ResourceSnapshot snapshot);

    /// <summary>
    /// Reports that this job's worker invoked a TOOL, so the UI can show what a long-running
    /// llm_agent is actually doing instead of 40 seconds of silence.
    ///
    /// <para>Best-effort telemetry like <see cref="ReportResources"/>: never load-bearing for job
    /// completion, and an executor with no tool loop simply never calls it. <paramref name="summary"/>
    /// is a short human phrase ("read Calc.cs"), not the tool's result — the result can be
    /// thousands of characters and already reaches the transcript through the job's own output.</para>
    /// </summary>
    void ReportToolCall(string toolName, string summary);

    /// <summary>The results of this job's completed dependencies, keyed by dependency Job.Id (ULID).</summary>
    IReadOnlyDictionary<string, JobResult> CompletedJobOutputs { get; }

    /// <summary>
    /// Human-readable names for the same dependencies, keyed by the SAME Job.Id as
    /// <see cref="CompletedJobOutputs"/>. A parallel map rather than a name-keyed output dictionary:
    /// display names are not unique and are not identity, so keying results by them would break the
    /// id lookup that <see cref="CompletedJobOutputs"/> promises.
    ///
    /// <para>Exists because a ULID is meaningless to a worker model. The plan the orchestrator
    /// authored refers to jobs by name, so labelling injected dependency output with
    /// <c>01JQZX9K2M4P7R8V…</c> gives the model nothing to bind a prompt like "the first reviewer
    /// disagreed with the second" to — and spends ~80 tokens per dependency saying nothing.</para>
    /// </summary>
    IReadOnlyDictionary<string, string> CompletedJobNames { get; }
}
