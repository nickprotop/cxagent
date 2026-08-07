using System.Text;
using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;

namespace CxAgent.UI;

/// <summary>
/// Diagnoses one failed job: asks the LLM (via the suggest_recovery tool, DiagnoseTool) what went
/// wrong and what to do about it, and maps the answer to a validated AiDiagnosis. UI-agnostic — like
/// GoalRunner, it takes a provider + registry + logs and never touches a control; the caller (the
/// diagnosis dialog, a later task) decides how to present the result and whether to apply it.
///
/// Validation of jobs_to_insert covers both cases the spec calls out (§Diagnosis Request): a plugin
/// type that doesn't exist, and one that exists but whose params fail that plugin's own
/// IJobPlugin.Validate. Either failure gets fed back as a correction round under the same 2-round cap
/// — a malformed but real-shaped suggestion is exactly what a validation message can repair, whereas an
/// unparseable tool call (missing fields, bad action enum) is left to the never-throws path below since
/// a retry is unlikely to fix a genuinely confused response.
///
/// Diagnosis is a best-effort read, not an execution step: it must never throw on a provider failure
/// (mirrors ProviderProbe's never-throw contract — a diagnosis is an enhancement, not something that
/// should take down a goal that has already failed) and it must never touch job.RetryCount — only
/// actually executing a retry consumes retry headroom (spec: analysing a failure is free).
/// </summary>
public sealed class JobDiagnoser
{
    private const int MaxCorrectionRounds = 2;
    private const int LogTailLines = 50;

    private readonly ILlmProvider _provider;
    private readonly PluginRegistry _plugins;
    private readonly LogFileManager _logs;
    private readonly Action<LlmUsage>? _onUsage;

    /// <param name="onUsage">
    /// Invoked with each round's token usage, if the caller wants to track it (e.g. into a
    /// TokenLedger — Task 11 review I3: without this, diagnosis rounds are invisible to both the
    /// status-bar cost readout and the goal token budget, since JobDiagnoser calls the provider
    /// directly and previously recorded nowhere). Null (the default) preserves prior behavior exactly
    /// — usage is simply not tracked, same as before this parameter existed.
    /// </param>
    public JobDiagnoser(ILlmProvider provider, PluginRegistry plugins, LogFileManager logs,
        Action<LlmUsage>? onUsage = null)
    {
        _provider = provider;
        _plugins = plugins;
        _logs = logs;
        _onUsage = onUsage;
    }

    public async Task<AiDiagnosis?> DiagnoseJobAsync(Job job, JobDag dag, CancellationToken ct)
    {
        try
        {
            var messages = new List<ChatMessage> { new() { Role = "user", Content = await BuildPromptAsync(job, dag) } };
            var tools = new List<ToolDefinition> { DiagnoseTool.BuildDefinition(_plugins) };

            for (int round = 0; ; round++)
            {
                // ChatAsync, not ChatStreamAsync: diagnosis has no streaming UI to feed and wants one
                // complete tool call to validate, not incremental deltas.
                var response = await _provider.ChatAsync(messages, tools, ct);
                _onUsage?.Invoke(response.Usage);
                var call = response.ToolCalls.FirstOrDefault(c => c.Name == "suggest_recovery");
                if (call is null)
                    return null;

                if (TryMapToDiagnosis(call.Arguments, out var diagnosis, out var errors))
                    return diagnosis;

                if (round >= MaxCorrectionRounds - 1)
                    return null;   // bounded correction: stop feeding a stubborn model, don't loop forever.

                // Feed the assistant's bad call back plus a correction, and let it try again.
                messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "",
                    ToolCalls = new List<ToolCall> { call },
                });
                messages.Add(new ChatMessage
                {
                    Role = "user",
                    ToolCallId = call.Id,
                    Content = BuildCorrectionMessage(errors!),
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string> BuildPromptAsync(Job job, JobDag dag)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A job in the running plan failed. Diagnose the cause and propose a recovery.");
        sb.AppendLine();
        sb.AppendLine($"Job: {job.DisplayName} (type: {job.PluginType})");
        sb.AppendLine($"Exit code: {job.Result?.ExitCode}");
        if (!string.IsNullOrWhiteSpace(job.Result?.ErrorMessage))
            sb.AppendLine($"Error message: {job.Result!.ErrorMessage}");
        if (job.StartedAt is { } started && job.CompletedAt is { } completed)
            sb.AppendLine($"Elapsed: {completed - started}");

        var ancestors = dag.GetAncestors(job.Id);
        if (ancestors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Preceding jobs:");
            foreach (var a in ancestors)
                sb.AppendLine($"- {a.DisplayName} ({a.PluginType}): {a.State}");
        }

        var log = await _logs.ReadAsync(job.GoalId, job.Id, "log");
        if (!string.IsNullOrEmpty(log))
        {
            var tail = log.Split('\n').Reverse().Take(LogTailLines).Reverse();
            sb.AppendLine();
            sb.AppendLine($"Last {LogTailLines} log lines:");
            sb.AppendLine(string.Join('\n', tail));
        }

        return sb.ToString();
    }

    private string BuildCorrectionMessage(IReadOnlyList<string> errors)
    {
        var legal = string.Join(", ", PluginSchemaText.TypeNames(PluginSchemaText.OrderedPlugins(_plugins)));
        return $"Your suggest_recovery call was invalid: {string.Join("; ", errors)}. "
             + $"Valid plugin types are: {legal}. Call suggest_recovery again with corrected jobs_to_insert.";
    }

    private bool TryMapToDiagnosis(JsonElement args, out AiDiagnosis? diagnosis, out List<string>? errors)
    {
        diagnosis = null;
        errors = null;

        var cause = args.TryGetProperty("cause", out var c) ? c.GetString() ?? "" : "";
        var rationale = args.TryGetProperty("rationale", out var r) ? r.GetString() ?? "" : "";
        var actionText = args.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
        if (!TryMapAction(actionText, out var action))
            return false;   // not a validation round-trip case per the spec; treat as unmappable.

        DagModification? modification = null;

        if (action == RecoveryAction.InsertBefore && args.TryGetProperty("jobs_to_insert", out var jobsToInsert)
            && jobsToInsert.ValueKind == JsonValueKind.Array)
        {
            var jobs = new List<Job>();
            var bad = new List<string>();

            foreach (var j in jobsToInsert.EnumerateArray())
            {
                var type = j.GetProperty("type").GetString()!;
                if (!_plugins.TryGet(type, out var plugin))
                {
                    bad.Add($"unknown plugin type '{type}'");
                    continue;
                }

                var paramsDict = new Dictionary<string, object?>();
                if (j.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                    foreach (var prop in p.EnumerateObject())
                        paramsDict[prop.Name] = prop.Value.Clone();

                var jobParams = new JobParameters(paramsDict);
                var validation = plugin!.Validate(jobParams);
                if (!validation.IsValid)
                {
                    bad.Add($"'{type}' job '{j.GetProperty("id").GetString()}': {string.Join(", ", validation.Errors)}");
                    continue;
                }

                jobs.Add(new Job
                {
                    Id = j.GetProperty("id").GetString()!,
                    GoalId = "",   // plan-local placeholder; the applier assigns real ids/goal, like PlanCompiler does.
                    PluginType = type,
                    DisplayName = j.GetProperty("name").GetString()!,
                    Parameters = jobParams,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }

            if (bad.Count > 0)
            {
                errors = bad;
                return false;
            }

            modification = new DagModification(jobs, Array.Empty<string>(), new Dictionary<string, JobParameters>());
        }
        else if (action == RecoveryAction.ModifyAndRetry && args.TryGetProperty("parameter_changes", out var pc)
            && pc.ValueKind == JsonValueKind.Object)
        {
            var changes = new Dictionary<string, JobParameters>();
            foreach (var prop in pc.EnumerateObject())
            {
                var paramsDict = new Dictionary<string, object?>();
                if (prop.Value.ValueKind == JsonValueKind.Object)
                    foreach (var inner in prop.Value.EnumerateObject())
                        paramsDict[inner.Name] = inner.Value.Clone();
                changes[prop.Name] = new JobParameters(paramsDict);
            }
            modification = new DagModification(Array.Empty<Job>(), Array.Empty<string>(), changes);
        }

        diagnosis = new AiDiagnosis(cause, action, rationale, modification);
        return true;
    }

    private static bool TryMapAction(string wire, out RecoveryAction action)
    {
        switch (wire)
        {
            case "retry": action = RecoveryAction.Retry; return true;
            case "modify_and_retry": action = RecoveryAction.ModifyAndRetry; return true;
            case "insert_before": action = RecoveryAction.InsertBefore; return true;
            case "skip": action = RecoveryAction.Skip; return true;
            case "ask_user": action = RecoveryAction.AskUser; return true;
            default: action = default; return false;
        }
    }
}
