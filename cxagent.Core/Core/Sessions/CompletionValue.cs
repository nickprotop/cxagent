namespace CxAgent.Core.Sessions;

/// <summary>
/// One offerable value and what it is — a provider instance and its model, an edit mode and what it
/// does, a session and when it ran.
///
/// <para>DELIBERATELY NOT A UI TYPE. The layers that own the data answer "what are the valid values
/// for this", and whether that becomes a popup, a dropdown or a printed list is the front end's
/// business. Core knowing about a command palette is the coupling this exists to avoid.</para>
/// </summary>
/// <param name="Name">What the user would type.</param>
/// <param name="Summary">What it means, shown beside the name.</param>
public readonly record struct CompletionValue(string Name, string Summary);

/// <summary>
/// The named sets a caller can ask a session or a manager to enumerate.
///
/// <para>CONSTANTS RATHER THAN LOOSE STRINGS, so a declaration and its supplier cannot drift apart
/// silently — a typo on either side offers nothing, which is the failure that reads as "the palette
/// is broken".</para>
///
/// <para>WHY THE OWNER ANSWERS, and not the composition root. Resolving these centrally means one UI
/// method reaching into a resume store, a provider catalog and a session's own state to build them —
/// the internals of three layers in the one place least equipped to own any of them. It also
/// discourages use: when adding a popup means editing the composition root, commands that want one
/// (<c>/mode edits</c>, <c>/mcp</c>) simply never get it. Asking the owner keeps each set beside the
/// state it is derived from.</para>
/// </summary>
public static class CompletionSets
{
    /// <summary>Configured provider instances, for <c>/model</c>. Answered by the session, which
    /// knows which one it is currently using.</summary>
    public const string Providers = "providers";

    /// <summary>Sessions in this folder, for <c>/sessions resume</c>. Answered by the manager, which
    /// owns the resume store.</summary>
    public const string Sessions = "sessions";

    /// <summary>Edit modes this session can be put into, for <c>/mode edits</c>. Answered by the
    /// session, because whether <c>auto</c> is among them depends on its own classifier.</summary>
    public const string EditModes = "edit-modes";

    /// <summary>Connected MCP servers, for <c>/mcp</c>. Answered by the manager, which owns the
    /// toolset — the LIVE servers, not the names in a config file that may not have connected.</summary>
    public const string McpServers = "mcp-servers";

    /// <summary>Delegation modes, for <c>/mode agent</c>. Answered by the session.</summary>
    public const string AgentModes = "agent-modes";

    /// <summary>Sub-agent types this session can spawn, for <c>/agents</c>. Answered by the session,
    /// because the catalog is the one it was wired against — a config edit since launch has not taken
    /// effect here and offering those names would point at types this session cannot spawn.</summary>
    public const string AgentTypes = "agent-types";

    /// <summary>Configured plugin names, for <c>/plugin load</c> and <c>/plugin unwire</c>. Answered
    /// by the session — <see cref="Sessions.Session.Resolution"/> already carries every configured
    /// name and whether config permits it, which is what lets the palette mark a disabled one rather
    /// than silently omitting a name the user knows they wrote.</summary>
    public const string Plugins = "plugins";
}
