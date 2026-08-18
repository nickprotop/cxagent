using CxAgent.Core.Sessions;

namespace CxAgent.Core.Commands;

/// <summary>
/// What a command does, given the session it was typed into and whatever followed its name.
/// </summary>
/// <param name="session">
/// The session the command acts on.
///
/// <para>A PARAMETER RATHER THAN A CLOSURE, and the reason is coming rather than current: with one
/// session a handler could capture it and nothing would notice, and the moment a process has two —
/// tabs — every captured handler acts on whichever session existed when it was registered. Passing
/// it makes that unexpressible. Handlers that genuinely do not care (a key map, a quit) ignore it
/// today and will not be able to later, which is the point.</para>
/// </param>
/// <param name="arguments">Everything after the command name, trimmed. Empty when there was none.</param>
/// <returns>False when this command declines to handle the input, so a caller can fall through.</returns>
public delegate bool CommandHandler(Session session, string arguments);

/// <summary>
/// Every command this process can run, and what each one does.
///
/// <para>ONE TABLE, CONTRIBUTED TO BY BOTH LAYERS. Core seeds the commands that act on a session or
/// on the manager's own stores; a front end adds the ones only it can service — a key map needs a
/// window, quitting needs a message loop. Neither layer needs to know the split exists: dispatch is
/// a lookup, and where a handler came from stops being a question the caller asks.</para>
///
/// <para>WHAT IT REPLACES. Dispatch was a switch on <see cref="CommandOutcome"/> wrapping chains of
/// <c>if (command.Name == "/x")</c> — about 170 lines in a composition root, where adding a command
/// meant editing the largest method in the codebase. The outcome enum was meant to remove exactly
/// that name-matching and could not: nine of thirteen commands declared NeedsWindow, which its own
/// doc admits means "serviced by the UI rather than by the conversation" — not a precondition, and
/// not true of the eight that merely printed a string they had already computed.</para>
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A declaration and the code behind it.</summary>
    private readonly record struct Entry(SessionCommand Command, CommandHandler Handle);

    /// <summary>
    /// Adds a command, replacing any earlier one of the same name.
    ///
    /// <para>REPLACING RATHER THAN REFUSING, so a front end can override a seeded command with one
    /// that suits it — a host with no transcript might answer /clear differently. Last registration
    /// wins, which makes the composition root's contributions final by virtue of running last.</para>
    /// </summary>
    public void Register(SessionCommand command, CommandHandler handle) =>
        _byName[command.Name] = new Entry(command, handle);

    /// <summary>Every registered command, for a palette or a help listing.</summary>
    public IReadOnlyList<SessionCommand> All => [.. _byName.Values.Select(e => e.Command)];

    /// <summary>
    /// Runs the command in <paramref name="input"/>, if it names one.
    ///
    /// <para>FALSE MEANS "NOT A COMMAND" — the input is a goal and belongs to the model. A handler
    /// returning false means the same thing from one level down: it recognised its own name and
    /// decided this was not for it, so the caller keeps looking rather than swallowing the line.</para>
    /// </summary>
    public bool TryRun(Session session, string input)
    {
        if (SessionCommands.Match(input) is not { } command) return false;
        if (!_byName.TryGetValue(command.Name, out var entry)) return false;

        return entry.Handle(session, SessionCommands.Arguments(input));
    }
}
