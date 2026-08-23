using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Jobs;

/// <summary>
/// The consumer's injected tools, as one dispatchable set.
///
/// <para>ORDERED BEHIND THE BUILT-INS ON PURPOSE. This is consulted only after every built-in has
/// declined, so a consumer cannot shadow <c>read_file</c> by naming a tool <c>read_file</c> — which
/// would be a silent hijack of a name the model already trusts, and the kind of thing that reads as
/// a model bug rather than a configuration one.</para>
///
/// <para>AND BEFORE <see cref="ToolBindings.InvokeAsync"/>, which is not a preference. That method
/// answers "no such tool" rather than returning null, so it TERMINATES the <c>??</c> chain in
/// Agent.RunAsync: anything placed after it is unreachable code that looks correct.</para>
/// </summary>
public sealed class AgentToolset
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _byName;

    public AgentToolset(IReadOnlyList<IAgentTool> tools)
    {
        // LAST ONE WINS on a duplicate name rather than throwing. A consumer that registers two
        // tools with one name has made a mistake, but taking down a session at construction is a
        // worse answer than running the one they most recently asked for.
        //
        // AN INDEXER, NOT ToDictionary. This said exactly the above while calling ToDictionary,
        // which throws ArgumentException on a duplicate key — a comment asserting the opposite of
        // its own code, found by writing the documentation for this interface rather than by any
        // test. The failure was a wiring-time crash on a plausible consumer mistake.
        var byName = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (var tool in tools) byName[tool.Definition.Name] = tool;
        _byName = byName;
    }

    /// <summary>Whether an injected tool owns this name. Used to label the transcript row, which
    /// must happen BEFORE the call runs — so it cannot be answered by dispatching.</summary>
    public bool Knows(string toolName) => _byName.ContainsKey(toolName);

    public IReadOnlyList<ToolDefinition> Definitions() =>
        _byName.Values.Select(t => t.Definition).ToList();

    /// <summary>
    /// Null when no injected tool owns this name, so the caller's <c>??</c> chain continues to the
    /// built-ins' terminator.
    ///
    /// <para>RETURNS THE TOOL'S OWN RESULT ALONGSIDE THE TEXT, rather than a bare string. A string
    /// alone forces the caller to rebuild job.Result from it, which discards a tool's output
    /// dictionary and leaves the row's display to reach Agent through a side-channel property. Letting
    /// the object survive the dispatch removes the need for one — see <see cref="ToolOutcome"/>.</para>
    /// </summary>
    public async Task<ToolOutcome?> TryInvokeAsync(ToolCall call, IJobContext context, CancellationToken ct)
    {
        // RESET UP FRONT: a name that doesn't match falls through without touching
        // context.DecidedBy at all, and a prior call's leftover value must not survive to be read
        // as this one's verdict.
        context.DecidedBy = null;
        if (!_byName.TryGetValue(call.Name, out var tool)) return null;

        var result = await tool.ExecuteAsync(JobParametersFrom(call), context, ct);

        // THE ERROR BECOMES THE RESULT, never an exception. Agent.RunAsync appends the assistant
        // message carrying the tool calls BEFORE running them, so an exception unwinding the loop
        // leaves tool calls with no matching results — an orphan the provider rejects with a 400
        // that no recovery path matches. ToolBindings.InvokeAsync holds the same contract.
        if (!result.Success)
            return new ToolOutcome(result.ErrorMessage ?? "error: the tool failed without saying why", result);

        var content = result.Output.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";

        // TWO AUDIENCES. "content" is what the TRANSCRIPT shows; "summary", when present, is what
        // the MODEL is told instead. A tool whose content is FOR A PERSON TO LOOK AT — rendered
        // markup, a table, anything whose value is in how it is displayed — is what forces the
        // split: hand that to the model verbatim and it spends a turn describing something already
        // on the user's screen. The split needs no smuggling: the Text member is the model's copy
        // and the row reads the tool's own Output for what to display.
        if (result.Output.TryGetValue("summary", out var summary) && summary?.ToString() is { Length: > 0 } s)
            return new ToolOutcome(s, result);

        return new ToolOutcome(content, result);
    }

    /// <summary>
    /// A tool call's arguments as executor parameters.
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
