namespace CxAgent.Core.Sessions;

/// <summary>
/// What about a session changed — see <see cref="Session.Changed"/>.
///
/// <para>A KIND AND NOTHING ELSE. Everything a watcher needs is readable from the session it already
/// holds, so carrying values here would be a second copy of state that can disagree with the first.
/// This exists only so a watcher can skip work: a resume has no reason to repaint a model label.</para>
/// </summary>
public enum SessionChangeKind
{
    /// <summary>The working mode moved — delegation, edits, or both.</summary>
    Mode,

    /// <summary>A different model, over the same conversation.</summary>
    Model,

    /// <summary>An earlier conversation was restored into this session.</summary>
    Resumed,

    /// <summary>The running turn was stopped. The session, its context and its MCP servers survive —
    /// what a surface does about a half-drawn response is its own decision.</summary>
    TurnCancelled,

    /// <summary>The conversation was emptied. What a surface does about its own scrollback is its
    /// own decision — the session only reports that the messages behind it are gone.</summary>
    ContextCleared,
}
