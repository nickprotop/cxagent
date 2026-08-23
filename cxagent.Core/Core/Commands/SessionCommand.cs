namespace CxAgent.Core.Commands;

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
/// <param name="Arguments">
/// What may follow the name, or empty when nothing may.
///
/// <para>THE TABLE HAD NO NOTION OF ARGUMENTS, and three surfaces inherited that blindness: the
/// popup, <c>/help</c>, and the unknown-command reply all rendered name-plus-summary, so
/// <c>/mcp reload</c>, <c>/stats clear</c> and <c>/mode fan-out</c> existed in the dispatcher and
/// nowhere a user could find them. A command that takes arguments and one that does not also looked
/// identical in the list, which is the discovery half of the same gap.</para>
/// </param>
public readonly record struct SessionCommand(
    string Name,
    string Summary,
    IReadOnlyList<CommandArgument>? Arguments = null)
{
    /// <summary>Never null, so every consumer can enumerate without a guard.</summary>
    public IReadOnlyList<CommandArgument> Args => Arguments ?? [];

    /// <summary>Does anything follow this command's name?</summary>
    public bool TakesArguments => Args.Count > 0;

    /// <summary>
    /// True when this command cannot work without a provider — it reaches the model.
    ///
    /// <para>TWO COMMANDS, AND THE REST ARE FREE. Everything else answers from state the process
    /// already holds, costing no tokens and no time, which is what lets a session opened without
    /// a working provider still run them — including /model, the one command that FIXES having
    /// no provider.</para>
    ///
    /// <para>AN INIT PROPERTY, NOT A POSITIONAL PARAMETER, so only the two commands that need it
    /// mention it. A bool in the constructor is a value every future declaration must supply and
    /// can transpose with a neighbour silently.</para>
    /// </summary>
    public bool NeedsModel { get; init; }

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
/// <param name="Values">
/// Names a source of LIVE rows for this argument, or null when the argument names nothing the app
/// knows about.
///
/// <para>THE TABLE STAYS A DESCRIPTION OF THE COMMANDS, not a view of the world. It declares that
/// <c>&lt;instance&gt;</c> is filled from "providers"; the composition root, which owns the registry,
/// is what answers. Nothing here reads a session.</para>
///
/// <para>A NAME RATHER THAN A DELEGATE, because the table is static and a delegate would have to be
/// bound at startup — turning a constant into something assembled. The supplier is keyed on this
/// string, so an argument that names a missing source simply offers nothing.</para>
/// </param>
public readonly record struct CommandArgument(
    string Name, string Summary, bool Completes = true, string? Values = null);

/// <summary>
/// The named sources a <see cref="CommandArgument"/> can be filled from.
///
/// <para>CONSTANTS RATHER THAN LOOSE STRINGS, so the declaration in the table and the supplier in
/// the composition root cannot drift apart silently — a typo on either side would simply offer
/// nothing, which is the failure that looks like "the palette is broken".</para>
/// </summary>
public static class ValueSources
{
    /// <summary>Configured provider instances, for <c>/model</c>.</summary>
    public const string Providers = Core.Sessions.CompletionSets.Providers;

    /// <summary>Sessions in this folder, for <c>/sessions resume</c>.</summary>
    public const string Sessions = Core.Sessions.CompletionSets.Sessions;

    /// <summary>Edit modes, for <c>/mode edits</c>.</summary>
    public const string EditModes = Core.Sessions.CompletionSets.EditModes;

    /// <summary>Connected MCP servers, for <c>/mcp</c>.</summary>
    public const string McpServers = Core.Sessions.CompletionSets.McpServers;

    /// <summary>Delegation modes, for <c>/mode agent</c>.</summary>
    public const string AgentModes = Core.Sessions.CompletionSets.AgentModes;

    /// <summary>Sub-agent types, for <c>/agents</c>.</summary>
    public const string AgentTypes = Core.Sessions.CompletionSets.AgentTypes;
}
