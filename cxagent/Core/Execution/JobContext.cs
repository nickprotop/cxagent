using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
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

    public void ReportProgress(double percent, string? message = null)
    {
        // TODO(P5): raise a progress event the UI subscribes to. Headless P3 is a no-op.
    }

    /// <summary>
    /// Raised when a plugin's real work begins, after any permission prompt has been answered. The
    /// caller uses it to start the duration clock, so a row reports how long the WORK took rather
    /// than how long the user took to approve it.
    /// </summary>
    public event Action? WorkStarted;

    public void WorkStarting() => WorkStarted?.Invoke();

    /// <summary>Set by the spawn branch to the child's description; null for the parent's own work.
    /// Settable rather than a constructor parameter so the ~20 construction sites are untouched.</summary>
    public string? Requester { get; set; }

    /// <summary>
    /// Raised whenever a plugin reports a resource sample. ProcessRunner subscribes its
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
