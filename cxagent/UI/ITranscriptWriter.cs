namespace CxAgent.UI;

/// <summary>
/// How the UI writes to its own transcript.
///
/// <para>WHY THIS IS NOT <c>ISessionObserver</c>. The UI used to print through the session's observer —
/// 26 call sites of <c>ShowSystemMessage</c>, none of them from Core. That made the session's port a
/// general-purpose message bus, and meant the layer above needed a session-shaped object simply to
/// say something to the user. A command reporting "reloaded 3 servers" is not the session reporting
/// anything; it is the UI talking to its own surface.</para>
///
/// <para>Markup, not plain text, unlike the session's observer: these callers are the UI, they know
/// the transcript renders markup, and they already write it.</para>
/// </summary>
public interface ITranscriptWriter
{
    /// <summary>A line for the user, in transcript markup.</summary>
    void Write(string markup);

    /// <summary>A failure, styled as one by the implementation.</summary>
    void WriteError(string message);
}
