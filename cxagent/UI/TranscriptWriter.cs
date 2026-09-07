using CxAgent.Core.Commands;
using SharpConsoleUI;
using SharpConsoleUI.Controls;

namespace CxAgent.UI;

/// <summary>
/// The UI's own transcript writer, marshalling onto the UI thread.
///
/// <para>WRITES STRAIGHT TO THE CONTROL, not through a forwarder that tracks whichever sink is
/// current. A forwarder solves for the SINK's lifetime when the CONTROL's is what matters — the
/// control is created with the window and never replaced, so there is nothing to forward to and
/// nothing to keep current.</para>
///
/// <para>It implements no Core interface, which is the point: the UI prints to its own surface
/// rather than through the session's port.</para>
/// </summary>
/// <remarks>
/// THE CONTROL IS RESOLVED PER WRITE, NOT CAPTURED. `MainWindow.Chat` is a PROPERTY over the active
/// tab, so passing it to a constructor evaluates it once — at startup, when only the first tab
/// exists — and pins this writer to that tab's transcript for the life of the process. One gate
/// serves every session and writes its denial echoes through here, so a captured control sent every
/// session's "denied: …" and "trusted this folder" into the FIRST session's history: a security
/// notice filed against a conversation that did not produce it, and missing from the one that did.
///
/// A delegate rather than a MainWindow reference, because what this needs is one control chosen at
/// the moment of writing, not a window to reach into.
/// </remarks>
public sealed class TranscriptWriter(ConsoleWindowSystem system, Func<ChatTranscriptControl> chat)
    : ITranscriptWriter
{
    // MARKUP, SAID PER MESSAGE. The System role renders markdown — which is what Core writes — and
    // this port's contract is markup: its name says so, and WriteError wraps a colour scope that the
    // markdown converter would put on screen as a literal "[red]". The per-message override keeps
    // both, rather than making one of the two writers wrong.
    public void Write(string markup) =>
        system.EnqueueOnUIThread(() =>
            ChatTranscriptSink.Post(chat(), new ChatTranscriptSink.SystemRow(markup, false)));

    public void WriteError(string message) =>
        system.EnqueueOnUIThread(() => ChatTranscriptSink.Post(chat(),
            new ChatTranscriptSink.SystemRow($"[{ColorScheme.DangerMarkup}]{message}[/]", false)));

    // SEVERITY BECOMES A COLOUR HERE, and nowhere earlier. Core says what a line MEANS; only the UI
    // knows what the transcript renders and which palette is live, so the gate's "denied: ..." picks
    // up caution and a fault picks up danger without Core having named either.
    //
    // Info goes through the markdown path unstyled — it is the common case, and wrapping every
    // ordinary notice in a colour scope would put a literal tag on screen for the System role.
    public void Write(Message message) => system.EnqueueOnUIThread(() =>
        ChatTranscriptSink.Post(chat(), new ChatTranscriptSink.SystemRow(message.Severity switch
        {
            Severity.Error => $"[{ColorScheme.DangerMarkup}]{message.Text}[/]",
            Severity.Warning => $"[{ColorScheme.CautionMarkup}]{message.Text}[/]",
            _ => message.Text,
        }, false)));
}
