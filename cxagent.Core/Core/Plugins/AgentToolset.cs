using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Plugins;

/// <summary>
/// The consumer's injected tools, as one dispatchable set.
///
/// <para>ORDERED BEHIND THE BUILT-INS ON PURPOSE. This is consulted only after every built-in has
/// declined, so a consumer cannot shadow <c>read_file</c> by naming a tool <c>read_file</c> — which
/// would be a silent hijack of a name the model already trusts, and the kind of thing that reads as
/// a model bug rather than a configuration one.</para>
///
/// <para>AND BEFORE <see cref="WorkerToolset.InvokeAsync"/>, which is not a preference. That method
/// answers "no such tool" rather than returning null, so it TERMINATES the <c>??</c> chain in
/// Agent.RunAsync: anything placed after it is unreachable code that looks correct.</para>
/// </summary>
public sealed class AgentToolset
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _byName;

    public AgentToolset(IReadOnlyList<IAgentTool> tools) =>
        // LAST ONE WINS on a duplicate name rather than throwing. A consumer that registers two
        // tools with one name has made a mistake, but taking down a session at construction is a
        // worse answer than running the one they most recently asked for.
        _byName = tools.ToDictionary(t => t.Definition.Name, t => t, StringComparer.Ordinal);

    public IReadOnlyList<ToolDefinition> Definitions() =>
        _byName.Values.Select(t => t.Definition).ToList();

    /// <summary>Null when no injected tool owns this name, so the caller's <c>??</c> chain
    /// continues to the built-ins' terminator.</summary>
    public async Task<string?> TryInvokeAsync(ToolCall call, IJobContext context, CancellationToken ct)
    {
        if (!_byName.TryGetValue(call.Name, out var tool)) return null;

        var result = await tool.ExecuteAsync(JobParametersFrom(call), context, ct);

        // THE ERROR BECOMES THE RESULT, never an exception. Agent.RunAsync appends the assistant
        // message carrying the tool calls BEFORE running them, so an exception unwinding the loop
        // leaves tool calls with no matching results — an orphan the provider rejects with a 400
        // that no recovery path matches. WorkerToolset.InvokeAsync holds the same contract.
        if (!result.Success)
            return result.ErrorMessage ?? "error: the tool failed without saying why";

        return result.Output.TryGetValue("content", out var content)
            ? content?.ToString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    /// A tool call's arguments as plugin parameters.
    ///
    /// <para>Values arrive as <c>JsonElement</c> and STAY that way — JobParameters.Get converts on
    /// read, which is the same shape a persisted job's parameters have. Unwrapping to CLR types
    /// here would make an injected tool's arguments behave differently from every other tool's.</para>
    /// </summary>
    private static JobParameters JobParametersFrom(ToolCall call)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (call.Arguments.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var property in call.Arguments.EnumerateObject())
                values[property.Name] = property.Value;

        return new JobParameters(values);
    }
}
