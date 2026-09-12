using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Execution;

/// <summary>
/// Concrete IJobContext for one job's execution. Routes Log lines to the LogFileManager
/// (P2) and exposes the dependency results the executor collected. Progress/log events
/// for the UI (P5) are a later concern — Log currently persists best-effort.
/// </summary>
public sealed class JobContext : IJobContext
{
    private readonly string _agentId;
    private readonly string _jobId;
    private readonly LogFileManager? _logs;

    public IReadOnlyDictionary<string, JobResult> CompletedJobOutputs { get; }
    public IReadOnlyDictionary<string, string> CompletedJobNames { get; }

    /// <param name="completedNames">
    /// Display names for the dependencies, keyed by the same Job.Id. Optional so existing callers
    /// (and tests) that only care about results keep compiling; an absent map degrades the label to
    /// the raw id rather than dropping it.
    /// </param>
    /// <param name="agentId">Which agent this job belongs to — the grouping key for its logs.</param>
    /// <param name="jobId">This job's own id.</param>
    /// <param name="completedOutputs">Results of the jobs this one depends on, keyed by Job.Id.</param>
    /// <param name="logs">Where the job's output is written, or null to keep none.</param>
    public JobContext(string agentId, string jobId,
        IReadOnlyDictionary<string, JobResult> completedOutputs, LogFileManager? logs,
        IReadOnlyDictionary<string, string>? completedNames = null)
    {
        _agentId = agentId;
        _jobId = jobId;
        CompletedJobOutputs = completedOutputs;
        CompletedJobNames = completedNames ?? new Dictionary<string, string>();
        _logs = logs;
    }

    /// <summary>
    /// Which agent made this call — the id <see cref="Delivery"/> is addressed by.
    ///
    /// <para>NOT <see cref="Requester"/>, WHICH IS A HUMAN LABEL. <c>IJobContext</c> is emphatic that
    /// Requester is a description to show ("find the loader"), not something to look an agent up by;
    /// an executor that passed it to <c>Tell</c> would address nothing and be told so as
    /// <c>Unknown</c>.</para>
    ///
    /// <para>ON THE CONCRETE TYPE, NOT THE INTERFACE, for the reason <see cref="Delivery"/> is:
    /// <c>IJobContext</c> ships in CxAgent.Plugins.Abstractions, whose contract is published, and an
    /// executor that needs to reach back already casts to reach the port.</para>
    /// </summary>
    public string AgentId => _agentId;

    public void ReportProgress(double percent, string? message = null)
    {
        // TODO(P5): raise a progress event the UI subscribes to. Headless P3 is a no-op.
    }

    /// <summary>
    /// Raised when an executor's real work begins, after any permission prompt has been answered. The
    /// caller uses it to start the duration clock, so a row reports how long the WORK took rather
    /// than how long the user took to approve it.
    /// </summary>
    public event Action? WorkStarted;

    public void WorkStarting() => WorkStarted?.Invoke();

    /// <summary>Set by the spawn branch to the child's description; null for the parent's own work.
    /// Settable rather than a constructor parameter so the ~20 construction sites are untouched.</summary>
    public string? Requester { get; set; }

    /// <summary>The agent's own directory, so a relative path resolves where the model was told it
    /// would. Settable for the same reason <see cref="Requester"/> is.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Where to tell the agent that made this call something it should read later.
    ///
    /// <para>BESIDE <see cref="Requester"/> AND <see cref="WorkingDirectory"/> BECAUSE IT IS THE SAME
    /// CATEGORY: a fact about who asked, travelling with the call so an executor does not have to be
    /// told separately. An executor whose work outlives the call — a process it backgrounded, a watch
    /// it armed — has an agent id in this context and nothing else to address.</para>
    ///
    /// <para>NULL WHEN NOBODY CAN BE TOLD: a headless run, a test, an embedder that wired no session.
    /// Delivery is best-effort and an executor must treat it as such rather than assume a port.</para>
    /// </summary>
    public Agents.IAgentDelivery? Delivery { get; init; }

    private string? _decidedBy;

    /// <summary>
    /// The classifier's verdict on this call — set by the gate wrapper the moment it has one, and
    /// re-stamped by whichever dispatch path (ToolBindings, AgentToolset) unwraps the gate's
    /// JobResult down to a string. See the interface doc for why it rides the context at all.
    ///
    /// <para>THE SETTER RAISES <see cref="DeciderReported"/>, which is what makes the badge a
    /// live report rather than a value collected at the end. The agent subscribes and stamps the
    /// running Job, so the word appears while the tool is still working — the gate decides before
    /// the tool runs, and an auto-denied tool never runs at all.</para>
    ///
    /// <para>SILENT ON A NULL, deliberately. Both toolsets reset this to null up front so a prior
    /// call's verdict cannot be read as this one's, and the dispatch paths re-stamp it after the
    /// await with the same value the gate already reported. Raising on null would fire a "no
    /// decider" report on every ungated call and, worse, would let the reset clear a badge the
    /// gate had just earned.</para>
    /// </summary>
    public string? DecidedBy
    {
        get => _decidedBy;
        set
        {
            _decidedBy = value;
            if (value is not null) DeciderReported?.Invoke(value);
        }
    }

    /// <summary>Raised when a gate names the decider for this call, at the moment it does. Same
    /// contract as <see cref="ResourceReported"/>: the subscriber marshals to its own thread.</summary>
    public event Action<string>? DeciderReported;

    /// <summary>
    /// Raised true when this job stops at a permission prompt, false when it stops waiting.
    ///
    /// <para>THE AGENT SUBSCRIBES, and marks itself — which is what lets a parent's row say which
    /// child is parked on approval. Routing it from the gate instead would not work: the gate is a
    /// single shared instance and its request carries a display LABEL, so two children of the same
    /// type are indistinguishable to it. The agent, by contrast, knows perfectly well whether it is
    /// the one waiting.</para>
    ///
    /// <para>Only matters with several children — with one, blocked and slow look alike and nobody
    /// is choosing between rows.</para>
    /// </summary>
    public event Action<bool>? PermissionWaitChanged;

    public void ReportPermissionWait(bool waiting) => PermissionWaitChanged?.Invoke(waiting);

    /// <summary>Raised when the classifier starts (true) or stops (false) being consulted for this
    /// call's gate. Same contract as <see cref="PermissionWaitChanged"/>: the agent subscribes and
    /// stamps the running Job so the row can show "reviewing…" while it is true.</summary>
    public event Action<bool>? ReviewingChanged;

    public void ReportReviewing(bool reviewing) => ReviewingChanged?.Invoke(reviewing);

    /// <summary>
    /// Raised whenever an executor reports a resource sample. ProcessRunner subscribes its
    /// ProcessResourceMonitor.Updated to ReportResources, which re-raises it here; the UI
    /// (Task 10 wiring) subscribes this event and marshals onto the UI thread itself — this
    /// class does not know about the UI thread.
    /// </summary>
    public event Action<ResourceSnapshot>? ResourceReported;

    public void ReportResources(ResourceSnapshot snapshot) => ResourceReported?.Invoke(snapshot);

    /// <summary>Raised when a worker invokes a tool. Same shape and same contract as
    /// <see cref="ResourceReported"/>: the UI subscribes and marshals to its own thread; this class
    /// knows nothing about the UI thread.</summary>
    public event Action<string, string>? ToolCallReported;

    /// <summary>Raised as a worker generates text. Same shape and contract as
    /// <see cref="ResourceReported"/>: the UI subscribes and marshals to its own thread.</summary>
    public event Action<string>? TextDeltaReported;

    public void ReportTextDelta(string delta) => TextDeltaReported?.Invoke(delta);

    public void ReportToolCall(string toolName, string summary)
    {
        ToolCallReported?.Invoke(toolName, summary);
        WriteLog("tool", $"{toolName}: {summary}");
    }

    /// <summary>
    /// Where this job may write a file the agent will be pointed at, or null when no logs were wired.
    ///
    /// <para>WHERE A COMMAND'S OVER-LONG OUTPUT GOES. It is the job's own subdirectory of the tree
    /// holding its <c>.log</c> files, on purpose: that tree is already per-session, already 0700, and
    /// already the only thing <c>FolderSessionStore.Prune</c> sweeps — so the file expires with the
    /// conversation that produced it and NOBODY HAS TO REMEMBER TO DELETE IT. Writing to
    /// <c>Path.GetTempPath()</c> instead, as the trigger plugin does, would leak a file per
    /// overflowing command forever, on the hot path of the most-used tool in the app.</para>
    ///
    /// <para>PER JOB RATHER THAN PER AGENT, which the sibling <c>.log</c> files are not. Those are
    /// named <c>&lt;jobId&gt;.log</c> so they can share one directory; a spill is named for its STREAM,
    /// so two shell jobs running at once under the same agent — the normal case under a fan-out —
    /// would otherwise both write <c>stdout.spill</c> into one directory and serve each other's output
    /// to the wrong model.</para>
    ///
    /// <para>NOT ON <see cref="IJobContext"/>, which plugins implement: a path only the built-in
    /// shell path needs is not worth a member on every plugin's context. Callers that want it match
    /// on this concrete type and treat its absence as "no spill", which is already a supported
    /// case.</para>
    /// </summary>
    public string? JobDir => _logs is null
        ? null
        : Path.Combine(Path.GetDirectoryName(_logs.PathFor(_agentId, _jobId, "log"))!, _jobId);

    public void Log(string line) => WriteLog("log", line);
    public void Log(JobLogLevel level, string line) => WriteLog("log", $"[{level}] {line}");

    private void WriteLog(string stream, string line)
    {
        // Best-effort: log I/O is diagnostic and must never fail the job (P2 stance).
        if (_logs is null) return;
        _ = SafeAppend(stream, line);
    }

    private async Task SafeAppend(string stream, string line)
    {
        try { await _logs!.AppendAsync(_agentId, _jobId, stream, line + Environment.NewLine); }
        catch { /* diagnostic only */ }
    }
}
