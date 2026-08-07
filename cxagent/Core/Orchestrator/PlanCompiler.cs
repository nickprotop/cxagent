using System.Text.Json;
using CxAgent.Core.Models;
using CxAgent.Core.Execution;
using CxAgent.Helpers;

namespace CxAgent.Core.Orchestrator;

/// <summary>
/// Compiles an LLM create_plan tool-call payload into a JobDag: maps plan-local ids to ULIDs,
/// wires depends_on to the mapped ULIDs, and rejects duplicate local ids or dangling dependencies.
/// Extracted verbatim from Orchestrator so both the Orchestrator and the UI's GoalRunner share
/// one plan-compilation path (no duplication).
/// </summary>
public static class PlanCompiler
{
    /// <param name="plugins">
    /// When supplied, every job's params are checked against its plugin's own Validate BEFORE the
    /// plan is accepted — the same gate ConsultJobCompiler has applied since it was written.
    ///
    /// <para>Optional only so existing callers and tests keep compiling; every production path
    /// passes it. Without it a plan naming an llm_agent job with no `prompt` compiled clean, was
    /// dispatched, and failed at execution with "'prompt' is required" — a wasted dispatch and a red
    /// job where the model should simply have been asked to fix its plan. Reference validation was
    /// added to this compiler for exactly the same reason (D8, see below); param validation is its
    /// twin, and was missed.</para>
    /// </param>
    public static JobDag BuildDag(string goalId, JsonElement planArgs,
        Plugins.PluginRegistry? plugins = null)
    {
        var dag = new JobDag();
        var idMap = new Dictionary<string, string>(); // plan-local id -> ULID
        var jobsElement = planArgs.GetProperty("jobs");

        // Pass 1: assign ULIDs.
        foreach (var j in jobsElement.EnumerateArray())
        {
            var localId = j.GetProperty("id").GetString()!;
            if (idMap.ContainsKey(localId))
                throw new InvalidOperationException($"Duplicate plan-local id '{localId}'.");
            idMap[localId] = UlidGenerator.NewId();
        }

        // D7: collected alongside pass 2 below (same paramsDict / role fold-down), then checked once
        // every job in the batch is built — the rule needs to see the whole plan at once, not one
        // job at a time.
        var editFacts = new List<EditJobValidator.JobFact>();

        // Pass 2: build jobs with mapped dependencies.
        foreach (var j in jobsElement.EnumerateArray())
        {
            var localId = j.GetProperty("id").GetString()!;
            var deps = new List<string>();
            var depLocalIds = new List<string>();
            if (j.TryGetProperty("depends_on", out var d) && d.ValueKind == JsonValueKind.Array)
                foreach (var dep in d.EnumerateArray())
                {
                    var depLocal = dep.GetString()!;
                    depLocalIds.Add(depLocal);
                    if (!idMap.TryGetValue(depLocal, out var depUlid))
                        throw new InvalidOperationException($"Job '{localId}' depends on unknown id '{depLocal}'.");
                    deps.Add(depUlid);
                }

            var paramsDict = new Dictionary<string, object?>();
            if (j.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                foreach (var prop in p.EnumerateObject())
                    paramsDict[prop.Name] = prop.Value.Clone(); // JsonElement — JobParameters.Get converts

            // `role` is advertised as a JOB-LEVEL field (CreatePlanTool puts it beside id/name/type),
            // but LlmAgentJobPlugin reads it from JobParameters — i.e. from inside `params`. Without
            // this fold-down, a job-level role was silently DROPPED and the feature worked only when
            // the model happened to guess `params` instead.
            //
            // P7's live drive measured the cost: 3 of 4 llm_agent jobs ran with role='(none)',
            // including a goal that named the reviewer role explicitly. That was misread at the time
            // as the local model being weak at optional enum fields; it was this. The one job that
            // DID resolve a role was the model happening to nest it correctly.
            //
            // A role inside `params` wins if both are present — the plugin's own schema documents
            // that form too, so an explicit nested value is the more specific instruction.
            if (j.TryGetProperty("role", out var roleEl)
                && roleEl.ValueKind == JsonValueKind.String
                && !paramsDict.ContainsKey("role"))
            {
                paramsDict["role"] = roleEl.Clone();
            }


            var type = j.GetProperty("type").GetString()!;
            var role = paramsDict.TryGetValue("role", out var roleVal)
                && roleVal is JsonElement { ValueKind: JsonValueKind.String } roleJe ? roleJe.GetString() : null;
            var action = paramsDict.TryGetValue("action", out var actionVal)
                && actionVal is JsonElement { ValueKind: JsonValueKind.String } actionJe ? actionJe.GetString() : null;
            var path = paramsDict.TryGetValue("path", out var pathVal)
                && pathVal is JsonElement { ValueKind: JsonValueKind.String } pathJe ? pathJe.GetString() : null;
            editFacts.Add(new EditJobValidator.JobFact(localId, type, action, path, depLocalIds));

            // THE PARAMS THEMSELVES. Everything above validates the plan's SHAPE — ids, references,
            // review boundaries — and none of it notices that a job is missing a param it cannot run
            // without. Seen live: an llm_agent job planned with no `prompt` compiled clean, was
            // dispatched, and died with "'prompt' is required", showing a failed job instead of a
            // corrected plan. The plugin already knows its own requirements; asking it here is the
            // difference between a re-plan and a wasted dispatch.
            var (wAction, wHasContent, wContent) = WriteJobValidator.Inspect(paramsDict);

            // AMBIGUOUS WRITE: more than one dependency and no authored content. Injection cannot
            // choose between them, and picking one silently would write an arbitrary dependency's
            // output to the user's file.
            if (!WriteJobValidator.TryValidate(localId, type, wAction, wHasContent, deps.Count,
                    out var writeError))
                throw new InvalidOperationException(writeError);

            // A leftover {{…}} in `content` is literal text now and would be written verbatim.
            if (!WriteJobValidator.TryValidateContent(localId, type, wAction, wContent,
                    out var contentError))
                throw new InvalidOperationException(contentError);

            // Only when the plugin is PRESENT. A registry legitimately lacks optional plugins —
            // llm_agent registers only when a provider resolver is supplied — and treating absence
            // as a bad plan would reject every worker job compiled against a bare registry. Unknown
            // types are already caught downstream; this guard is about params, nothing else.
            //
            // SKIPPED for a write whose content arrives at run time from its one dependency:
            // FileJobPlugin requires `content`, so validating here would reject the very shape this
            // design makes valid.
            if (plugins is not null && plugins.TryGet(type, out var plugin) && plugin is not null
                && !WriteJobValidator.ContentComesFromDependency(type, wAction, wHasContent, deps.Count))
            {
                var validation = plugin.Validate(new JobParameters(paramsDict));
                if (!validation.IsValid)
                    throw new InvalidOperationException(
                        $"Job '{localId}' ('{type}'): {string.Join(", ", validation.Errors)}");
            }

            dag.AddJob(new Job
            {
                Id = idMap[localId],
                PlanLocalId = localId,
                GoalId = goalId,
                PluginType = type,
                DisplayName = j.GetProperty("name").GetString()!,
                Parameters = new JobParameters(paramsDict),
                DependsOn = deps,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        // D7: a non-writing role must not overwrite the file it was asked to review.
        // The read-feeds-replace shape, rejected before an LLM call is spent on a plan that cannot
        // work — the same point at which the review boundary is checked.
        // Mode read off the REGISTRY, not a flag: the remedy must name a job type the orchestrator
        // was actually shown.
        if (!EditJobValidator.TryValidate(editFacts, out var editError,
                fanOut: plugins?.TryGet("llm_agent", out _) == true))
            throw new InvalidOperationException(editError);


        return dag;
    }
}
