using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Jobs;

/// <summary>
/// The consumer's injected tools, as one dispatchable set.
///
/// <para>ORDERED AHEAD OF THE BUILT-INS, AND THAT MEANS AN INJECTED NAME WINS. This link is
/// consulted BEFORE <see cref="ToolBindings.InvokeAsync"/>, which is where every built-in is
/// dispatched — so a consumer injecting a tool called <c>read_file</c> shadows the built-in rather
/// than being declined by it. Ordering is not a defence here and must not be read as one: the only
/// thing standing between a consumer and a hijacked name is the consumer not choosing it.</para>
///
/// <para>AND BEFORE <see cref="ToolBindings.InvokeAsync"/>, which is not a preference. That method
/// answers "no such tool" rather than returning null, so it TERMINATES the <c>??</c> chain in
/// Agent.RunAsync: anything placed after it is unreachable code that looks correct.</para>
/// </summary>
public sealed class AgentToolset
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _byName;

    /// <param name="tools">The consumer's tools, in registration order.</param>
    /// <param name="strict">
    /// Whether a duplicate name refuses construction instead of withdrawing the tools that share it.
    ///
    /// <para>FALSE BY DEFAULT, because withdrawing is recoverable and reports itself: the rest of a
    /// well-formed set still runs, and <see cref="Withdrawn"/> names what collided. Taking down a
    /// session over one mis-wired tool is a heavier answer than the mistake deserves.</para>
    ///
    /// <para>TRUE FOR AN EMBEDDER WHO WOULD RATHER NOT SHIP DEGRADED. A product assembling tools
    /// from configuration may prefer to fail at wiring time, where a developer sees it, over
    /// starting with two tools quietly missing — and this library cannot know which of those a
    /// consumer is. Both answers are honest; neither is silent. The default picks the one that
    /// keeps a session running.</para>
    /// </param>
    public AgentToolset(IReadOnlyList<IAgentTool> tools, bool strict = false)
    {
        // A DUPLICATE NAME DISABLES BOTH TOOLS AND SAYS SO. Neither is offered, and the reason is
        // reported rather than left to be inferred.
        //
        // LAST-WINS IS THE DANGEROUS ANSWER, not the kind one. It depends on registration ORDER,
        // which an embedder assembling tools from configuration or a container does not control and
        // cannot see — so the tool that runs is decided by something invisible, and the one that
        // does not run fails silently. "A fair guess at their intent" is a guess: with two tools
        // claiming one name there is no evidence which was meant, and running either is a coin flip
        // the embedder never consented to.
        //
        // WITHDRAWING BOTH COSTS ONLY WHAT THE EMBEDDER REGISTERED. Nothing else advertised these
        // names — unlike a built-in, whose name the model has already been told it has, which is why
        // that case throws instead of withdrawing.
        //
        // NOT A THROW, because two tools with one name is a recoverable state: the rest of the set
        // is well-formed and the session runs without them. A built-in collision is not recoverable
        // — the injected tool would win a name the model trusts — so it refuses construction.
        // A BUILT-IN'S NAME IS NOT REFUSED HERE, because whether it IS a built-in's name depends on
        // something this constructor cannot see. A user who disables `write_file` through tool
        // selection has freed that name: nothing offers it, so nothing is shadowed, and refusing an
        // injected tool for colliding with a tool that is not there would deny the escape hatch the
        // selection grammar exists to provide.
        //
        // AND THE ANSWER MOVES. Selection composes per turn, so a built-in withheld for one request
        // is offered in the next — a decision taken once at construction would be wrong for every
        // turn after the one it was taken in. The check therefore belongs where the offered set is
        // assembled, against the composed selection for THAT request. See Agent's dispatch.
        var duplicates = tools
            .GroupBy(t => t.Definition.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (strict && duplicates.Count > 0)
            throw new ArgumentException(
                $"tool name{(duplicates.Count == 1 ? "" : "s")} "
                + $"{string.Join(", ", duplicates.Select(n => $"'{n}'"))} "
                + "registered more than once. Strict mode refuses rather than withdrawing them.",
                nameof(tools));

        Withdrawn = duplicates;

        var byName = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
            if (!duplicates.Contains(tool.Definition.Name, StringComparer.Ordinal))
                byName[tool.Definition.Name] = tool;

        _byName = byName;
    }

    /// <summary>Whether an injected tool owns this name. Used to label the transcript row, which
    /// must happen BEFORE the call runs — so it cannot be answered by dispatching.</summary>
    public bool Knows(string toolName) => _byName.ContainsKey(toolName);

    /// <summary>
    /// Names registered more than once, and therefore offered not at all.
    ///
    /// <para>REPORTED RATHER THAN LOGGED HERE, because this type has nowhere to say it: a consumer
    /// wires it up and a session renders. Exposing the list lets whoever has an output surface tell
    /// the embedder which names collided — the difference between a tool that is missing and a tool
    /// that is missing FOR A STATED REASON.</para>
    /// </summary>
    public IReadOnlyList<string> Withdrawn { get; }

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
