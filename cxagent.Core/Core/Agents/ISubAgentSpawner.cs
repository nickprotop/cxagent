using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Agents;

/// <summary>
/// Runs a sub-agent on behalf of a tool call, or declines a name it does not own.
///
/// <para>CONSULTED BEFORE <see cref="Plugins.WorkerToolset"/>, exactly as the MCP branch already is,
/// and returning null for a name it does not own — the same contract
/// <c>McpToolset.TryInvokeAsync</c> holds, so the dispatch site gains one more <c>??</c> rather than a
/// new shape.</para>
///
/// <para>NOT A <see cref="Llm.WorkerTool"/> ENUM MEMBER, which is the obvious alternative and is
/// wrong. <c>Agent.AllTools</c> is <c>Enum.GetValues&lt;WorkerTool&gt;()</c>, so an enum member is
/// offered to EVERY agent — including children, which would make "no sub-agents of sub-agents" a rule
/// the child is asked to obey rather than a capability it lacks. A child is constructed without a
/// spawner and therefore cannot nest, whatever it is told.</para>
/// </summary>
public interface ISubAgentSpawner
{
    /// <summary>The tool name this spawner answers to, so the dispatch site can advertise it.</summary>
    string ToolName { get; }

    /// <summary>
    /// Points future children at a different default model, after the session switched.
    ///
    /// <para>ON THE INTERFACE because the host holds a spawner, not a factory, and a swap that
    /// stopped at the host left every child on the model the session started with — while the switch
    /// notice promised the opposite.</para>
    /// </summary>
    void SwapDefaultProvider(ILlmProvider provider, int? contextWindow, string? instanceName);

    /// <summary>The definition the model sees. Hand-built: a spawn tool has no plugin and no
    /// <c>JobSchema</c>, so <c>WorkerToolset.BuildDefinition</c>'s drift guard has nothing to guard
    /// against — <c>McpToolset.Definitions()</c> constructs its own the same way.</summary>
    ToolDefinition Definition { get; }

    /// <summary>
    /// Runs the child and returns the envelope, or null if <paramref name="call"/> is not this
    /// spawner's tool.
    /// </summary>
    /// <param name="onChild">
    /// Called with the child the moment it is built, BEFORE it runs — the seam telemetry uses to
    /// attach to a child's events and to associate its id with the row already on screen.
    /// </param>
    /// <param name="parentAgentId">
    /// The spawning agent's id, so the child's logs nest under it rather than beside it. Optional
    /// because a spawner that keeps no logs does not need it.
    /// </param>
    /// <param name="turnTools">
    /// The selection the PARENT's current request is running under, so a child inherits it.
    ///
    /// <para>RIDES THE CALL rather than living on SubAgentRuntime, which is built once when the
    /// session is wired and therefore cannot carry a per-request value. A turn narrowed to
    /// read-only that then spawns a writing child has not narrowed anything.</para>
    /// </param>
    /// <param name="call">The spawn call the model issued.</param>
    /// <param name="ct">Cancels the child mid-run.</param>
    Task<string?> TryInvokeAsync(ToolCall call, Action<SubAgent>? onChild, CancellationToken ct,
        string? parentAgentId = null, Plugins.ToolSelection? turnTools = null);
}

/// <summary>
/// What the parent's model receives back from a spawn.
///
/// <para>AN ENVELOPE FROM STEP 1, NOT A BARE STRING (D13). An id retrofitted later would have to be
/// threaded through every surface that had already been built against a string; adding it now costs a
/// field. And <c>state</c> is the part that cannot be deferred at all: without it a capped run — a
/// salvage summary of unfinished work — is indistinguishable from a finished answer, and the parent
/// acts on it as though the work were done.</para>
/// </summary>
public static class SubAgentEnvelope
{
    /// <summary>
    /// Renders the envelope the parent's model reads.
    ///
    /// <para>XML-ISH RATHER THAN JSON, following opencode's <c>&lt;task id=… state=…&gt;</c>. The text
    /// inside is the child's prose, which routinely contains braces, quotes and code — embedding that
    /// in JSON means escaping it, and a model reading escaped source is reading something other than
    /// what the child wrote.</para>
    /// </summary>
    /// <summary>
    /// Reads the <c>state</c> back out of a rendered envelope, or null if this is not one.
    ///
    /// <para>THE INVERSE OF <see cref="Render"/>, and deliberately parsed rather than threaded
    /// alongside: the envelope is the one artefact that always carries the outcome, so a caller that
    /// has the string has the truth. Threading a second copy of the same fact through the spawn
    /// dispatch would create two things that can disagree — and the failure would be silent, since
    /// both are plausible strings.</para>
    ///
    /// <para>Null for an ordinary tool result, which is how a non-spawn call is told apart.</para>
    /// </summary>
    public static string? StateOf(string? envelope)
    {
        if (string.IsNullOrEmpty(envelope)) return null;

        const string marker = "state=\"";
        var i = envelope.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;

        // Only in the opening tag: a state= appearing later is the CHILD's own text, not ours.
        var close = envelope.IndexOf('>', 0);
        if (close >= 0 && i > close) return null;

        var start = i + marker.Length;
        var end = envelope.IndexOf('"', start);
        return end > start ? envelope[start..end] : null;
    }

    public static string Render(string childId, SendOutcome outcome, string text)
    {
        var state = outcome switch
        {
            SendOutcome.Completed => "completed",
            SendOutcome.Capped => "capped",
            SendOutcome.Stuck => "stuck",
            SendOutcome.Cancelled => "cancelled",
            SendOutcome.Silent => "no-answer",
            _ => "error",
        };

        // A NOTE ON THE UNHAPPY STATES, because "capped" alone is a word the model has to interpret.
        // The parent must know that what follows is an ACCOUNT OF UNFINISHED WORK rather than an
        // answer — that is the entire reason the field exists.
        var note = outcome switch
        {
            SendOutcome.Capped =>
                "\nThis agent hit its turn limit before finishing. What follows is its own summary of "
                + "how far it got, NOT a completed answer — check what remains before relying on it.",
            SendOutcome.Stuck =>
                "\nThis agent stopped making progress: it repeated the same call and got the same "
                + "result. What follows may be incomplete.",
            SendOutcome.Failed =>
                "\nThis agent failed. What follows is the error, not an answer.",
            SendOutcome.Cancelled =>
                "\nThis agent was cancelled before finishing.",
            SendOutcome.Silent =>
                "\nThis agent produced no answer — its request to the model did not come back. It may "
                + "have completed some of its work before that happened, so check the tree before "
                + "assuming nothing changed, and re-run it if the work is still needed.",
            _ => "",
        };

        return $"<sub_agent id=\"{childId}\" state=\"{state}\">{note}\n{text}\n</sub_agent>";
    }
}
