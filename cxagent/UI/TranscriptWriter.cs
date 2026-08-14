using SharpConsoleUI;
using SharpConsoleUI.Controls;

namespace CxAgent.UI;

/// <summary>
/// The UI's own transcript writer, marshalling onto the UI thread.
///
/// <para>WAS <c>LatestChatSink</c>, a forwarder that implemented the session's <c>IChatSink</c> so the
/// permission gate could print before the real transcript sink existed. It solved for the SINK's
/// lifetime when the CONTROL's is what matters — the control is created with the window and never
/// replaced, so there is nothing to forward to and nothing to keep current.</para>
///
/// <para>It no longer implements a Core interface, which is the point: the UI prints to its own
/// surface rather than through the session's port.</para>
/// </summary>
public sealed class TranscriptWriter(ConsoleWindowSystem system, ChatTranscriptControl chat)
    : ITranscriptWriter
{
    public void Write(string markup) =>
        system.EnqueueOnUIThread(() => chat.AddMessage(ChatRole.System, markup));

    public void WriteError(string message) =>
        system.EnqueueOnUIThread(() => chat.AddMessage(ChatRole.System,
            $"[{ColorScheme.DangerMarkup}]{message}[/]"));
}
