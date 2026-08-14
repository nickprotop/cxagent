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
}
