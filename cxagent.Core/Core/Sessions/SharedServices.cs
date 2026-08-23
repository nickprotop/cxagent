using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Sessions;

/// <summary>
/// What every session in a process shares, and shares DELIBERATELY.
///
/// <para>NAMED FOR ITS LIFETIME, which is the distinction that matters. Each member here is built
/// once at startup and handed to every session; each member of <see cref="SessionPorts"/> is built
/// per session and handed to exactly one. A single bag of dependencies could not say which is
/// which, and the difference is not cosmetic: two sessions sharing a history database is the
/// feature that makes <c>/stats</c> span sessions, while two sessions sharing a transcript is two
/// conversations in one scrollback.</para>
///
/// <para>SHARING IS SAFE BY CONSTRUCTION, not by luck, and TwoSessionsTests proves each one:
/// <see cref="LogFileManager"/> is immutable and nests by agent ancestry;
/// <see cref="SqliteSessionStore"/> and <see cref="UsageHistoryStore"/> key by agent id and run WAL
/// with a busy timeout; the rules store behind <see cref="Gate"/> scopes by folder and merges
/// another writer's newer rules. Splitting them would break the features that depend on the
/// sharing.</para>
///
/// <para>EVERY MEMBER IS OPTIONAL. A headless session has no resume buffer, no history and no
/// gate, and that is an ordinary configuration rather than a degraded one.</para>
///
/// <para>ONE PROCESS-WIDE THING IS DELIBERATELY NOT HERE:
/// <see cref="Jobs.Builtin.FileMutation"/>, whose per-path lock table serialises two sessions
/// editing one file. It is reached statically rather than injected, and that is the difference
/// between this record and an invariant. Every member below is CONFIGURATION — a headless session
/// passes null, a test passes a fake, and a session receiving a different one is a legitimate
/// arrangement. A session receiving a different lock table is not: two of them serialise against
/// nothing, silently, while every test that checks isolation still passes. Injectability is exactly
/// the property it must not have, so it is static and
/// <c>TwoSessions_EditingOneFile_DoNotLoseEachOthersWork</c> fails if anyone splits it.</para>
/// </summary>
public sealed record SharedServices
{
    /// <summary>Where agents write their logs. Nests by agent id, so children land under parents.</summary>
    public LogFileManager? Logs { get; init; }

    /// <summary>The resume buffer — every completed turn, so a crash leaves something to come back to.</summary>
    public SqliteSessionStore? Resume { get; init; }

    /// <summary>The usage archive behind <c>/stats</c>. A different database from Resume, deliberately.</summary>
    public UsageHistoryStore? History { get; init; }

    /// <summary>Connected MCP servers, or null when none are configured — the common case.</summary>
    public Mcp.McpToolset? Mcp { get; init; }

    /// <summary>
    /// What the MCP servers are doing right now, or null when this host has none.
    ///
    /// <para>A SUPPLIER RATHER THAN THE MANAGER. Listing needs connection state and per-server
    /// errors, which only the thing that opened the connections knows — but reconnection, OAuth and
    /// the token store are the host's, and handing those to Core to render a table would couple it
    /// to a lifecycle it has no part in. A function returning the current statuses is the whole of
    /// what listing needs.</para>
    ///
    /// <para>READ EACH TIME, never cached: servers reconnect and fail between one /mcp and the next,
    /// and a snapshot taken at startup would describe a world that has moved.</para>
    /// </summary>
    public Func<IReadOnlyList<Mcp.McpServerStatus>>? McpStatuses { get; init; }

    /// <summary>The permission gate. ONE per process: a fresh gate per session would forget every
    /// rule and trust decision the user has already made.</summary>
    public IPermissionGate? Gate { get; init; }

    /// <summary>
    /// Which tools every session this manager opens is offered. Null means no opinion.
    ///
    /// <para>S1 IN CODE, and there is a second home for the same level: <c>llmAgent.tools</c> in
    /// config, which a user writes. They compose in order — an embedder saying what their
    /// application permits, a user saying what their machine does — and neither can exceed what the
    /// agent structurally has.</para>
    ///
    /// <para>PROCESS-WIDE, like the gate above it: two sessions may narrow differently through
    /// <see cref="SessionPorts.ToolSelection"/>, but this is the floor an embedder sets once.</para>
    /// </summary>
    public Jobs.ToolSelection? ToolSelection { get; init; }

    /// <summary>
    /// cxagent's own config directory, for globally-installed instructions and skills.
    ///
    /// <para>A STRING RATHER THAN AppPaths, because that is all the assembly needs — the whole
    /// paths object would be five unused members travelling with one used one.</para>
    /// </summary>
    public string? GlobalInstructionsDir { get; init; }
}
