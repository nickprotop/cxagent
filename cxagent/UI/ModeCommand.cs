using CxAgent.Core.Agent;

namespace CxAgent.UI;

/// <summary>What <c>/mode</c> decided to do, and the line to show for it.</summary>
/// <param name="NewMode">The mode to switch to, or null when nothing changes.</param>
/// <param name="Reply">The message for the transcript. Never empty — a command that appears to do
/// nothing is indistinguishable from one that silently failed.</param>
public readonly record struct ModeCommandResult(AgentMode? NewMode, string Reply);

/// <summary>
/// The decision behind <c>/mode</c>, separated from the wiring that applies it.
///
/// <para>SEPARATED FOR THE SAME REASON AS <see cref="EscapeRouting"/> AND <see cref="PromptQueue"/>:
/// the decision is worth testing and needs no window, no provider and no running agent. Everything
/// here is a pure function of (argument, current mode, is a turn running).</para>
/// </summary>
public static class ModeCommand
{
    /// <summary>
    /// Decides what <c>/mode</c>, <c>/mode single</c> or <c>/mode fan-out</c> should do.
    /// </summary>
    /// <param name="argument">Everything after the command word — empty for a bare <c>/mode</c>.</param>
    /// <param name="current">The mode right now, for reporting and for detecting a no-op.</param>
    /// <param name="turnRunning">
    /// DECLINED MID-TURN, and this is not caution for its own sake. The tool list is fixed once a
    /// request begins — deliberately, so a tool cannot appear or vanish between two turns of one
    /// request and leave the model chasing something that is no longer there. Changing mode under a
    /// running turn is exactly that, and it is the same predicate <c>/compress</c> and Escape share.
    /// </param>
    public static ModeCommandResult Decide(string argument, AgentMode current, bool turnRunning)
    {
        // A BARE /mode REPORTS. Asking what mode you are in must never change it.
        if (string.IsNullOrWhiteSpace(argument))
            return new(null, $"mode: {AgentModes.Name(current)}  (set with /mode single or /mode fan-out)");

        var requested = AgentModes.Parse(argument);

        // NAME THE VALID VALUES. "unknown mode" alone leaves someone guessing at the spelling, and
        // the guess they usually make — "fanout" — is already accepted by the parser.
        if (requested is null)
            return new(null, $"[yellow]unknown mode '{argument.Trim()}'. Valid: {AgentModes.Valid}.[/]");

        // A NO-OP SAYS SO AND CHANGES NOTHING. Applying it anyway would rewrite index 0 with
        // identical text — harmless, since reconciliation only replaces when the text differs, but
        // reporting "switched" when nothing switched is a small lie the user will act on.
        if (requested == current)
            return new(null, $"already in {AgentModes.Name(current)} mode.");

        if (turnRunning)
            return new(null, "[yellow]A turn is running — press Escape to stop it first.[/]");

        // WHAT ACTUALLY CHANGES, said plainly. A mode is invisible otherwise: the user has no way to
        // see a tool list, and "switched to fan-out" alone does not tell them what they now have.
        var effect = requested == AgentMode.FanOut
            ? "this agent can now spawn sub-agents."
            : "this agent works alone; the spawn tool is withdrawn.";

        return new(requested, $"mode: {AgentModes.Name(requested.Value)} — {effect} "
                            + "The conversation is unchanged.");
    }
}
