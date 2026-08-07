using System.Text;
using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Plugins;

namespace CxAgent.UI;

/// <summary>
/// The tool the orchestrator LLM calls when consulted mid-drive (P8): given a JobDigest of what just
/// finished, decide whether to continue, modify the plan, or declare the goal finished. Mirrors
/// DiagnoseTool's pattern — a schema built from the live PluginRegistry/RoleRegistry so a job the
/// model proposes adding is guaranteed to be one the registry can actually run, plus a defensive
/// Parse that returns null rather than guess at a malformed reply.
///
/// `jobs_to_add` reuses the SAME job shape as CreatePlanTool's `jobs` (id/name/type/params/depends_on,
/// a dependency's output arrives automatically) — a second, slightly different job schema here would be
/// one more syntax for the model to confuse with the first.
/// </summary>
public static class ConsultTool
{
    public static ToolDefinition BuildDefinition(PluginRegistry registry)
    {
        var plugins = PluginSchemaText.OrderedPlugins(registry);
        object jobProperties = JobShape(registry);

        return BuildDefinitionCore(plugins, jobProperties);
    }

    /// <summary>
    /// The job object a tool accepts — id/name/type/params/depends_on, plus `role` when a role
    /// registry is supplied. PUBLIC so PlannerToolset's `propose_jobs` uses this exact shape rather
    /// than a second, slightly different one: a planner proposing jobs and an orchestrator adding
    /// them must speak the same language, or there are two syntaxes for the model to confuse.
    /// </summary>
    public static object JobShape(PluginRegistry registry)
    {
        var plugins = PluginSchemaText.OrderedPlugins(registry);
        var typeNames = PluginSchemaText.TypeNames(plugins);
        var paramsDescription = PluginSchemaText.BuildParamsPropertyDescription(plugins) + "\n\n" + JobOutputReferenceGuidance;

        // role is added conditionally, same rule as CreatePlanTool: a registry with no RoleRegistry
        // No `role` field: roles are gone. The job shape is id/name/type/params/depends_on, the
        // same one create_plan uses -- a planner proposing jobs and an orchestrator adding them must
        // speak one language, or there are two syntaxes for the model to confuse.
        return new
        {
            id = new { type = "string", description = "Plan-local unique id for this new job. Other jobs name it in their depends_on." },
            name = new { type = "string", description = "Human-readable job name." },
            type = new { type = "string", @enum = typeNames, description = "Plugin type. See `params`'s description for each type's required/optional params." },
            @params = new { type = "object", description = paramsDescription },
            depends_on = new { type = "array", items = new { type = "string" }, description = "Ids (existing or new) this job waits for." },
        };
    }

    /// <summary>
    /// The job-type rule for THIS mode, derived from what is registered rather than from a flag —
    /// guidance and reality cannot drift when one is read off the other.
    ///
    /// <para>Single-agent mode's rule is the more useful one, and this is the turn it pays off on:
    /// the read has FINISHED, so its text is in the digest above. The edit the orchestrator could
    /// not plan at create_plan time — because it had not seen the bytes — it can plan now.</para>
    /// </summary>
    private static string ModeRule(IReadOnlyList<IJobPlugin> plugins) =>
        plugins.Any(p => p.TypeName == "llm_agent")
            ? "CHOOSING A JOB TYPE (same rule as create_plan): `file`/`shell`/`http` only when every "
              + "parameter is final literal text you have right now. Anything that must LOOK before "
              + "it acts — an edit matching a file's actual bytes, a read-then-modify — is ONE "
              + "`llm_agent` job doing it all in one context. Never split a read from an edit that "
              + "depends on it: you see digests, and a digest cannot reproduce exact bytes.\n"
            : "THIS IS WHERE YOU DO READ-THEN-EDIT WORK. There are no worker jobs. A read job that "
              + "has finished put its text in the digest above — so an edit you could not plan "
              + "before, because you had not seen the bytes, you can plan NOW with `modify` and "
              + "`jobs_to_add`. Copy the pattern from the text above, exactly as it appears there, "
              + "rather than from memory of what the file probably says.\n";

    private static ToolDefinition BuildDefinitionCore(
        IReadOnlyList<IJobPlugin> plugins, object jobProperties)
    {
        var typeNames = PluginSchemaText.TypeNames(plugins);
        var schema = new
        {
            type = "object",
            properties = new
            {
                action = new
                {
                    type = "string",
                    @enum = new[] { "continue", "modify", "finish_goal" },
                    description =
                        "continue: the results are as expected — do nothing. THIS IS THE COMMON CASE and "
                        + "costs nothing: no rationale or summary required. Only reach for modify or "
                        + "finish_goal when something in the digest actually calls for a change.\n"
                        + "modify: add jobs, remove not-yet-started jobs, re-parameterise a job, or cancel "
                        + "one still running. A digest showing PROPOSED JOBS is a planner asking you "
                        + "to run them — adopt the ones you want here, with `jobs_to_add`.\n"
                        + "\n"
                        + ModeRule(plugins)
                        + "finish_goal: EVERY part of what the user asked for has actually been done "
                        + "— stop the drive and report `summary` to the user.\n"
                        + "\n"
                        + "Before choosing finish_goal, re-read the user's original request and check "
                        + "each thing it asked for against the jobs listed above. A request to "
                        + "\"review X and write the result to Y\" is TWO deliverables: if no job "
                        + "wrote Y, the goal is NOT met — use `modify` to add the missing job. "
                        + "Answering in prose is not a substitute for a file the user asked for.",
                },
                jobs_to_add = new
                {
                    type = "array",
                    description = "Only for modify. New jobs to add to the running plan — SAME shape as "
                        + "create_plan's `jobs`: id/name/type/params/depends_on. A new job receives the output of "
                        + "every job in its depends_on automatically. "
                        + "Worked example — the digest showed a review job (id \"review\") flagged a "
                        + "problem, so a fix-up job is added depending on it:\n"
                        + "  { \"action\": \"modify\", \"jobs_to_add\": [\n"
                        + "    { \"id\": \"fix\", \"name\": \"Apply the fix\", \"type\": \"" + FirstTypeName(typeNames) + "\",\n"
                        + "      \"depends_on\": [\"review\"],\n"
                        + "      \"params\": " + WorkedExampleParams(plugins) + " }\n"
                        + "  ] }",
                    items = new
                    {
                        type = "object",
                        properties = jobProperties,
                        required = new[] { "id", "name", "type", "params" },
                    },
                },
                job_ids_to_remove = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Only for modify. Ids of not-yet-started jobs to drop from the plan.",
                },
                parameter_changes = new
                {
                    type = "object",
                    description = "Only for modify. Map of job id -> the FULL replacement params object for it.",
                },
                cancel_job_ids = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Only for modify. Ids of currently-running jobs to cancel.",
                },
                summary = new
                {
                    type = "string",
                    description = "Only for finish_goal. One-line summary of the outcome — shown to the user.",
                },
                rationale = new
                {
                    type = "string",
                    description = "Why — shown to the user. Not required for continue.",
                },
            },
            required = new[] { "action" },
        };

        var schemaJson = JsonSerializer.SerializeToElement(schema);

        return new ToolDefinition("consult", BuildDescription(), schemaJson);
    }

    /// <summary>Parses the model's consult reply. Returns null on anything unparseable — an unknown
    /// action, a malformed payload, or a missing required field — rather than guessing: inferring
    /// "modify" from a half-parsed payload could add jobs the user never authorised. The caller
    /// reports and stops (mirrors JobDiagnoser.TryMapToDiagnosis).</summary>
    public static ConsultDecision? Parse(ToolCall call)
    {
        try
        {
            var args = call.Arguments;

            var actionText = args.TryGetProperty("action", out var a) ? a.GetString() : null;
            if (!TryMapAction(actionText, out var action))
                return null;

            var summary = args.TryGetProperty("summary", out var s) ? s.GetString() : null;
            var rationale = args.TryGetProperty("rationale", out var r) ? r.GetString() : null;

            var cancelIds = ReadStringArray(args, "cancel_job_ids");

            ConsultModification? modification = null;
            if (action == ConsultAction.Modify)
            {
                JsonElement? jobsToAdd = args.TryGetProperty("jobs_to_add", out var j) && j.ValueKind == JsonValueKind.Array
                    ? j
                    : null;

                var parameterChanges = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (args.TryGetProperty("parameter_changes", out var pc) && pc.ValueKind == JsonValueKind.Object)
                    foreach (var prop in pc.EnumerateObject())
                        parameterChanges[prop.Name] = prop.Value.Clone();

                modification = new ConsultModification(jobsToAdd, parameterChanges);
            }

            return new ConsultDecision(action, modification, cancelIds, summary, rationale);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryMapAction(string? text, out ConsultAction action)
    {
        switch (text)
        {
            case "continue": action = ConsultAction.Continue; return true;
            case "modify": action = ConsultAction.Modify; return true;
            case "finish_goal": action = ConsultAction.FinishGoal; return true;
            default: action = default; return false;
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement args, string property)
    {
        if (!args.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
                result.Add(item.GetString()!);
        return result;
    }

    private static string FirstTypeName(string[] typeNames) =>
        typeNames.Length > 0 ? typeNames[0] : "shell";

    /// <summary>
    /// How data moves between jobs, now that there is no reference syntax.
    ///
    /// <para>The old {{job_id.key}} form asked the model to state one fact twice — once in
    /// depends_on, once as a name inside a param — and every observed failure was the two
    /// disagreeing: a literal `key` token copied through, a name belonging to no job, a reference
    /// whose dependency was never declared. Research across seven agent frameworks found none that
    /// lets a plan-generating LLM author cross-step references; the closest match to the old design
    /// was Airflow's classic XCom, which Airflow itself abandoned.</para>
    /// </summary>
    private const string JobOutputReferenceGuidance =
        "PASSING DATA BETWEEN JOBS. A job automatically receives the output of every job in its "
        + "`depends_on`. You never write a reference to it — declaring the dependency IS the request "
        + "for its output.\n"
        + "\n"
        + "  - A job that takes a prompt receives each dependency's output ahead of it.\n"
        + "  - A `file` write job with ONE dependency and no `content` writes that dependency's "
        + "output.\n"
        + "  - Every other parameter is literal text, exactly as you write it.\n"
        + "\n"
        + "A write job depending on SEVERAL jobs with no `content` is rejected — there is no way to "
        + "tell which output you meant. Combine them into one job's output first, then have the "
        + "write depend on that single job.";

    private static string BuildDescription() =>
        "Called after a job finishes so you can decide what happens next. `continue` when the results "
        + "are as expected — this is the common case and costs nothing. `modify` to add/remove/"
        + "re-parameterise jobs or cancel one still running. `finish_goal` when the goal is met, with "
        + "a summary the user will read.";

    private static string WorkedExampleParams(IReadOnlyList<IJobPlugin> plugins)
    {
        if (plugins.Count == 0)
            return "{ }";

        var schema = plugins[0].GetSchema();
        var sb = new StringBuilder("{ ");
        var first = true;
        foreach (var p in schema.Params.Where(p => p.Required))
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append($"\"{p.Name}\": \"...\"");
        }
        if (first) sb.Append("");
        sb.Append(" }");
        return sb.ToString();
    }
}
