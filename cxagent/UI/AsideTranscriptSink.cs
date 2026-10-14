using System.Text;
using CxAgent.Core.Agents;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using FwChatId = SharpConsoleUI.Controls.ChatMessageId;

namespace CxAgent.UI;

/// <summary>
/// Draws each aside as one block in the transcript: its questions, its answers, its lookups.
///
/// <para>MARSHALLED, like every sink: an aside answers on a pool thread, and the transcript is a
/// control.</para>
/// </summary>
public sealed class AsideTranscriptSink : IAsideObserver
{
    public const string Header = "aside · not in the agent's memory";

    private const string WaitingText = "waiting for the model";

    private readonly ChatTranscriptControl _chat;
    private readonly Action<Action, string> _onUiThread;

    // UI-THREAD ONLY: every touch is inside _onUiThread.
    private readonly Dictionary<int, FwChatId> _blocks = new();

    // EVERYTHING EACH BLOCK HOLDS, kept here because the transcript cannot read a message back, and
    // moving a block means re-adding it whole — see Asked.
    private readonly Dictionary<int, StringBuilder> _threads = new();

    public AsideTranscriptSink(ConsoleWindowSystem system, ChatTranscriptControl chat)
        : this(chat, system.EnqueueOnUIThread)
    {
    }

    private AsideTranscriptSink(ChatTranscriptControl chat, Action<Action, string> onUiThread)
    {
        _chat = chat;
        _onUiThread = onUiThread;
    }

    /// <summary>
    /// Runs each write inline. Public with a ForTest suffix because this assembly grants no
    /// InternalsVisibleTo, and a headless test cannot drain the UI queue a real sink writes through.
    /// </summary>
    public static AsideTranscriptSink ForTest(ChatTranscriptControl chat) =>
        new(chat, static (action, _) => action());

    /// <summary>What an aside's block holds, for a test: the transcript has no way to read it back.</summary>
    public string ThreadTextForTest(int aside) =>
        _threads.TryGetValue(aside, out var text) ? text.ToString() : "";

    // OPENED BY THE QUESTION, AND ONLY BY IT. Every later write goes to the block the question
    // opened; one that has gone — /clear wiped the transcript while the aside was finishing — is
    // dropped rather than re-created. A block holding only "stopped" under no question would be
    // noise, and appending to the vanished id throws on the UI thread.
    private FwChatId Place(int aside, string content)
    {
        var id = _chat.AddMessage(ChatRole.Error, content, Header);
        _chat.SetMessageRail(id, true);
        _blocks[aside] = id;
        return id;
    }

    private bool TryBlock(int aside, out FwChatId id) =>
        _blocks.TryGetValue(aside, out id) && _chat.MessageIds.Contains(id);

    private void Append(int aside, FwChatId id, string text)
    {
        _chat.Append(id, text);
        _threads[aside].Append(text);
    }

    private void Write(int aside, string? text, string label) =>
        _onUiThread(() =>
        {
            if (text is { Length: > 0 } && TryBlock(aside, out var id)) Append(aside, id, text);
        }, label);

    public void Asked(int aside, string question) =>
        _onUiThread(() =>
        {
            FwChatId id;
            if (!TryBlock(aside, out var open))
            {
                _threads[aside] = new StringBuilder();
                id = Place(aside, "");
            }
            else if (_chat.MessageIds[^1].Equals(open))
            {
                id = open;
            }
            else
            {
                // ONE BLOCK PER /btw, MOVED TO WHERE THE USER IS LOOKING. The main turn keeps adding
                // rows while the aside talks, and a follow-up appended to a block they have pushed up
                // answers out of sight. Moved whole, so the thread still reads as one conversation;
                // only at a question, never mid-answer, so a streaming answer never jumps.
                _chat.RemoveMessage(open);
                id = Place(aside, _threads[aside].ToString());
            }

            Append(aside, id, $"\n\n**› {ChatTranscriptSink.Escape(question)}**\n\n");
        }, "asideSink.Asked");

    public void Waiting(int aside, bool waiting) =>
        _onUiThread(() =>
        {
            if (!TryBlock(aside, out var id)) return;
            if (waiting) _chat.SetStatus(id, WaitingText);
            else _chat.ClearStatus(id);
        }, "asideSink.Waiting");

    public void AnswerAppended(int aside, string text) =>
        Write(aside, ChatTranscriptSink.Escape(text), "asideSink.AnswerAppended");

    public void LookedUp(int aside, string row) =>
        Write(aside, $"\n`{row.Replace("`", "'")}`\n", "asideSink.LookedUp");

    public void Finished(int aside, AsideEnd end, string? detail) =>
        Write(aside, end switch
        {
            AsideEnd.Stopped => "\n\n*stopped*",
            AsideEnd.Capped => "\n\n*answered at the lookup limit*",
            AsideEnd.Failed => $"\n\n*failed: {ChatTranscriptSink.Escape(detail ?? "the model did not answer")}*",
            _ => null,
        }, "asideSink.Finished");
}
