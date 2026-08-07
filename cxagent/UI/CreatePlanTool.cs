using System.Text;
using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;

namespace CxAgent.UI;

/// <summary>
/// The single tool the orchestrator LLM may call: create_plan. Its schema tells a real provider the
/// shape of a valid plan (a jobs array of id/name/type/params/depends_on). P1's Orchestrator passes
/// tools:null today (a documented TODO); the UI's GoalRunner passes this so a real provider knows
/// create_plan is callable.
///
/// The `type` enum and per-plugin `params` documentation are generated from a PluginRegistry rather
/// than hand-written, so every registered plugin's real JobSchema reaches the model instead of the
/// bare "Plugin-specific parameters." placeholder that let a live drive emit a job missing a required
/// param. Per-plugin params are documented as a reference table in the tool description (not a
/// JSON-Schema `oneOf` keyed on `type`) because not every OpenAI-compatible provider — including the
/// local llama.cpp servers cxagent targets — handles `oneOf` reliably; a plain-text reference is more
/// portable and works the same everywhere.
/// </summary>
public static class CreatePlanTool
{
    // NOTE: there is deliberately no cached `Definition` property here (DiagnoseTool still has one).
    // It was a static snapshot built from the NO-ARG CreateWithBuiltins(), so it described a registry
    // with no llm_agent and no roles — and it had no production caller: GoalRunner.cs builds the real
    // schema per call via BuildDefinition(_plugins, _roles), because the roles it must advertise change
    // within a session (an F7 rebinding re-wires the runner). A public static that must never be used
    // at runtime is a trap: the next task to need a create_plan definition would reach for the
    // convenient property and silently ship a schema advertising neither llm_agent nor any role. Build
    // it from the live registry instead.

    /// <summary>
    /// Builds the create_plan schema. <paramref name="roles"/> is optional: a null registry (e.g. a
    /// PluginRegistry built without a RoleResolver, so llm_agent isn't even registered) omits the
    /// `role` field entirely rather than advertising a job field the orchestrator has no way to act
    /// on. When supplied, the same two-audience rule as the worker's system message applies here:
    /// only RoleDefinition.Description (3rd person, "what this role is for") reaches the schema —
    /// never SystemPrompt (2nd person, worker-only behavioural instructions).
    /// </summary>
    public static ToolDefinition BuildDefinition(PluginRegistry registry)
    {
        var plugins = PluginSchemaText.OrderedPlugins(registry);
        var typeNames = PluginSchemaText.TypeNames(plugins);

        // Appended here, NOT folded into PluginSchemaText.BuildParamsPropertyDescription — that
        // helper is shared with DiagnoseTool's jobs_to_insert schema, which has no depends_on field at
        // all (an inserted job runs unconditionally before the failed one), so guidance about
        // dependencies supplying data would be actively wrong there.
        var paramsDescription = PluginSchemaText.BuildParamsPropertyDescription(plugins) + "\n\n" + JobOutputReferenceGuidance;

        // No role field: roles are gone.
        // without a RoleResolver, which never registers llm_agent) doesn't advertise a field the
        // orchestrator has nothing to act on.
        object jobProperties = new
            {
                id = new { type = "string", description = "Plan-local unique id you choose for this job. "
                    + "Other jobs name it in their `depends_on`. Copy it verbatim — do not "
                    + "paraphrase it (an id of \"read1\" is not \"read_1\" or \"readFile\"), or "
                    + "the dependency cannot be resolved." },
                name = new { type = "string", description = "Human-readable job name." },
                type = new
                {
                    type = "string",
                    @enum = typeNames,
                    description = "Plugin type. See `params`'s description for each type's required/optional params.",
                },
                @params = new { type = "object", description = paramsDescription },
                depends_on = new { type = "array", items = new { type = "string" }, description = "Plan-local ids this job waits for." },
            };

        var schema = new
        {
            type = "object",
            properties = new
            {
                summary = new { type = "string", description = "One-line summary of the plan." },
                jobs = new
                {
                    type = "array",
                    description = "The typed jobs to execute, in dependency order.",
                    items = new
                    {
                        type = "object",
                        properties = jobProperties,
                        required = new[] { "id", "name", "type", "params" },
                    },
                },
            },
            required = new[] { "jobs" },
        };

        var schemaJson = JsonSerializer.SerializeToElement(schema);

        return new ToolDefinition("create_plan", BuildDescription(plugins), schemaJson);
    }

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


    private static string BuildDescription(IReadOnlyList<CxAgent.Core.Plugins.IJobPlugin> plugins)
    {
        var sb = new StringBuilder();
        sb.Append("Decompose the user's goal into a DAG of typed jobs. Call this exactly once with the full plan. ");
        sb.Append("Each job's `type` must be one of the plugin types below, and its `params` must match that ");
        sb.Append("plugin's parameter reference exactly.\n\n");
        // YOU are the planner. A live drive worked out the exact `sed -i` commands for six files,
        // wrote them into its closing message, and called finish_goal -- nothing was edited. The
        // plan was not missing; a PLACE to put it was, so it went into prose. Another drive
        // delegated to a `planner` role and got a methodology document as the deliverable.
        sb.Append("YOU are the planner — there is no one to hand this to. If you can describe a step, ");
        sb.Append("emit it as a JOB. Writing out the commands or edits you would make, instead of ");
        sb.Append("planning jobs that make them, means nothing happens: text in a message is not work. ");
        sb.Append("A goal that asks you to CHANGE something needs a job that changes it.\n\n");
        // TWO MODES, TWO RULES, chosen from what is actually REGISTERED rather than from a flag —
        // guidance and reality cannot drift if one is derived from the other. In single-agent mode
        // llm_agent is not registered at all, so telling the orchestrator to "plan one llm_agent
        // job" would name a type absent from the enum it was just given.
        if (plugins.Any(p => p.TypeName == "llm_agent"))
        {
            sb.Append("CHOOSING A JOB TYPE. Use `file`/`shell`/`http` only for steps whose every ");
            sb.Append("parameter you can write as final literal text right now — paths, globs, commands, ");
            sb.Append("text the user gave you verbatim. The moment a step must LOOK at something before ");
            sb.Append("acting on it — an edit that must match a file's actual bytes, a change decided by ");
            sb.Append("reading code, any read-then-modify — plan ONE `llm_agent` job whose worker does the ");
            sb.Append("whole read-decide-edit in a single context. Never split a read from an edit that ");
            sb.Append("depends on it: job outputs reach you as DIGESTS, and a digest cannot reproduce ");
            sb.Append("exact bytes.\n\n");
        }
        else
        {
            sb.Append("YOU DO THE WORK YOURSELF. There are no worker jobs — the job types below are ");
            sb.Append("the only ones that exist. Plan `file`/`shell`/`http` jobs whose parameters you ");
            sb.Append("can write as final literal text: paths, globs, commands, content.\n\n");
            sb.Append("A step that must LOOK at something before acting on it — an edit that has to ");
            sb.Append("match a file's exact bytes — is one you do across TURNS, not one you delegate. ");
            sb.Append("Plan the `file read` job now; when its output comes back you will be consulted, ");
            sb.Append("and you can plan the edit THEN, with the file's real text in front of you. Do ");
            sb.Append("not plan a `file replace` whose pattern you have not yet seen: job outputs ");
            sb.Append("reach you as digests when the job is FINISHED, so guessing the bytes now fails ");
            sb.Append("before it runs.\n\n");
        }
        sb.Append("Plugin parameter reference:\n");
        sb.Append(PluginSchemaText.BuildParamsPropertyDescription(plugins));
        return sb.ToString();
    }
}
