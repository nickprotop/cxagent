using CxAgent.Core.Agent;

namespace CxAgent.UI;

/// <summary>What <c>/mode</c> decided to do, and the line to show for it.</summary>
/// <param name="NewMode">The mode to switch to, or null when nothing changes.</param>
/// <param name="Reply">The message for the transcript. Never empty — a command that appears to do
/// nothing is indistinguishable from one that silently failed.</param>
public readonly record struct ModeCommandResult(WorkingMode? NewMode, string Reply);

/// <summary>
/// What <c>/mode</c> needs to answer "what mode am I in", and to change it.
///
/// <para>A RECORD BECAUSE THE LIST GREW BY ACCRETION — argument, mode, turnRunning, and now trust and
/// root so the listing can report what is ACTUALLY in force. Five positional parameters, three of
/// them strings and bools that transpose cleanly, is the exact shape the parameter rule exists to
/// catch: <c>Decide(arg, mode, true, false, root)</c> compiles just as well with the two booleans
/// swapped and reports the opposite of the truth.</para>
/// </summary>
/// <param name="Argument">Everything after the command word — empty for a bare <c>/mode</c>.</param>
/// <param name="Current">The mode right now, for reporting and for detecting a no-op.</param>
/// <param name="TurnRunning">Whether a turn is in flight; a mode change is declined mid-turn.</param>
/// <param name="FolderTrusted">
/// Whether this folder is trusted, so the listing can say what is IN FORCE rather than what the mode
/// name promises. An <c>accept-edits</c> session on an untrusted folder asks for everything, and the
/// listing is where a user meets that rule — at the moment it is affecting them.
/// </param>
/// <param name="Root">The working directory, named in the effect line so "inside" is concrete.</param>
/// <param name="ClassifierConfigured">
/// Whether a classifier instance is configured. FALSE HIDES AUTO ENTIRELY — unlisted, unparseable —
/// because a mode that claims background review while nothing reviews is worse than not having it.
/// </param>
public readonly record struct ModeQuery(string Argument, WorkingMode Current, bool TurnRunning,
    bool FolderTrusted, string Root, bool ClassifierConfigured = false);

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
    /// Decides what <c>/mode</c>, <c>/mode agent fan-out</c> or <c>/mode edits always-ask</c> should
    /// do.
    /// </summary>
    /// <remarks>
    /// DECLINED MID-TURN (<see cref="ModeQuery.TurnRunning"/>), and this is not caution for its own
    /// sake. The tool list is fixed once a request begins — deliberately, so a tool cannot appear or
    /// vanish between two turns of one request and leave the model chasing something that is no
    /// longer there. Changing mode under a running turn is exactly that, and it is the same predicate
    /// <c>/compress</c> and Escape share.
    /// </remarks>
    public static ModeCommandResult Decide(ModeQuery query)
    {
        var (argument, current, turnRunning) = (query.Argument, query.Current, query.TurnRunning);

        // A BARE /mode REPORTS EVERY AXIS. Asking what mode you are in must never change it — and
        // once there is more than one axis, "what mode am I in" has more than one answer, so this is
        // a BLOCK rather than a line. It reads like /skills and /mcp: a coloured heading, then one
        // indented row per thing, with the detail muted underneath.
        //
        // WRITTEN AS A LIST OF AXES while there was one of them, so adding the second was a row
        // rather than a rewrite of a sentence that assumed there was only ever one. It was.
        if (string.IsNullOrWhiteSpace(argument))
        {
            var accent = ColorScheme.AccentMarkup;
            var muted = ColorScheme.MutedMarkup;

            return new(null, string.Join('\n',
            [
                $"[{accent}]Working mode[/]",
                "",
                $"  [{accent}]agent[/]  {AgentModes.Name(current.Agent)}",
                $"    [{muted}]{(current.CanDelegate
                    ? "can spawn sub-agents"
                    : "works alone; the spawn tool is withdrawn")}[/]",
                "",
                $"  [{accent}]edits[/]  {EditModes.Name(current.Edits)}",
                $"    [{muted}]{EditsEffect(current.Edits, query.FolderTrusted, query.Root)}[/]",
                "",
                $"  [{muted}]set with /mode agent {AgentModes.Valid.Replace(", ", " | ")}[/]",
                $"  [{muted}]         /mode edits "
              + $"{EditModes.ValidWith(query.ClassifierConfigured).Replace(", ", " | ")}[/]",
            ]));
        }

        var words = argument.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // `/mode <axis> <value>` — the axis named, then its value. The axis is what made room for a
        // second one without either colliding with the first or needing a command of its own.
        //
        // `/mode fan-out` STILL WORKS unqualified, because agent's values remain unambiguous.
        //
        // THE EDITS AXIS MUST BE NAMED, and that is the prediction landing rather than an exception:
        // "ask" and "edits" say nothing about which axis they belong to. The comment here used to say
        // the day a value collides across axes is the day to demand the axis word. This is that day,
        // for this axis only.
        if (words.Length >= 2 && IsEditsAxis(words[0]))
        {
            var editValue = string.Join(' ', words.Skip(1));
            var requestedEdits = EditModes.Parse(editValue, query.ClassifierConfigured);

            if (requestedEdits is null)
                return new(null, $"[yellow]unknown edit mode '{editValue.Trim()}'. "
                               + $"Valid: {EditModes.ValidWith(query.ClassifierConfigured)}.[/]");

            if (requestedEdits == current.Edits)
                return new(null, $"already in {EditModes.Name(current.Edits)} mode.");

            if (turnRunning)
                return new(null, "[yellow]A turn is running — press Escape to stop it first.[/]");

            // WHAT IS ACTUALLY IN FORCE, not what the name promises — the same rule the listing
            // follows. Switching to accept-edits on an untrusted folder changes nothing observable,
            // and saying "writes are now silent" there would be a readout that is wrong exactly when
            // it matters.
            return new(current with { Edits = requestedEdits.Value },
                $"edits: {EditModes.Name(requestedEdits.Value)} — "
                + $"{EditsEffect(requestedEdits.Value, query.FolderTrusted, query.Root)}.");
        }

        var value = words.Length >= 2 && IsAgentAxis(words[0])
            ? string.Join(' ', words.Skip(1))
            : argument;

        // AN AXIS WE DO NOT KNOW IS NOT A VALUE. `/mode work plan` today should say that work is not
        // an axis yet, rather than "unknown mode 'work plan'" — which reads as though the VALUE were
        // wrong and sends the user hunting for the right spelling of it.
        if (words.Length >= 2 && !IsAgentAxis(words[0]) && KnownAxes.Contains(words[0].ToLowerInvariant()))
            return new(null, $"[yellow]'{words[0]}' is not settable yet. Valid axes: agent, edits.[/]");

        var requested = AgentModes.Parse(value);

        // NAME THE VALID VALUES. "unknown mode" alone leaves someone guessing at the spelling, and
        // the guess they usually make — "fanout" — is already accepted by the parser.
        if (requested is null)
            return new(null, $"[yellow]unknown mode '{value.Trim()}'. Valid: {AgentModes.Valid}.[/]");

        // A NO-OP SAYS SO AND CHANGES NOTHING. Applying it anyway would rewrite index 0 with
        // identical text — harmless, since reconciliation only replaces when the text differs, but
        // reporting "switched" when nothing switched is a small lie the user will act on.
        if (requested == current.Agent)
            return new(null, $"already in {AgentModes.Name(current.Agent)} mode.");

        if (turnRunning)
            return new(null, "[yellow]A turn is running — press Escape to stop it first.[/]");

        // WHAT ACTUALLY CHANGES, said plainly. A mode is invisible otherwise: the user has no way to
        // see a tool list, and "switched to fan-out" alone does not tell them what they now have.
        var effect = requested == AgentMode.FanOut
            ? "this agent can now spawn sub-agents."
            : "this agent works alone; the spawn tool is withdrawn.";

        // THE OTHER AXIS IS CARRIED, not reset. `with` is why this record exists.
        return new(current with { Agent = requested.Value },
            $"agent: {AgentModes.Name(requested.Value)} — {effect} The conversation is unchanged.");
    }

    /// <summary>
    /// What the edit mode ACTUALLY does right now, which is not always what its name says.
    ///
    /// <para>An <c>accept-edits</c> session on an untrusted folder asks for everything — modes
    /// narrow, trust bounds — and this line is where a user meets that rule, at the moment it is
    /// affecting them rather than in documentation they will not read. Reporting the nominal effect
    /// here would be a readout that is wrong exactly when it matters.</para>
    /// </summary>
    private static string EditsEffect(EditMode mode, bool trusted, string root) => mode switch
    {
        _ when !trusted => "asks for everything (this folder is not trusted)",
        EditMode.AlwaysAsk => "every write asks; stored rules still apply",
        EditMode.Auto => $"a classifier reviews each write; outside {root} asks",
        _ => $"writes inside {root} are silent; elsewhere asks",
    };

    /// <summary>The edits axis, spelled the ways people reach for it.</summary>
    private static bool IsEditsAxis(string word) =>
        word.Equals("edits", StringComparison.OrdinalIgnoreCase)
        || word.Equals("edit", StringComparison.OrdinalIgnoreCase)
        || word.Equals("editing", StringComparison.OrdinalIgnoreCase)
        || word.Equals("files", StringComparison.OrdinalIgnoreCase)
        || word.Equals("file", StringComparison.OrdinalIgnoreCase);

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
        // "files", "file", "edit", "editing" LEFT THIS SET when the edits axis became real — they are
        // handled by IsEditsAxis above and would never reach here.
        //
        // A PLANNING AXIS WAS CONSIDERED AND DECLINED. WorkingMode's doc named it as a third axis, and
        // it would have withdrawn the write tools, restricted shell to ReadOnlyCommands, and swapped
        // in a planning prompt. The reason not to: withholding tools does not make a model plan well —
        // the BRIEFING does, and the `planner` agent type already carries one. A session axis would
        // re-implement that through a weaker mechanism and then owe an answer to "which governs" when
        // a user sets both.
        //
        // What it would have added is the model not ATTEMPTING writes, which costs turns rather than
        // safety. `/mode edits always-ask` plus the planner type covers the safety half today.
        //
        // These words stay here so `/mode work plan` still says "not settable yet" rather than
        // blaming the value — the user reaching for it has a real intent, and the planner type is
        // where it is served.
        "work", "task", "plan", "planning",
    };
}
