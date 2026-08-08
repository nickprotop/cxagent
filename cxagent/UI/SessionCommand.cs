namespace CxAgent.UI;

/// <summary>
/// What the caller must do about a command it just dispatched.
///
/// <para>REPLACES A BOOL PLUS A SIDE-CHANNEL. <c>TryHandle</c> returned "was this a command?" and
/// <c>/compress</c> needed a separate <c>IsCompress</c> probe called BEFORE it, because compressing
/// requires a provider call and the handler is synchronous. That worked for one exception and does
/// not generalise: every command needing something the handler cannot do — a provider, the window,
/// the process — would arrive as another probe, checked in an order nobody can see is significant.
/// One outcome per command says it in the return value instead.</para>
/// </summary>
public enum CommandOutcome
{
    /// <summary>Not a command at all. The caller runs it as a goal.</summary>
    NotACommand,

    /// <summary>Done, and the reply is ready to show.</summary>
    Handled,

    /// <summary>
    /// Recognised, but the work needs a provider and an await — the caller services it. Only
    /// <c>/compress</c> today: it summarises through the model exactly as auto-compression does.
    /// </summary>
    NeedsProvider,

    /// <summary>
    /// Recognised, but serviced by the UI rather than by the conversation — <c>/help</c> posts the
    /// key map. Named as an outcome rather than matched by NAME at the call site: the dispatcher was
    /// branching on outcome for some commands and on <c>cmd.Name == "/help"</c> for others, which is
    /// the ordered-probe shape the outcome exists to remove.
    /// </summary>
    NeedsWindow,

    /// <summary>Recognised, and the application should shut down.</summary>
    Quit,
}

/// <summary>
/// One slash command: its name, what it does, and how it is serviced.
///
/// <para>A TABLE, NOT A SWITCH, because three separate things need the same list and were each
/// keeping their own copy: the dispatcher, the "Unknown command — available: …" reply, and the help
/// text. The reply hardcoded <c>"/clear, /compress"</c> and would have gone stale the moment a
/// command was added.</para>
///
/// <para>It is also what a COMMAND PALETTE needs. A palette is a filtered list of name-plus-summary
/// pairs that dispatches the chosen one — which is this record, a <c>Contains</c>, and the dispatch
/// that already exists. Tab completion is the same list under a different filter. Neither is built
/// yet; both are a consumer of <see cref="SessionCommands.All"/> rather than a change to it, and
/// that is the point of putting the data here.</para>
/// </summary>
/// <param name="Name">The command including its leading slash — <c>/clear</c>.</param>
/// <param name="Summary">One line, imperative, for help and for a palette row.</param>
/// <param name="Outcome">What the caller must do when this command is dispatched.</param>
public readonly record struct SessionCommand(string Name, string Summary, CommandOutcome Outcome);
