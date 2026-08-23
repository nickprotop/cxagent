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

    /// <summary>A verb registered against a command, and the code behind it.</summary>
    private readonly record struct Verb(CommandArgument Argument, CommandHandler Handle);

    private readonly Dictionary<string, List<Verb>> _verbs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds an argument to an existing command, serviced by this front end alone.
    ///
    /// <para>FOR WORK CORE CANNOT DO RATHER THAN WORK IT HAS NOT DONE. /stats clear deletes a
    /// usage archive and must be confirmed first, and a handler that returns bool has no way to
    /// ask a question and wait for the answer — so the verb, its help row and its palette row all
    /// belong to whoever can ask. A consumer that registers nothing is never offered it.</para>
    ///
    /// <para>THE ARGUMENT TRAVELS WITH THE HANDLER, not declared separately in the table, because
    /// the two go stale apart: a row offered by every front end and serviced by one is the exact
    /// failure this replaces.</para>
    /// </summary>
    public void RegisterVerb(string command, CommandArgument verb, CommandHandler handle)
    {
        if (!_verbs.TryGetValue(command, out var list))
            _verbs[command] = list = [];

        list.RemoveAll(v => string.Equals(v.Argument.Name, verb.Name,
            StringComparison.OrdinalIgnoreCase));
        list.Add(new Verb(verb, handle));
    }

    /// <summary>
    /// Everything that may follow this command's name here — the table's arguments plus the verbs
    /// this process registered.
    ///
    /// <para>WHAT HELP AND THE PALETTE MUST READ. Reading <see cref="SessionCommand.Args"/>
    /// directly offers rows that only some front ends service.</para>
    /// </summary>
    public IReadOnlyList<CommandArgument> ArgumentsOf(SessionCommand command) =>
        _verbs.TryGetValue(command.Name, out var list)
            ? [.. command.Args, .. list.Select(v => v.Argument)]
            : command.Args;

    /// <summary>
    /// What a dispatch attempt found.
    ///
    /// <para>THREE ANSWERS, NOT TWO. A bool folded "not a command" together with "a declared
    /// command nobody registered", and they call for opposite behaviour: the first is a goal and
    /// belongs to the model, the second must never reach a model — a front end that cannot
    /// service /exit should say so, not ask an LLM to end its process.</para>
    /// </summary>
    public enum Dispatch
    {
        /// <summary>Not a command at all. The input is a goal.</summary>
        NotACommand,

        /// <summary>A handler took it.</summary>
        Ran,

        /// <summary>Declared in the table, but this process registered nothing for it.</summary>
        NoHandler,
    }

    /// <summary>
    /// Runs the command in <paramref name="input"/>, if it names one.
    ///
    /// <para>A HANDLER RETURNING FALSE IS <see cref="Dispatch.NotACommand"/>: it recognised its
    /// own name and decided this was not for it, so the caller keeps looking rather than
    /// swallowing the line.</para>
    /// </summary>
    public Dispatch Run(Session session, string input)
    {
        // THE REGISTRY FIRST, THE TABLE SECOND. A command a front end declared and registered
        // itself — /exit — is not in SessionCommands.All at all, so matching against the table
        // alone would read it as NotACommand and hand "/exit" to the model as a goal. A command
        // the table declares but nobody registered — /mcp on a bare manager — is absent from the
        // registry's own view, so falling back to the table is what lets NoHandler below still
        // fire instead of this returning NotACommand for a command that plainly exists.
        if ((SessionCommands.Match(input, this) ?? SessionCommands.Match(input))
            is not { } command)
            return Dispatch.NotACommand;

        var arguments = SessionCommands.Arguments(input);

        // A VERB FIRST, and matched on the FIRST WORD so "/stats clear" reaches it while
        // "/stats 30" does not. A verb intercepts one argument, never the command.
        if (_verbs.TryGetValue(command.Name, out var verbs))
        {
            var first = arguments.Split(' ', 2)[0];
            foreach (var verb in verbs)
                if (string.Equals(verb.Argument.Name, first, StringComparison.OrdinalIgnoreCase))
                    return verb.Handle(session, arguments) ? Dispatch.Ran : Dispatch.NotACommand;
        }

        if (!_byName.TryGetValue(command.Name, out var entry)) return Dispatch.NoHandler;

        return entry.Handle(session, arguments)
            ? Dispatch.Ran
            : Dispatch.NotACommand;
    }

    /// <summary>Whether a handler took this input. See <see cref="Run"/> for the fuller answer.</summary>
    public bool TryRun(Session session, string input) => Run(session, input) == Dispatch.Ran;
}
