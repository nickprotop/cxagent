using CxAgent.Core.Jobs;
using CxAgent.Core.Models;

namespace CxAgent.Plugins.Testing;

/// <summary>How the test answers a call's permission question — what a user would click.</summary>
public enum GateAnswer { Allow, Deny }

/// <summary>
/// The plugin's own question before the call runs — what its sidecar's <c>gated</c> and its
/// <c>Gate</c> decide. A session in an asking mode may first ask whether to use the tool at all; that
/// question is cxagent's, the same for every tool, and not modelled here.
/// </summary>
/// <param name="Display">The words on the prompt.</param>
/// <param name="AlwaysAskable">Whether "Always" is offered.</param>
/// <param name="Subject">What the auto-reviewer judges instead of the words, when the plugin gave one.</param>
public sealed record GateQuestion(string Display, bool AlwaysAskable, string? Subject);

/// <summary>One thing a plugin reported while its call ran, in the order it reported them.</summary>
public abstract record HarnessReport
{
    public sealed record Progress(double Percent, string? Message) : HarnessReport;
    public sealed record Log(JobLogLevel Level, string Line) : HarnessReport;
    public sealed record Stream(string Text) : HarnessReport;
}

/// <summary>
/// What one call did: the question a user would have been asked, how it was answered, the result, and
/// every report the plugin made while it ran.
/// </summary>
/// <param name="Gate">The plugin's question, or null when the plugin asks nothing.</param>
/// <param name="Asked">Whether a user would have been asked.</param>
/// <param name="Answer">The test's answer; null when it gave none.</param>
/// <param name="Result">The tool's result; null when the call never ran — stopped at the gate, or denied.</param>
/// <param name="Reports">Progress, log lines and streamed text, in order.</param>
public sealed record HarnessCall(GateQuestion? Gate, bool Asked, GateAnswer? Answer, JobResult? Result,
    IReadOnlyList<HarnessReport> Reports)
{
    /// <summary>A user would have been asked, and nobody answered: the call went no further.</summary>
    public bool StoppedAtGate => Asked && Answer is null;

    public IReadOnlyList<HarnessReport.Progress> Progress => [.. Reports.OfType<HarnessReport.Progress>()];
    public IReadOnlyList<HarnessReport.Log> Logs => [.. Reports.OfType<HarnessReport.Log>()];

    /// <summary>Everything the call streamed, joined.</summary>
    public string Streamed => string.Concat(Reports.OfType<HarnessReport.Stream>().Select(s => s.Text));
}
