using System.Text;
using CxAgent.Core.Models;

namespace CxAgent.Core.Execution;

/// <summary>
/// What the orchestrator sees when a job finishes: bounded by construction, and carrying the job's
/// RESOLVED parameters as well as its outcome.
///
/// <para>The parameters are not decoration. A live drive showed the orchestrator writing
/// <c>{{read_app_paths.content}}</c> while its own job carried a different id — it had paraphrased
/// its own id and could not see the mistake, because nothing ever showed it what it had written.
/// A digest that reports only state and output leaves that class of error undiagnosable by the one
/// party able to fix it.</para>
///
/// <para>Truncation policy is deliberately the SAME as
/// <c>LlmAgentJobPlugin.FormatDependencyOutputs</c>: bounded head+tail, a VISIBLE elision marker
/// (the model must know text was cut, or it reasons confidently about output it never saw),
/// <see cref="JobResult.ErrorMessage"/> NEVER truncated — the orchestrator is about to decide what
/// to do BECAUSE of that error — and <see cref="JobResult.LogFile"/> named whenever output was cut,
/// so large payloads live in files rather than in context.</para>
/// </summary>
public record JobDigest(
    string Id,
    string PlanLocalId,
    string DisplayName,
    string PluginType,
    string? Role,
    JobState State,
    TimeSpan? Duration,
    int? ExitCode,
    IReadOnlyDictionary<string, string> Parameters,
    string OutputExcerpt,
    long OutputBytes,
    string? ErrorMessage,
    string? LogFile,
    bool Truncated)
{
    public const int DefaultPerValueCap = 2048;

    /// <param name="perValueCap">
    /// Cap on a PARAMETER value. Params stay bounded because a write_file job's `content` is
    /// routinely the entire artefact being written, and it is echoed back verbatim in the pending
    /// list on EVERY consult — a cost the output does not have, since a job's output is rendered
    /// once per digest.
    /// </param>
    /// <param name="outputCap">
    /// Cap on the rendered OUTPUT, or null for none. Null is the default because a truncated result
    /// is what the orchestrator plans and answers from: it decides continue/modify/finish from this
    /// text, and SayClosingAnswerAsync writes the user's reply out of it with no tool available to
    /// fetch the rest. Cutting a worker's review in half there silently discards findings, and the
    /// audit that measured this found only file reads being truncated — whose bulk is now withheld
    /// on the consult path by bulkOutputAsPlaceholder instead, which is the targeted fix a blanket
    /// cap was standing in for.
    /// </param>
    public static JobDigest From(Job job, int perValueCap = DefaultPerValueCap, int? outputCap = null)
    {
        var result = job.Result;

        // Ordinal ordering: Dictionary enumeration order is not a documented guarantee, and an
        // unstable digest changes the prompt every consult — defeating provider-side prompt caching
        // for the same reason CreatePlanTool's schema is ordinally sorted.
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in job.Parameters.Values)
            parameters[key] = Truncate(value?.ToString() ?? "", perValueCap);

        var body = result is null ? "" : RenderOutput(result.Output);

        return new JobDigest(
            Id: job.Id,
            PlanLocalId: job.PlanLocalId ?? "",
            DisplayName: job.DisplayName,
            PluginType: job.PluginType,
            // Read through JobParameters rather than a cast: values become JsonElement after a
            // SQLite round-trip, so a blind (string) throws on every reload.
            Role: job.Parameters.Get<string?>("role", null),
            State: job.State,
            Duration: result?.Duration,
            ExitCode: result?.ExitCode,
            Parameters: parameters,
            OutputExcerpt: outputCap is { } cap ? Truncate(body, cap) : body,
            OutputBytes: body.Length,
            // In full, always.
            ErrorMessage: result?.ErrorMessage,
            LogFile: result?.LogFile,
            // Values become JsonElement after a SQLite round-trip, so a blind (bool) cast throws on
            // every reload — same reason Role above is read through JobParameters rather than a cast.
            Truncated: result?.Output is { } o
                       && o.TryGetValue("truncated", out var t)
                       && t?.ToString() is "True" or "true");
    }

    /// <summary>
    /// One compact block per job. Deterministic for the same input — see the ordering note in
    /// <see cref="From"/>.
    /// </summary>
    /// <param name="bulkOutputAsPlaceholder">
    /// Replace a NON-worker job's output with a placeholder naming its true size and log file.
    ///
    /// <para>For the consult path. An instrumented drive found every JobDigest truncation was a
    /// `file` read — 9,007 chars cut to 2,078, twice per job — and none was a worker. That content
    /// is an ECHO: the worker that ran the job already read it, analysed it, and reported. Handing
    /// the orchestrator a mangled copy to decide continue/modify/finish costs ~25KB a goal and
    /// answers no question `consult`'s schema can ask.</para>
    ///
    /// <para>A PLACEHOLDER, never a silent drop — the same rule Anthropic's context editing follows
    /// ("replaces each cleared result with placeholder text so Claude knows it was removed"). The
    /// orchestrator must be able to tell the difference between a job that produced nothing and one
    /// whose output it is not being shown.</para>
    ///
    /// <para>Default false because <c>SayClosingAnswerAsync</c> must keep the body: it SYNTHESISES
    /// the user's answer from these digests, has no tools to fetch more, and a placeholder there
    /// would turn "here is what those scripts do" into "I read three scripts".</para>
    /// </param>
    public string Render(bool bulkOutputAsPlaceholder = false)
    {
        var sb = new StringBuilder();
        var label = string.IsNullOrEmpty(PlanLocalId) ? Id : PlanLocalId;
        sb.Append($"[{label}] {DisplayName} ({PluginType}");
        if (!string.IsNullOrWhiteSpace(Role)) sb.Append($", role={Role}");
        sb.AppendLine($") — {State}");

        // Immediately after the state line, before params/output — a "truncated: True" line buried
        // among other output keys reads as a flag, not a consequence. This is the consequence.
        if (Truncated)
            sb.AppendLine("  ⚠ INCOMPLETE — this worker hit its turn limit and returned partial output. "
                        + "Its answer is NOT the whole task. Split the remaining work into smaller jobs, "
                        + "or raise orchestrator.maxWorkerTurns.");

        if (Duration is { } d) sb.AppendLine($"  duration: {d.TotalSeconds:0.0}s");
        if (ExitCode is { } code && code != 0) sb.AppendLine($"  exit code: {code}");

        if (Parameters.Count > 0)
        {
            sb.AppendLine("  params:");
            foreach (var (key, value) in Parameters)
                sb.AppendLine($"    {key}: {value}");
        }

        // Before the output, because the orchestrator is deciding what to do BECAUSE of it.
        if (!string.IsNullOrWhiteSpace(ErrorMessage))
            sb.AppendLine($"  error: {ErrorMessage}");

        // A WORKER's output is the answer and is never elided here; anything else is an echo of
        // something a job already consumed. Gated on OutputBytes so a job that genuinely produced
        // nothing still renders as nothing rather than as "0 chars withheld".
        if (bulkOutputAsPlaceholder && PluginType != "llm_agent" && OutputBytes > 0)
        {
            sb.AppendLine($"  output: {OutputBytes:N0} chars — withheld here; the job that ran it "
                        + "already consumed it.");
            if (!string.IsNullOrWhiteSpace(LogFile))
                sb.AppendLine($"  (full output: {LogFile})");
        }
        else if (!string.IsNullOrWhiteSpace(OutputExcerpt))
        {
            sb.AppendLine("  output:");
            sb.AppendLine(OutputExcerpt);
            // Only when something was actually cut — naming a log for a 20-byte result is noise.
            if (OutputExcerpt.Length != OutputBytes && !string.IsNullOrWhiteSpace(LogFile))
                sb.AppendLine($"  (full output: {LogFile})");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Flattens an output bag to text. 'content' renders bare because it is the substance of the
    /// result; every other key is labelled, since a shell job's <c>exit_code</c> means nothing
    /// unlabelled — and dropping unknown keys would render such a job as blank.
    ///
    /// <para><c>internal</c>, not private, so <c>IntrospectionTools.GetJobOutput</c> can share it.
    /// That tool needs the UN-TRUNCATED body to slice by offset/limit, so it cannot go through
    /// <see cref="From"/> — but a second copy of this convention would drift, and then the same job
    /// would read differently depending on which tool the orchestrator happened to call.</para>
    /// </summary>
    internal static string RenderOutput(Dictionary<string, object?>? output)
    {
        if (output is null || output.Count == 0) return "";

        var parts = new List<string>();
        foreach (var (key, value) in output.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var text = value?.ToString();
            if (string.IsNullOrEmpty(text)) continue;

            // A PROPOSAL, not an output blob. Rendered as raw JSON under a bare key it reads as
            // "here is some data", and the orchestrator summarises it instead of adopting it —
            // which looks exactly like a goal that quietly does less than it was asked. Saying what
            // it IS and what to do with it is the difference between a plan and a paragraph.
            if (key == "proposed_jobs")
            {
                parts.Add("PROPOSED JOBS — this planner is asking you to run these. Adopt the ones "
                        + "you want with action `modify` and `jobs_to_add`, or say why not:\n" + text);
                continue;
            }

            parts.Add(key == "content" ? text : $"{key}: {text}");
        }
        return string.Join("\n", parts);
    }

    /// <summary>
    /// Head+tail truncation with a visible count. Both ends are kept because a result typically
    /// states what it did at the top and concludes at the bottom; a head-only cut discards the
    /// conclusion, which is usually the part that matters.
    /// </summary>
    private static string Truncate(string text, int cap)
    {
        if (cap <= 0 || text.Length <= cap) return text;

        var half = cap / 2;
        var elided = text.Length - (half * 2);
        // A marker longer than what it saves is not a saving.
        if (elided <= 0) return text;

        return text[..half] + $"\n[... {elided:N0} bytes elided ...]\n" + text[^half..];
    }
}
