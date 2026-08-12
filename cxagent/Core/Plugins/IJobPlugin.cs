using CxAgent.Core.Models;

namespace CxAgent.Core.Plugins;

/// <summary>Severity for a structured log line. Local enum keeps the core dependency-free.</summary>
public enum JobLogLevel { Trace, Debug, Info, Warning, Error }

/// <summary>A native job plugin: advertises a type + schema, validates params, and executes.</summary>
public interface IJobPlugin
{
    string TypeName { get; }              // "shell", "file", etc.
    string DisplayName { get; }
    JobSchema GetSchema();
    JobValidation Validate(JobParameters parameters);
    Task<JobResult> ExecuteAsync(JobParameters parameters, IJobContext context, CancellationToken ct);
}

/// <summary>Runtime callbacks a plugin uses during execution.</summary>
public interface IJobContext
{
    void ReportProgress(double percent, string? message = null);

    /// <summary>
    /// The work is ACTUALLY STARTING NOW — everything before this was preamble.
    ///
    /// <para>WHY THIS EXISTS. A row's duration was measured from the moment the row appeared, which
    /// is before the permission gate has asked the user anything. Seen live: a shell command whose
    /// own timeout fired at 15s reported <c>failed · 270.8s</c>, because the user spent four minutes
    /// deciding whether to approve it. The number described how long the HUMAN took and was presented
    /// as how long the COMMAND took — and it is worse than merely wrong, because "270s" invites
    /// someone to go looking for a slow command that does not exist.</para>
    ///
    /// <para>Best-effort, like the other reporting methods: a plugin that never calls it keeps the
    /// old behaviour, and a caller that ignores it loses nothing.</para>
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
    /// WHO THIS WORK IS FOR, in words a user can act on — null for the session's own agent.
    ///
    /// <para>A LABEL, NOT AN ID. The permission prompt is the only place this surfaces, and a ULID
    /// there ("01KZQN97XT8DA…") tells the user nothing they can weigh: what they need is "the
    /// sub-agent you asked to analyse the test failure". So the spawn branch sets the child's
    /// DESCRIPTION here, which is the phrase the parent's model already wrote to name the task.</para>
    ///
    /// <para>It lives on the context rather than on the gate because the gate is one per SESSION —
    /// built once in AppBootstrap, shared by parent and child alike — while the context is built per
    /// tool call, which is exactly the granularity "who is asking" needs.</para>
    /// </summary>
    string? Requester { get; }
    void Log(string line);
    void Log(JobLogLevel level, string line);
    /// <summary>
    /// Reports a CPU/memory sample for the job's underlying work (e.g. a monitored child
    /// process). Not every plugin has a process to sample — implementations must treat this
    /// as best-effort telemetry, never load-bearing for job completion.
    /// </summary>
    void ReportResources(ResourceSnapshot snapshot);

    /// <summary>
    /// Reports that this job's worker invoked a TOOL, so the UI can show what a long-running
    /// llm_agent is actually doing instead of 40 seconds of silence.
    ///
    /// <para>Best-effort telemetry like <see cref="ReportResources"/>: never load-bearing for job
    /// completion, and a plugin with no tool loop simply never calls it. <paramref name="summary"/>
    /// is a short human phrase ("read Calc.cs"), not the tool's result — the result can be
    /// thousands of characters and already reaches the transcript through the job's own output.</para>
    /// </summary>
    void ReportToolCall(string toolName, string summary);

    /// <summary>
    /// Reports a chunk of a worker's generated TEXT as it arrives, so the UI can show prose
    /// building rather than a spinner followed by a wall of text.
    ///
    /// <para>Best-effort telemetry, same contract as <see cref="ReportResources"/>: a plugin that
    /// does not stream simply never calls it, and a dropped chunk costs a moment's smoothness,
    /// never correctness. The job's real output is still assembled and returned in
    /// <see cref="JobResult.Output"/> — this is a VIEW of that work in progress, not the record
    /// of it.</para>
    /// </summary>
    void ReportTextDelta(string delta);
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
