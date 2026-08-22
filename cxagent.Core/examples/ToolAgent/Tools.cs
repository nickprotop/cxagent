using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Jobs;

namespace ToolAgent;

/// <summary>
/// Three tools, chosen to cover the three things <see cref="IAgentTool.Gate"/> can say. The
/// difference between them is ONE LINE each, and that line is the whole permission design.
/// </summary>
internal static class Tools
{
    private static JsonElement Schema(object shape) => JsonSerializer.SerializeToElement(shape);

    /// <summary>
    /// UNGATED: <c>Gate</c> returns null, so no human is ever asked.
    ///
    /// <para>Correct here because the tool cannot touch anything — it adds two numbers. Returning
    /// null is a claim about the tool, not a convenience: anything that reads a file, spends money
    /// or talks to a network has something to ask about.</para>
    /// </summary>
    public sealed class Calc : IAgentTool
    {
        public ToolDefinition Definition { get; } = new("calc", "Add two numbers.", Schema(new
        {
            type = "object",
            properties = new { a = new { type = "number" }, b = new { type = "number" } },
            required = new[] { "a", "b" },
        }));

        public PermissionRequest? Gate(JobParameters call) => null;

        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct) =>
            Task.FromResult(new JobResult
            {
                Success = true,
                Output = { ["content"] = (call.Get("a", 0d) + call.Get("b", 0d)).ToString() },
            });
    }

    /// <summary>
    /// ASKED ONCE PER ENVIRONMENT: the AlwaysRule names the environment, so "always" answered for
    /// <c>dev</c> does not answer for <c>prod</c>.
    ///
    /// <para>THE RULE IS THE GRANULARITY. A rule of "deploy*" would be asked once ever and would
    /// hand over production on the strength of a yes about a development box. The scope of what the
    /// user agreed to is decided HERE, by what this method returns — not by the gate, which can only
    /// honour what it was given.</para>
    /// </summary>
    public sealed class Deploy : IAgentTool
    {
        public ToolDefinition Definition { get; } = new("deploy", "Deploy to an environment.", Schema(new
        {
            type = "object",
            properties = new { env = new { type = "string" } },
            required = new[] { "env" },
        }));

        public PermissionRequest? Gate(JobParameters call)
        {
            var env = call.Get("env", "");
            return new PermissionRequest(PermissionKind.Tool, $"deploy to {env}", AlwaysRule: $"deploy env={env}");
        }

        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct) =>
            Task.FromResult(new JobResult
            {
                Success = true,
                Output = { ["content"] = $"deployed to {call.Get("env", "")}" },
            });
    }

    /// <summary>
    /// ASKED EVERY TIME: a null AlwaysRule says this call cannot be truthfully generalised, so no
    /// "always" button is offered and no stored rule can ever match it.
    ///
    /// <para>Right when the arguments are free text: "notify #eng" and "notify @ceo" differ in a way
    /// no rule string captures. Offering "always" here would let one yes cover every future message
    /// to anyone.</para>
    /// </summary>
    public sealed class Notify : IAgentTool
    {
        public ToolDefinition Definition { get; } = new("notify", "Send someone a message.", Schema(new
        {
            type = "object",
            properties = new { to = new { type = "string" }, text = new { type = "string" } },
            required = new[] { "to", "text" },
        }));

        public PermissionRequest? Gate(JobParameters call) =>
            new(PermissionKind.Tool, $"notify {call.Get("to", "")}: {call.Get("text", "")}", AlwaysRule: null);

        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct) =>
            Task.FromResult(new JobResult
            {
                Success = true,
                Output = { ["content"] = $"sent to {call.Get("to", "")}" },
            });
    }
}
