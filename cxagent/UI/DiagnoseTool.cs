using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;

namespace CxAgent.UI;

/// <summary>
/// The tool the orchestrator LLM calls during diagnosis: suggest_recovery. Given a failed job's
/// context, the model proposes one of five recovery actions (mirroring RecoveryAction exactly — see
/// DiagnoseToolTests.ActionEnum_MatchesRecoveryActionMembers_Exactly) instead of returning prose that
/// JobDiagnoser would have to parse.
///
/// `jobs_to_insert[].type` and `params` reuse PluginSchemaText, the same generator CreatePlanTool
/// uses, so a job the model proposes inserting is guaranteed to be one the registry can actually run —
/// the same lesson that moved CreatePlanTool off a hand-written schema.
/// </summary>
public static class DiagnoseTool
{
    /// <summary>
    /// The resolver-LESS baseline definition: built from <see cref="PluginRegistry.CreateWithBuiltins()"/>,
    /// so it describes a registry with NO <c>llm_agent</c>. Do NOT use it for dispatch — the live path
    /// calls <see cref="BuildDefinition"/> with the runner's own registry (JobDiagnoser.cs), which is
    /// the only one whose advertised job types match what can actually run.
    /// </summary>
    public static ToolDefinition Definition { get; } = BuildDefinition(PluginRegistry.CreateWithBuiltins());

    public static ToolDefinition BuildDefinition(PluginRegistry registry)
    {
        var plugins = PluginSchemaText.OrderedPlugins(registry);
        var typeNames = PluginSchemaText.TypeNames(plugins);
        var paramsDescription = PluginSchemaText.BuildParamsPropertyDescription(plugins);

        var schema = new
        {
            type = "object",
            properties = new
            {
                cause = new { type = "string", description = "One-line human-readable cause of the failure." },
                action = new
                {
                    type = "string",
                    @enum = new[] { "retry", "modify_and_retry", "insert_before", "skip", "ask_user" },
                    description = "retry: re-run unchanged (transient). modify_and_retry: change params, then re-run. "
                                + "insert_before: add jobs before this one, then re-run it. skip: give up on this job "
                                + "and let dependents proceed. ask_user: you cannot decide; explain in rationale.",
                },
                rationale = new { type = "string", description = "Why this action — shown to the user, who confirms it." },
                parameter_changes = new
                {
                    type = "object",
                    description = "Only for modify_and_retry. Map of job id -> the FULL replacement params object for it.",
                },
                jobs_to_insert = new
                {
                    type = "array",
                    description = "Only for insert_before. Jobs to run before the failed one, in order.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            id = new { type = "string", description = "Plan-local unique id." },
                            name = new { type = "string", description = "Human-readable job name." },
                            type = new { type = "string", @enum = typeNames, description = "Plugin type." },
                            @params = new { type = "object", description = paramsDescription },
                        },
                        required = new[] { "id", "name", "type", "params" },
                    },
                },
            },
            required = new[] { "cause", "action", "rationale" },
        };

        var schemaJson = JsonSerializer.SerializeToElement(schema);

        return new ToolDefinition("suggest_recovery", BuildDescription(), schemaJson);
    }

    private static string BuildDescription() =>
        "A job in the running plan failed. Diagnose the cause from the context you were given and "
        + "propose exactly one recovery action. Call this exactly once with your suggestion; the user "
        + "confirms it before it is applied.";
}
