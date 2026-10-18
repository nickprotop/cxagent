namespace CxAgent.Core.Jobs;

/// <summary>Severity for a structured log line. Local enum keeps the core dependency-free.</summary>
public enum JobLogLevel { Trace, Debug, Info, Warning, Error }

/// <summary>
/// What a tool call is given to work with: where it runs, who asked, and how to say what it is doing.
///
/// <para>WHAT A TOOL DOES WITH A CALL, AND NOTHING OF THE HOST'S. The host's gate and job engine
/// report through a wider interface of their own, in Core; a plugin, an embedder's tool, or a test
/// standing in for either sees only this. Adding a member here later breaks nobody — the host is
/// what implements it — so it holds what tools use now, and grows when one needs more.</para>
/// </summary>
public interface IJobContext
{
    /// <summary>How far along the work is, and optionally what it is doing — best-effort, never
    /// load-bearing for completion.</summary>
    void ReportProgress(double percent, string? message = null);

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

    /// <summary>
    /// The directory this job's paths are relative TO — null when the caller has no opinion, which
    /// means the process's own.
    ///
    /// <para>THE SYSTEM PROMPT ALREADY PROMISES THIS: "Relative paths resolve from the working
    /// directory." Until this existed that was true only by COINCIDENCE — an executor handed
    /// <c>src/foo.cs</c> passed it to the framework, which resolved it against the PROCESS
    /// directory, and the two matched because nothing ever moved the process.</para>
    ///
    /// <para>WHAT IT COSTS TO BE WRONG, once a second session exists on another folder: the model
    /// asks to edit <c>src/foo.cs</c>, the permission gate checks that path against THIS session's
    /// root and allows it, and the framework then resolves it against the other session's. The write
    /// lands in a checkout the user never approved, and every layer behaved correctly on the way.
    /// That is why this rides on the context rather than being read at the point of use.</para>
    ///
    /// <para>On the CONTEXT, not the executor, for the same reason <see cref="Requester"/> is: a
    /// executor instance is shared by every agent in the session, while a context is built per tool
    /// call — which is exactly the granularity "whose folder is this" needs, since a sub-agent may
    /// eventually work somewhere else.</para>
    /// </summary>
    string? WorkingDirectory { get; }

    void Log(string line);
    void Log(JobLogLevel level, string line);

    /// <summary>
    /// Reports a chunk of a worker's generated TEXT as it arrives, so the UI can show prose
    /// building rather than a spinner followed by a wall of text.
    ///
    /// <para>Best-effort telemetry, like <see cref="ReportProgress"/>: an executor that
    /// does not stream simply never calls it, and a dropped chunk costs a moment's smoothness,
    /// never correctness. The job's real output is still assembled and returned in
    /// <see cref="JobResult.Output"/> — this is a VIEW of that work in progress, not the record
    /// of it.</para>
    /// </summary>
    void ReportTextDelta(string delta);
}
