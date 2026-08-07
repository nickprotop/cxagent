using System.Text.Json;
using CxAgent.Core.Execution;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Helpers;

namespace CxAgent.Core.Orchestrator;

/// <summary>
/// Turns a <see cref="ConsultModification"/> — the orchestrator's raw `jobs_to_add` /
/// `parameter_changes` payload — into a compiled <see cref="DagModification"/> that
/// <see cref="DagModifier"/> can apply to a LIVE dag.
///
/// The sibling of <see cref="PlanCompiler"/>, and deliberately mirrors its two-pass structure
/// (assign every ULID first, then resolve depends_on). Two differences, both load-bearing:
///
/// 1. It resolves `depends_on` against the LIVE dag as well as the current batch. PlanCompiler
///    resolves only within its own payload, so it cannot express a dependency on a job that has
///    ALREADY RUN — which is exactly what a consult-added job almost always needs, because the
///    orchestrator is reacting to that job's output.
/// 2. It RETURNS FALSE with a message rather than throwing. The feedback loop must survive a bad
///    consult, report it, and carry on; an exception here would kill the goal mid-flight.
///
/// Nothing is mutated: the live dag is read-only input, and on any failure the returned
/// modification is empty — a half-applied batch would leave the dag inconsistent with what the
/// orchestrator was told it did.
/// </summary>
public static class ConsultJobCompiler
{
    public static bool TryCompile(
        JobDag liveDag,
        string goalId,
        ConsultModification mod,
        PluginRegistry plugins,
        out DagModification result,
        out string? error)
    {
        result = DagModification.Empty;
        error = null;

        if (!TryCompileJobs(liveDag, goalId, mod.JobsToAdd, plugins, out var jobs, out error))
            return false;

        if (!TryCompileParameterChanges(liveDag, mod.ParameterChanges, out var paramChanges, out error))
            return false;

        // JobIdsToRemove has no counterpart in ConsultModification: the loop routes removals through
        // ConsultDecision.CancelJobIds, which cancels a RUNNING job rather than deleting it.
        result = new DagModification(jobs, Array.Empty<string>(), paramChanges);
        return true;
    }

    private static bool TryCompileJobs(
        JobDag liveDag,
        string goalId,
        JsonElement? jobsToAdd,
        PluginRegistry plugins,
        out IReadOnlyList<Job> jobs,
        out string? error)
    {
        jobs = Array.Empty<Job>();
        error = null;

        if (jobsToAdd is not { } jobsElement || jobsElement.ValueKind == JsonValueKind.Null)
            return true;   // nothing to add is a valid modification, not a failure.

        if (jobsElement.ValueKind != JsonValueKind.Array)
        {
            error = $"'jobs_to_add' must be an array, got {jobsElement.ValueKind}.";
            return false;
        }

        // Every plan-local id already in flight. Colliding with one makes {{that_id.content}}
        // ambiguous, and JobExecutor's jobRef->id map would silently keep only one of them.
        var existingLocalIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var j in liveDag.AllJobs)
            if (!string.IsNullOrEmpty(j.PlanLocalId))
                existingLocalIds[j.PlanLocalId!] = j.Id;

        // --- Pass 1: assign a ULID to every new local id. ---
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal); // plan-local id -> ULID
        foreach (var j in jobsElement.EnumerateArray())
        {
            if (!TryGetString(j, "id", out var localId))
            {
                error = "A job in 'jobs_to_add' is missing a string 'id'.";
                return false;
            }
            if (idMap.ContainsKey(localId))
            {
                error = $"Duplicate plan-local id '{localId}' in 'jobs_to_add'.";
                return false;
            }
            if (existingLocalIds.ContainsKey(localId))
            {
                // Say what to do, not just what is wrong. Two distinct intentions land here and they
                // need opposite corrections: wanting a NEW job (pick an unused id) versus wanting to
                // build on an existing one (do not re-add it — depend on it). A bare "choose a
                // different id" pushes both toward a renamed duplicate of a job that already ran.
                error = $"Plan-local id '{localId}' is already used by job "
                      + $"'{existingLocalIds[localId]}' in the running plan. If you meant a NEW job, "
                      + $"give it an unused id (e.g. '{localId}_2'). If you meant to USE that job's "
                      + $"result, do not add it again — put '{localId}' in the new job's "
                      + "`depends_on`; its output reaches the new job automatically.";
                return false;
            }
            idMap[localId] = UlidGenerator.NewId();
        }

        // --- Pass 2: build jobs with mapped dependencies and validated params. ---
        var built = new List<Job>();
        foreach (var j in jobsElement.EnumerateArray())
        {
            var localId = j.GetProperty("id").GetString()!;   // pass 1 proved this is a string.

            if (!TryGetString(j, "type", out var type))
            {
                error = $"Job '{localId}' is missing a string 'type'.";
                return false;
            }
            if (!TryGetString(j, "name", out var name))
            {
                error = $"Job '{localId}' is missing a string 'name'.";
                return false;
            }
            if (!plugins.TryGet(type, out var plugin) || plugin is null)
            {
                error = $"Job '{localId}': unknown plugin type '{type}'.";
                return false;
            }

            var deps = new List<string>();
            if (j.TryGetProperty("depends_on", out var d) && d.ValueKind == JsonValueKind.Array)
                foreach (var dep in d.EnumerateArray())
                {
                    if (dep.ValueKind != JsonValueKind.String)
                    {
                        error = $"Job '{localId}' has a non-string entry in 'depends_on'.";
                        return false;
                    }
                    var depLocal = dep.GetString()!;

                    // Resolve against, in order: this batch, the live dag's plan-local ids, then a
                    // raw Job.Id. The last covers the orchestrator naming a job by the real id the
                    // digest showed it.
                    var depId = idMap.TryGetValue(depLocal, out var newId) ? newId
                              : existingLocalIds.TryGetValue(depLocal, out var liveId) ? liveId
                              : liveDag.TryGet(depLocal)?.Id;
                    if (depId is null)
                    {
                        error = $"Job '{localId}' depends on unknown id '{depLocal}'.";
                        return false;
                    }
                    if (!deps.Contains(depId)) deps.Add(depId);
                }

            var paramsDict = ReadParams(j);

            // Same fold-down PlanCompiler documents: `role` is advertised as a job-level field but
            // LlmAgentJobPlugin reads it from params, so a job-level role would be silently dropped.
            // A nested role wins — the plugin's schema documents that form, so it is more specific.
            if (j.TryGetProperty("role", out var roleEl)
                && roleEl.ValueKind == JsonValueKind.String
                && !paramsDict.ContainsKey("role"))
            {
                paramsDict["role"] = roleEl.Clone();
            }

            var jobParams = new JobParameters(paramsDict);
            var (wAction, wHasContent, wContent) = WriteJobValidator.Inspect(paramsDict);

            // The TWIN of PlanCompiler's check — one shared implementation, because a rule added to
            // one compiler and not the other is this codebase's most repeated defect (D7, D8, D11,
            // D12, and today's missing param validation).
            if (!WriteJobValidator.TryValidate(localId, type, wAction, wHasContent, deps.Count,
                    out var writeError))
            {
                error = writeError;
                return false;
            }

            // A leftover {{…}} in `content` is literal text now and would be written verbatim.
            if (!WriteJobValidator.TryValidateContent(localId, type, wAction, wContent,
                    out var contentError))
            {
                error = contentError;
                return false;
            }

            // Skipped for a write whose content arrives at run time from its one dependency —
            // FileJobPlugin requires `content`, so validating here would reject the valid shape.
            if (!WriteJobValidator.ContentComesFromDependency(type, wAction, wHasContent, deps.Count))
            {
                var validation = plugin.Validate(jobParams);
                if (!validation.IsValid)
                {
                    error = $"Job '{localId}' ('{type}'): {string.Join(", ", validation.Errors)}";
                    return false;
                }
            }


            built.Add(new Job
            {
                Id = idMap[localId],
                PlanLocalId = localId,   // without this, {{localId.content}} resolves to nothing.
                GoalId = goalId,
                PluginType = type,
                DisplayName = name,
                Parameters = jobParams,
                DependsOn = deps,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        // The read-feeds-replace shape, across the live dag and this batch together — the same
        // whole-plan scope D7 needs, and for the same reason: the read may have been added a turn
        // before the replace that depends on it.
        var editFacts = new List<EditJobValidator.JobFact>();
        foreach (var j in liveDag.AllJobs.Concat(built))
        {
            var (act, _, _) = WriteJobValidator.Inspect(j.Parameters.Values);
            var path = j.Parameters.Values.TryGetValue("path", out var pv) ? pv?.ToString() : null;
            // Dependencies are ULIDs here; the validator matches on the id it is given, so feed it
            // the same currency for both sides.
            editFacts.Add(new EditJobValidator.JobFact(
                j.Id, j.PluginType, act, path, j.DependsOn));
        }

        if (!EditJobValidator.TryValidate(editFacts, out error,
                fanOut: plugins.TryGet("llm_agent", out _)))
            return false;


        jobs = built;
        return true;
    }

    private static bool TryCompileParameterChanges(
        JobDag liveDag,
        IReadOnlyDictionary<string, JsonElement> changes,
        out IReadOnlyDictionary<string, JobParameters> compiled,
        out string? error)
    {
        compiled = new Dictionary<string, JobParameters>();
        error = null;
        if (changes.Count == 0) return true;

        var map = new Dictionary<string, JobParameters>(StringComparer.Ordinal);
        foreach (var (jobRef, value) in changes)
        {
            // Keyed by real job id, but accept a plan-local id too — it is what the orchestrator
            // named the job when it planned it, and DagModifier needs the real id.
            var job = liveDag.TryGet(jobRef)
                   ?? liveDag.AllJobs.FirstOrDefault(x => x.PlanLocalId == jobRef);
            if (job is null)
            {
                error = $"Cannot change parameters for unknown job '{jobRef}'.";
                return false;
            }
            if (value.ValueKind != JsonValueKind.Object)
            {
                error = $"Parameter changes for job '{jobRef}' must be an object, got {value.ValueKind}.";
                return false;
            }
            map[job.Id] = new JobParameters(ReadParams(value, wrapped: false));
        }

        compiled = map;
        return true;
    }

    /// <summary>
    /// Copies a params object into a dictionary, keeping every value as a <see cref="JsonElement"/>.
    /// JobParameters' contract: values from untyped sources stay JsonElement and Get&lt;T&gt; converts
    /// — pre-casting here would diverge from what a SQLite round-trip produces.
    /// </summary>
    private static Dictionary<string, object?> ReadParams(JsonElement element, bool wrapped = true)
    {
        var dict = new Dictionary<string, object?>();
        var source = element;
        if (wrapped)
        {
            if (!element.TryGetProperty("params", out source) || source.ValueKind != JsonValueKind.Object)
                return dict;
        }
        foreach (var prop in source.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();
        return dict;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return false;
        var s = p.GetString();
        if (string.IsNullOrWhiteSpace(s)) return false;
        value = s;
        return true;
    }

}
