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
/// <param name="Arguments">
/// What may follow the name, or empty when nothing may.
///
/// <para>THE TABLE HAD NO NOTION OF ARGUMENTS, and three surfaces inherited that blindness: the
/// popup, <c>/help</c>, and the unknown-command reply all rendered name-plus-summary, so
/// <c>/mcp reload</c>, <c>/stats clear</c> and <c>/mode fan-out</c> existed in the dispatcher and
/// nowhere a user could find them. A command that takes arguments and one that does not also looked
/// identical in the list, which is the discovery half of the same gap.</para>
/// </summary>
public readonly record struct SessionCommand(
    string Name,
    string Summary,
    CommandOutcome Outcome,
    IReadOnlyList<CommandArgument>? Arguments = null)
{
    /// <summary>Never null, so every consumer can enumerate without a guard.</summary>
    public IReadOnlyList<CommandArgument> Args => Arguments ?? [];

    /// <summary>Does anything follow this command's name?</summary>
    public bool TakesArguments => Args.Count > 0;

    /// <summary>
    /// The argument names as a compact hint — <c>[days|clear]</c> — for a palette row.
    ///
    /// <para>SQUARE BRACKETS AND A PIPE, the shell's own convention for "optional, one of these".
    /// Every argument here is optional (each command answers something useful bare), so there is no
    /// second form to distinguish.</para>
    /// </summary>
    public string Hint => TakesArguments ? $"[{string.Join("|", Args.Select(a => a.Name))}]" : "";
}

/// <summary>
/// One thing that may follow a command's name.
/// </summary>
/// <param name="Name">
/// The literal word — <c>reload</c> — or a placeholder in angle brackets when the value is the
/// user's to supply: <c>&lt;server&gt;</c>, <c>&lt;days&gt;</c>.
/// </param>
/// <param name="Summary">One line, for the row this becomes in a palette or in help.</param>
/// <param name="Completes">
/// Whether choosing this in the palette should fill the composer with it.
///
/// <para>FALSE FOR A PLACEHOLDER. Completing <c>/mcp &lt;server&gt;</c> literally would put text in
/// the composer that is not a command — the angle brackets are notation for the reader, not something
/// to type. Such a row is shown to say the argument EXISTS and leaves the typing to the user.</para>
/// </param>
public readonly record struct CommandArgument(string Name, string Summary, bool Completes = true);
