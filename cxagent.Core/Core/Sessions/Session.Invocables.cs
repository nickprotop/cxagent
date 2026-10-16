using CxAgent.Core.Commands;
using CxAgent.Core.Skills;

namespace CxAgent.Core.Sessions;

/// <summary>A line the user typed, and the command file or skill it names.</summary>
/// <param name="Line">The whole line, <c>/name</c> and its arguments, as typed.</param>
/// <param name="Entry">What the name resolved to.</param>
public sealed record TypedCommand(string Line, Invocable Entry)
{
    /// <summary>Text to put under the expansion — headless piped input — never into
    /// <c>$ARGUMENTS</c>.</summary>
    public string? Attachment { get; init; }
}

public sealed partial class Session
{
    // THE LINE WHOSE EXPANSION IS RUNNING, for CancelTurn to hand back. Set and cleared by the
    // expansion itself, on the turn's thread; taken by CancelTurn on the caller's, before it cancels
    // (see there for why the order matters).
    private string? _expanding;

    /// <summary>
    /// Every command file and skill this session can see, freshly scanned — what <c>/commands</c>,
    /// <c>/skills</c> and the palette list.
    /// </summary>
    public InvocableCatalogResult Catalog() => InvocableCatalog.Find(
        new CatalogRoots(WorkingDirectory, Services?.GlobalInstructionsDir));

    /// <summary>
    /// The command file or skill a typed line runs, or null.
    ///
    /// <para>NULL FOR ANY NAME THE REGISTRY HAS. A built-in or a plugin's command wins by
    /// construction, so a repo's <c>init.md</c> can never change what <c>/init</c> does.</para>
    /// </summary>
    public Invocable? FindTyped(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('/') || trimmed.Contains('\n')) return null;
        if (Manager is { } manager && SessionCommands.Match(trimmed, manager.Commands) is not null) return null;

        var name = CommandText.SplitLine(trimmed).Name;
        return name.Length == 0 ? null : Catalog().Typed(name);
    }

    /// <summary>
    /// Runs a typed command as a turn whose first step is its expansion — or holds it, when a turn is
    /// already running (see <see cref="Hold"/>).
    /// </summary>
    public SubmitOutcome RunCommand(TypedCommand command)
    {
        if (Host is null) return new SubmitOutcome.NoAgent();
        if (IsBusy) return Hold(command.Line);

        var line = command.Line.Trim();
        var arguments = CommandText.SplitLine(line).Arguments;

        return Send(new TurnRequest(line, line, null, null, SubmitSource.User)
        {
            Expand = async ct =>
            {
                Volatile.Write(ref _expanding, line);
                try
                {
                    var text = await CommandExpander.ExpandAsync(
                        new ExpansionRequest(command.Entry, arguments), Ports(), ct);

                    // BLANK MEANS BLANK BEFORE ANYTHING IS ADDED. Checked after the attachment and the
                    // app's queued text, a command that said nothing would still call the model — with
                    // only the app's note, as if the user had sent it. Returning here also leaves that
                    // note queued for the user's next real message.
                    if (string.IsNullOrWhiteSpace(text)) return null;
                    if (command.Attachment is { Length: > 0 } attached) text += "\n\n" + attached;

                    // WHAT THE APP QUEUED GOES AFTER, not before: a typed skill must open with its
                    // marker for SkillLoader.LoadedIn to see it.
                    if (TakeInjected() is { Length: > 0 } injected) text += "\n\n" + injected;
                    return text;
                }
                finally
                {
                    Volatile.Write(ref _expanding, null);
                }
            },
        }, attachInjected: false);
    }

    private ExpansionPorts Ports() => new(WorkingDirectory,
        async (request, ct) =>
        {
            // NO GATE, NO PERMISSION. A bare session with nothing to ask runs no shell line and reads
            // no file — the safe answer for a file someone else may have written.
            if (Services?.Gate is not { } gate) return false;
            var outcome = await gate.RequestAsync(request with { Policy = Policy, RaisedByCommand = true }, ct);
            return outcome.Allowed;
        },
        Say);

    private string? _held;
    private readonly object _heldGate = new();

    /// <summary>The command waiting for the running turn to end, or null.</summary>
    public string? HeldCommand { get { lock (_heldGate) return _held; } }

    /// <summary>The held line when the slot fills or changes; null when it empties.</summary>
    public event Action<string?>? HeldChanged;

    /// <summary>
    /// Keeps a command typed mid-turn until the turn ends.
    ///
    /// <para>HELD, NOT STEERED. Steered text joins the running turn; a command is a turn of its own,
    /// and its shell lines must read the state AFTER the running turn's edits — so it is expanded only
    /// when it runs, not when typed.</para>
    ///
    /// <para>ONE SLOT. A second command replaces the first and says so: queuing several would run a
    /// chain of turns nobody is watching.</para>
    ///
    /// <para>INTERNAL so a test can call it on an idle session — the one moment of the race below
    /// that cannot be forced by scheduling.</para>
    /// </summary>
    internal SubmitOutcome Hold(string line)
    {
        string? replaced;
        lock (_heldGate) { replaced = _held; _held = line.Trim(); }

        if (replaced is not null)
            Say(new Message($"{replaced} was not run — {line.Trim()} replaced it.", Severity.Warning));
        HeldChanged?.Invoke(line.Trim());

        // THE TURN MAY HAVE ENDED SINCE THE CALLER SAW IT BUSY. The tail takes the slot after
        // releasing ownership; a line stored after that take has no turn left to run it, and would sit
        // in the slot until some later turn happened to end. Checked AFTER storing, so one of the
        // two always sees the line: either the tail's take comes later and finds it, or this check
        // does. TakeHeld is atomic, so they never both run it.
        //
        // OWNERSHIP, NOT IsBusy, which drops between laps of one turn: running the line in that gap
        // would reach Send, find the turn owned, and be held again — here — round and round.
        if (!TurnOwned && TakeHeld() is { } stranded) return RunHeld(stranded);
        return new SubmitOutcome.Queued();
    }

    /// <summary>
    /// Runs a line taken from the slot, through <see cref="Route"/> — Submit's path minus btw mode.
    ///
    /// <para>THE MAIN CONVERSATION, ALWAYS. btw mode sends what the user types there to the aside;
    /// this was typed before the mode was entered, as a command for the turn it was waiting on, and
    /// sent to the aside it would become a question about "/commit" and never run.</para>
    /// </summary>
    private SubmitOutcome RunHeld(string held) =>
        Route(new TurnRequest(held, null, null, null, SubmitSource.User));

    private string? TakeHeld()
    {
        string? taken;
        lock (_heldGate) { taken = _held; _held = null; }
        if (taken is not null) HeldChanged?.Invoke(null);
        return taken;
    }

    /// <summary>Empties the slot and names what will not run: on close, and when a turn is cancelled
    /// by something other than <see cref="CancelTurn"/> — which takes the slot itself.</summary>
    internal void DropHeld()
    {
        if (TakeHeld() is { } dropped) Say(new Message($"{dropped} was not run.", Severity.Warning));
    }

    /// <summary>
    /// What a cancel returns to the composer: ONE thing, the first of the steered text, the command
    /// whose expansion was running, and the held command. The composer takes one line back cleanly — a
    /// <c>/commit</c> on line two is not a command — so anything else is named in a notice rather than
    /// dropped silently.
    /// </summary>
    /// <param name="expanding">The line whose expansion was running, already taken by
    /// <see cref="CancelTurn"/> — it cannot be read here, after the cancel has unwound it.</param>
    /// <param name="held">The held command, taken before the cancel for the same reason: the cancel
    /// exit would otherwise drop it as "not run" before it could be handed back.</param>
    private void HandBack(string? expanding, string? held)
    {
        string? steered;
        lock (_pendingGate) { steered = _pending; _pending = null; }

        var lines = new[] { steered, expanding, held }.Where(l => l is { Length: > 0 }).ToList();
        if (lines.Count == 0) return;

        Cancelled?.Invoke(lines[0]!, null);
        foreach (var dropped in lines.Skip(1))
            Say(new Message($"{dropped!.Split('\n')[0]} was not run.", Severity.Warning));
    }
}
