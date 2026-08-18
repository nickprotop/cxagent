using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;

namespace DocCheck;

// VERBATIM FROM docs/tools.md, AND THAT IS THE POINT. A documented interface rots quietly: the
// sample keeps saying `Gate` returns a PermissionRequest long after it stopped, and nobody notices
// until a consumer pastes it. Compiling it here means a signature change breaks the BUILD rather
// than the reader.
//
// Writing that doc already caught one real bug this way — AgentToolset's constructor comment
// promised "last one wins on a duplicate name" while calling ToDictionary, which throws.
//
// Nothing here is executed. If a sample needs behaviour asserted, it belongs in a real test file;
// this one only has to compile.
public sealed class DeployTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "deploy",
        "Deploy the current build to an environment.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { env = new { type = "string" } },
            required = new[] { "env" },
        }));

    public PermissionRequest? Gate(JobParameters call)
    {
        var env = call.Get("env", "");
        return new PermissionRequest(PermissionKind.Tool, $"deploy to {env}",
            AlwaysRule: $"deploy env={env}");
    }

    public async Task<JobResult> ExecuteAsync(
        JobParameters call, IJobContext context, CancellationToken ct)
    {
        var env = call.Get("env", "");

        try
        {
            await Task.Delay(1, ct);
            return new JobResult { Success = true, Output = { ["content"] = $"deployed to {env}" } };
        }
        catch (Exception ex)
        {
            return new JobResult { Success = false, ExitCode = -1, ErrorMessage = ex.Message };
        }
    }
}

public sealed class ScreenTool : IAgentTool
{
    public bool OfferToSubAgents => false;
    public ToolDefinition Definition { get; } = new("draws", "x",
        JsonSerializer.SerializeToElement(new { type = "object" }));
    public PermissionRequest? Gate(JobParameters call) => null;
    public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct) =>
        Task.FromResult(new JobResult
        {
            Success = true,
            Output =
            {
                ["content"] = "renderedMarkup",
                ["summary"] = "4 rows, shown above",
            },
        });
}
