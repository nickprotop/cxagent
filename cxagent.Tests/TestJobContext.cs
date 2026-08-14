using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Tests;

/// <summary>
/// Shared fake IJobContext for job plugin tests. Extracted here so there is exactly ONE copy — no
/// duplication between test files (same reason TestProviders.cs exists).
/// </summary>
public sealed class TestJobContext : IJobContext
{
    public TestJobContext(IReadOnlyDictionary<string, JobResult>? completedOutputs = null,
        IReadOnlyDictionary<string, string>? displayNames = null)
    {
        CompletedJobOutputs = completedOutputs ?? new Dictionary<string, JobResult>();
        CompletedJobNames = displayNames ?? new Dictionary<string, string>();
    }

    public IReadOnlyDictionary<string, JobResult> CompletedJobOutputs { get; }
    public IReadOnlyDictionary<string, string> CompletedJobNames { get; }
    public List<(JobLogLevel Level, string Line)> Logs { get; } = new();

    /// <summary>Just the text, for assertions that do not care about level.</summary>
    public IEnumerable<string> LogLines => Logs.Select(l => l.Line);

    public void WorkStarting() { }

    /// <summary>Recorded rather than ignored, so a test can assert a job reported itself blocked on
    /// a prompt — the signal a parent's row turns into "waiting for permission".</summary>
    public List<bool> PermissionWaits { get; } = [];
    public void ReportPermissionWait(bool waiting) => PermissionWaits.Add(waiting);

    public string? Requester => null;

    /// <summary>Settable so a test can root a plugin somewhere real; null keeps the process's own,
    /// which is what every test got before the property existed.</summary>
    public string? WorkingDirectory { get; set; }

    public void ReportProgress(double percent, string? message = null) { }
    public void Log(string line) => Logs.Add((JobLogLevel.Info, line));
    public void Log(JobLogLevel level, string line) => Logs.Add((level, line));
    public void ReportResources(ResourceSnapshot snapshot) { }

    /// <summary>Recorded so a test can assert a worker actually reported its tool calls — the
    /// signal the transcript nests under a running job.</summary>
    public List<(string Tool, string Summary)> ToolCalls { get; } = new();
    public void ReportToolCall(string toolName, string summary) => ToolCalls.Add((toolName, summary));
    public List<string> TextDeltas { get; } = new();
    public void ReportTextDelta(string delta) => TextDeltas.Add(delta);
}
