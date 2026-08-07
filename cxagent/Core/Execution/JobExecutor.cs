using System.Text.Json;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Execution;

/// <summary>
/// The job engine: turns a Job into real work by dispatching to an IJobPlugin. Holds the
/// JobDag so it can build IJobContext.CompletedJobOutputs from the job's dependency results.
/// RunJobAsync matches P1's Func&lt;Job, CancellationToken, Task&lt;JobResult&gt;&gt; — pass it to
/// the Orchestrator to replace the demo stub. P1's scheduler/DAG/signature are unchanged.
/// </summary>
public class JobExecutor
{
    private readonly PluginRegistry _registry;
    private readonly JobDag _dag;
    private readonly LogFileManager? _logs;
    private readonly Action<string, ResourceSnapshot>? _onResource;

    /// <summary>Invoked with (jobId, delta) as a worker generates text. Same wire as
    /// <c>_onResource</c>: the plugin raises it, this joins it to whoever is listening, and null
    /// means the deltas are dropped.</summary>
    private readonly Action<string, string>? _onTextDelta;

    /// <param name="onResource">
    /// Invoked with (jobId, snapshot) whenever the job's IJobContext.ResourceReported fires. This is
    /// the wire that closes the resource-reporting gap: ProcessRunner already raises
    /// ctx.ReportResources and the UI already knows how to render a snapshot, but nothing joined
    /// them because RunJobAsync built a bare JobContext. Null (the default) means no one is
    /// listening — resource samples are simply dropped, exactly as before this parameter existed.
    /// </param>
    public JobExecutor(PluginRegistry registry, JobDag dag, LogFileManager? logs = null,
        Action<string, ResourceSnapshot>? onResource = null,
        Action<string, string>? onTextDelta = null)
    {
        _registry = registry;
        _dag = dag;
        _logs = logs;
        _onResource = onResource;
        _onTextDelta = onTextDelta;
    }

    public async Task<JobResult> RunJobAsync(Job job, CancellationToken ct)
    {
        // 1. Resolve the plugin. Missing → fail immediately (no retry).
        if (!_registry.TryGet(job.PluginType, out var plugin) || plugin is null)
            return new JobResult { Success = false, ExitCode = -1,
                ErrorMessage = $"No plugin registered for type '{job.PluginType}'." };

        // 2. Build context: dependency results keyed by dependency ULID Id, plus their display names
        //    keyed the same way. The names are what an llm_agent worker sees as block labels — a raw
        //    ULID there is unreadable to a model whose plan referred to jobs by name.
        var completedOutputs = new Dictionary<string, JobResult>();
        var completedNames = new Dictionary<string, string>();
        foreach (var depId in job.DependsOn)
            if (_dag.TryGet(depId) is { } dep && dep.Result is { } depResult)
            {
                completedOutputs[depId] = depResult;
                completedNames[depId] = dep.DisplayName;
            }
        var context = new JobContext(job.GoalId, job.Id, completedOutputs, _logs, completedNames);
        if (_onResource is not null)
            context.ResourceReported += snapshot => _onResource(job.Id, snapshot);
        if (_onTextDelta is not null)
            context.TextDeltaReported += delta => _onTextDelta(job.Id, delta);

        // 3. Params are LITERAL TEXT. The {{job.key}} reference syntax is gone: it asked the model
        //    to state one fact twice — once in depends_on, once as a name inside a param — and every
        //    observed failure was the two disagreeing. Data now moves by DECLARATION alone (step 3b
        //    below, and LlmAgentJobPlugin's dependency block), so {{anything}} is just characters.
        var effectiveParameters = job.Parameters;

        // 3b. INJECT a write job's content from its single dependency. The model no longer names an
        //     output — declaring the dependency IS the request for its content, the same way an
        //     llm_agent job already receives every dependency's output ahead of its prompt.
        //
        //     Before plugin.Validate below, deliberately: FileJobPlugin requires `content` for a
        //     write, and under this design the param is legally absent at plan time and present at
        //     run time.
        if (TryInjectDependencyContent(job, effectiveParameters, completedOutputs, completedNames,
                out var injected, out var injectError))
            effectiveParameters = injected!;
        else if (injectError is not null)
            return new JobResult { Success = false, ExitCode = -1, ErrorMessage = injectError };

        // 4. Validate params — on the SUBSTITUTED values, so a plugin's rules (non-empty command,
        //    path shape, …) apply to what it will actually receive rather than to a placeholder.
        var validation = plugin.Validate(effectiveParameters);
        if (!validation.IsValid)
            return new JobResult { Success = false, ExitCode = -1,
                ErrorMessage = string.Join("; ", validation.Errors) };

        // 5. Execute (defensively catch plugin exceptions — the scheduler also catches).
        try
        {
            return await plugin.ExecuteAsync(effectiveParameters, context, ct);
        }
        catch (Exception ex)
        {
            return new JobResult { Success = false, ExitCode = -1, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// A <c>file</c> write/append with exactly one dependency and no <c>content</c> takes that
    /// dependency's output. Returns false with a null error when injection simply does not apply.
    ///
    /// <para>This replaces <c>{{dep.content}}</c>. The reference syntax asked the model to state the
    /// same fact twice — once in <c>depends_on</c>, once as a name inside a param — and every
    /// observed failure was the two disagreeing: a literal <c>key</c> token, a name belonging to no
    /// job, a reference whose dependency was never declared. Declaring the dependency IS the
    /// request now, so there is nothing left to misname.</para>
    ///
    /// <para>A dependency with nothing to give FAILS the job rather than writing what it has.
    /// A skipped job's content is the string "[not available — …]", and a live drive already
    /// produced an 18-byte AUDIT.md holding a placeholder while reporting success. Writing a
    /// placeholder to disk is the exact defect this design removes; re-creating it here would be
    /// the worst possible outcome, and it is the quiet-failure trap CrewAI is documented to have.</para>
    /// </summary>
    private static bool TryInjectDependencyContent(Job job, JobParameters current,
        IReadOnlyDictionary<string, JobResult> completedOutputs,
        IReadOnlyDictionary<string, string> completedNames,
        out JobParameters? injected, out string? error)
    {
        injected = null;
        error = null;

        if (job.PluginType != "file") return false;
        var action = current.Values.TryGetValue("action", out var a) ? a?.ToString() : null;
        if (action is not ("write" or "append")) return false;

        // An explicit param always wins: injection fills a GAP, it never overrides what was authored.
        if (current.Values.TryGetValue("content", out var existing)
            && !string.IsNullOrEmpty(existing?.ToString()))
            return false;

        if (job.DependsOn.Count != 1) return false;   // ambiguity is rejected at COMPILE time
        var depId = job.DependsOn[0];
        if (!completedOutputs.TryGetValue(depId, out var depResult)) return false;

        var name = completedNames.TryGetValue(depId, out var n) ? n : depId;

        // Named so the message points at the job that actually has nothing to give — the write job
        // is the messenger here, not the cause.
        if (depResult.Output.TryGetValue("skipped", out var skipped)
            && skipped?.ToString() is "True" or "true")
        {
            error = $"cannot write: '{name}' was skipped and produced no content. "
                  + "Nothing was written.";
            return false;
        }

        var content = depResult.Output.TryGetValue("content", out var c) ? c?.ToString() : null;
        if (string.IsNullOrWhiteSpace(content))
        {
            error = $"cannot write: '{name}' produced no content. Nothing was written.";
            return false;
        }

        var values = new Dictionary<string, object?>(current.Values) { ["content"] = content };
        injected = new JobParameters(values);
        return true;
    }
}
