using CxAgent.Core.Commands;
using SharpConsoleUI;
using SharpConsoleUI.Controls;

namespace CxAgent.UI;

/// <summary>
/// The UI's own transcript writer, marshalling onto the UI thread.
///
/// <para>WAS <c>LatestChatSink</c>, a forwarder that implemented the session's <c>ISessionObserver</c> so the
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
    // MARKUP, SAID PER MESSAGE. The System role renders markdown — which is what Core writes — and
    // this port's contract is markup: its name says so, and WriteError wraps a colour scope that the
    // markdown converter would put on screen as a literal "[red]". The per-message override keeps
    // both, rather than making one of the two writers wrong.
    public void Write(string markup) =>
        system.EnqueueOnUIThread(() =>
            ChatTranscriptSink.Post(chat, new ChatTranscriptSink.SystemRow(markup, false)));

    public void WriteError(string message) =>
        system.EnqueueOnUIThread(() => ChatTranscriptSink.Post(chat,
            new ChatTranscriptSink.SystemRow($"[{ColorScheme.DangerMarkup}]{message}[/]", false)));

    // SEVERITY BECOMES A COLOUR HERE, and nowhere earlier. Core says what a line MEANS; only the UI
    // knows what the transcript renders and which palette is live, so the gate's "denied: ..." picks
    // up caution and a fault picks up danger without Core having named either.
    //
    // Info goes through the markdown path unstyled — it is the common case, and wrapping every
    // ordinary notice in a colour scope would put a literal tag on screen for the System role.
    public void Write(Message message) => system.EnqueueOnUIThread(() =>
        ChatTranscriptSink.Post(chat, new ChatTranscriptSink.SystemRow(message.Severity switch
        {
            Severity.Error => $"[{ColorScheme.DangerMarkup}]{message.Text}[/]",
            Severity.Warning => $"[{ColorScheme.CautionMarkup}]{message.Text}[/]",
            _ => message.Text,
        }, false)));
}
