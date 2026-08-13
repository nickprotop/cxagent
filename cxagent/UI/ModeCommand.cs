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
        // A BARE /mode REPORTS EVERY AXIS. Asking what mode you are in must never change it — and
        // once there is more than one axis, "what mode am I in" has more than one answer, so this is
        // a BLOCK rather than a line. It reads like /skills and /mcp: a coloured heading, then one
        // indented row per thing, with the detail muted underneath.
        //
        // WRITTEN AS A LIST OF AXES NOW, while there is one of them, so adding the second is a row
        // rather than a rewrite of a sentence that assumed there was only ever one.
        if (string.IsNullOrWhiteSpace(argument))
        {
            var accent = ColorScheme.AccentMarkup;
            var muted = ColorScheme.MutedMarkup;

            return new(null, string.Join('\n',
            [
                $"[{accent}]Working mode[/]",
                "",
                $"  [{accent}]agent[/]  {AgentModes.Name(current)}",
                $"    [{muted}]{(current == AgentMode.FanOut
                    ? "can spawn sub-agents"
                    : "works alone; the spawn tool is withdrawn")}[/]",
                "",
                $"  [{muted}]set with /mode agent {AgentModes.Valid.Replace(", ", " | ")}[/]",
            ]));
        }

        var words = argument.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // `/mode agent fan-out` — the axis named, then its value. The axis is what makes room for
        // `/mode files read-only` and `/mode work plan` without either colliding with this one or
        // needing a command of its own.
        //
        // `/mode fan-out` STILL WORKS, and that is deliberate rather than legacy tolerance: agent is
        // the only axis today, so naming it is pure ceremony for the one thing anyone is switching.
        // The day a value collides across axes, the unqualified form for THAT value stops being
        // unambiguous — which is a reason to name the axis then, not to demand it now.
        var value = words.Length >= 2 && IsAgentAxis(words[0])
            ? string.Join(' ', words.Skip(1))
            : argument;

        // AN AXIS WE DO NOT KNOW IS NOT A VALUE. `/mode files read-only` today should say that files
        // is not an axis yet, rather than "unknown mode 'files read-only'" — which reads as though
        // the VALUE were wrong and sends the user hunting for the right spelling of it.
        if (words.Length >= 2 && !IsAgentAxis(words[0]) && KnownAxes.Contains(words[0].ToLowerInvariant()))
            return new(null, $"[yellow]'{words[0]}' is not settable yet. Valid axes: agent.[/]");

        var requested = AgentModes.Parse(value);

        // NAME THE VALID VALUES. "unknown mode" alone leaves someone guessing at the spelling, and
        // the guess they usually make — "fanout" — is already accepted by the parser.
        if (requested is null)
            return new(null, $"[yellow]unknown mode '{value.Trim()}'. Valid: {AgentModes.Valid}.[/]");

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

        return new(requested, $"agent: {AgentModes.Name(requested.Value)} — {effect} "
                            + "The conversation is unchanged.");
    }

    /// <summary>The axis this command sets today. Spelled both ways people reach for.</summary>
    private static bool IsAgentAxis(string word) =>
        word.Equals("agent", StringComparison.OrdinalIgnoreCase)
        || word.Equals("agents", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Axis names that are COMING, so naming one gets a useful answer rather than a confusing one.
    ///
    /// <para>Listed before they work on purpose: `/mode files read-only` should say files is not
    /// settable yet, not "unknown mode 'files read-only'" — which reads as though the value were
    /// misspelled and sends the user looking for the right one.</para>
    /// </summary>
    private static readonly HashSet<string> KnownAxes = new(StringComparer.Ordinal)
    {
        "files", "file", "edit", "editing", "work", "task",
    };
}
