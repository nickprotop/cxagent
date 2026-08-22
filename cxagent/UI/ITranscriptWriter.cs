using CxAgent.Core.Commands;

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

    /// <summary>
    /// A line Core said, styled from its severity.
    ///
    /// <para>THE ONE OVERLOAD THAT DOES NOT TAKE MARKUP, because its caller is not the UI. Core hands
    /// over a <see cref="Message"/> — markdown text plus a tone — and this is where the tone becomes
    /// a colour. The UI's own callers keep <see cref="Write(string)"/>: they know what the transcript
    /// renders and they write it directly, which is a different contract from Core's.</para>
    /// </summary>
    void Write(Message message);
}
