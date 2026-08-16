namespace CxAgent.Core.Agent;

/// <summary>
/// One session's own connections to whatever is driving it.
///
/// <para>NOT "PRESENTATION". Every type here is a Core type over Core's own models — the UI
/// implements them, and so does <see cref="BufferedChatSink"/>, and so would a server, a log file
/// or a test recorder. Naming them for the implementer is the mistake this codebase has already
/// corrected once on these very interfaces.</para>
///
/// <para>ONE SET PER SESSION, which is the whole reason they are separate from
/// <see cref="SharedServices"/>. Two sessions may share a history database; they must not share a
/// transcript.</para>
/// </summary>
public sealed record SessionPorts
{
    /// <summary>What the session reports — text, reasoning, turn boundaries, failures.</summary>
    public required ISessionObserver Observer { get; init; }

    /// <summary>What it reports about the tools it runs.</summary>
    public required IToolObserver Tools { get; init; }

    /// <summary>How it asks the user something, or null when there is nobody to ask.</summary>
    public AskUser? Ask { get; init; }

    /// <summary>
    /// How THIS session's permission questions are judged — its working directory and edit mode.
    ///
    /// <para>PER SESSION, unlike the gate that asks them. One gate serves the process because stored
    /// rules and the prompt queue must be shared; the policy behind it must not be, because it holds
    /// a root and a mode that belong to one conversation. Captured in the gate, a second session
    /// would be judged against the first's folder and the first's <c>/mode edits</c> — allowing a
    /// write to a checkout the user never approved, with every layer behaving correctly.</para>
    ///
    /// <para>Null on the paths with no gating at all (headless, tests), where there is nothing to
    /// judge.</para>
    /// </summary>
    public Permissions.PermissionPolicy? Policy { get; init; }
}
