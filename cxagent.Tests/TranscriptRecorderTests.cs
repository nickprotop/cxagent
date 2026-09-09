using CxAgent.Core.Agents;
using CxAgent.Core.Commands;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a session was shown, written down and read back.
///
/// <para>THIS IS THE HALF THE STORE WAS MISSING. It had a schema and tests and no writer, so every
/// row it could return had been put there by a test — replay across a restart did not work, and the
/// piece that "writes and is never read" had become the piece that is never written.</para>
/// </summary>
public class TranscriptRecorderTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-recorder-" + Guid.NewGuid().ToString("N"));

    private readonly Lazy<FolderTranscriptStore> _store;

    public TranscriptRecorderTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new Lazy<FolderTranscriptStore>(() => new FolderTranscriptStore(new AppPaths(_dir)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// A recorder for one session, with its folder bound.
    ///
    /// <para>THE BIND IS NOT CEREMONY: the store writes into the AGENT's directory while entries key
    /// on the SESSION, so a session it was never told about has nowhere to put anything and drops it
    /// silently. The composition root does this at ReplaceHost; here each session gets its own agent
    /// id so the tests exercise the same separation two live sessions have.</para>
    /// </summary>
    private TranscriptRecorder Recorder(string sessionId = "session-1")
    {
        _store.Value.BindAgent(sessionId, "agent-for-" + sessionId);
        return new(_store, sessionId);
    }

    /// <summary>
    /// ASSISTANT TEXT IS ONE ROW, NOT ONE PER TOKEN.
    ///
    /// <para>The whole reason the recorder buffers: `Append` REPLACES a row's body, so writing per
    /// token would rewrite the accumulated message on every one — quadratic in bytes for a long
    /// answer. The row appears when the message ends.</para>
    /// </summary>
    [Fact]
    public void StreamedTextIsCoalescedIntoOneRow()
    {
        var recorder = Recorder();
        var id = new ChatMessageId(1);

        recorder.AssistantTurnBegan(id);
        recorder.AssistantTextAppended(id, "Hello");
        recorder.AssistantTextAppended(id, ", ");
        recorder.AssistantTextAppended(id, "world");
        recorder.AssistantTurnEnded(id);

        var rows = _store.Value.Window("session-1");

        Assert.Single(rows);
        Assert.Equal("Hello, world", rows[0].Body);
        Assert.Equal("assistant", rows[0].Kind);
    }

    /// <summary>AND NOTHING IS WRITTEN BEFORE THE MESSAGE ENDS — a half-streamed message is not a
    /// transcript row, it is a message in progress.</summary>
    [Fact]
    public void AnUnfinishedMessageIsNotStored()
    {
        var recorder = Recorder();
        var id = new ChatMessageId(1);

        recorder.AssistantTurnBegan(id);
        recorder.AssistantTextAppended(id, "half a thought");

        Assert.Empty(_store.Value.Window("session-1"));
    }

    /// <summary>
    /// A TURN THAT SAID NOTHING WRITES NO ROW. One that only made tool calls ends with empty text,
    /// and a blank row would replay as an assistant message the user never saw.
    /// </summary>
    [Fact]
    public void AnEmptyMessageIsNotStored()
    {
        var recorder = Recorder();
        var id = new ChatMessageId(1);

        recorder.AssistantTurnBegan(id);
        recorder.AssistantTurnEnded(id);

        Assert.Empty(_store.Value.Window("session-1"));
    }

    /// <summary>THE USER'S OWN WORDS ARE WRITTEN AT ONCE — they are complete on arrival, and they
    /// are the half that cannot be regenerated.</summary>
    [Fact]
    public void AUserTurnIsStoredImmediately()
    {
        Recorder().UserTurnAdded(new ChatMessageId(1), "what is 2 + 2?");

        var rows = _store.Value.Window("session-1");

        Assert.Single(rows);
        Assert.Equal("user", rows[0].Kind);
        Assert.Equal("what is 2 + 2?", rows[0].Body);
    }

    /// <summary>
    /// AND A CONVERSATION READS BACK IN ORDER.
    ///
    /// <para>The property replay depends on: the session's own message ids are monotonic, so they
    /// serve as the store's sequence without a second counter to keep in step.</para>
    /// </summary>
    [Fact]
    public void AConversationReplaysInTheOrderItHappened()
    {
        var recorder = Recorder();

        recorder.UserTurnAdded(new ChatMessageId(1), "first question");

        var reply = new ChatMessageId(2);
        recorder.AssistantTurnBegan(reply);
        recorder.AssistantTextAppended(reply, "first answer");
        recorder.AssistantTurnEnded(reply);

        recorder.UserTurnAdded(new ChatMessageId(3), "second question");

        var rows = _store.Value.Window("session-1");

        Assert.Equal(["first question", "first answer", "second question"],
            rows.Select(r => r.Body));
    }

    /// <summary>
    /// TWO SESSIONS DO NOT SHARE A TRANSCRIPT, which is what keying on the session's own id buys —
    /// and why it is that id rather than the agent's, which a re-wire replaces.
    /// </summary>
    [Fact]
    public void EachSessionsRowsAreItsOwn()
    {
        Recorder("alpha").UserTurnAdded(new ChatMessageId(1), "alpha's question");
        Recorder("beta").UserTurnAdded(new ChatMessageId(1), "beta's question");

        Assert.Equal(["alpha's question"], _store.Value.Window("alpha").Select(r => r.Body));
        Assert.Equal(["beta's question"], _store.Value.Window("beta").Select(r => r.Body));
    }

    /// <summary>
    /// A SYSTEM NOTICE KEEPS ITS PLACE without colliding with a message id. `Said` carries no id, so
    /// notices are numbered downward from zero — out of the messages' space entirely.
    /// </summary>
    [Fact]
    public void SystemNoticesAreStoredWithoutCollidingWithMessages()
    {
        var recorder = Recorder();

        recorder.UserTurnAdded(new ChatMessageId(1), "a question");
        recorder.Said(new Message("something went wrong", Severity.Warning));
        recorder.Said(new Message("and again", Severity.Warning));

        var rows = _store.Value.Window("session-1");

        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows.Count(r => r.Kind == "system"));
    }

    /// <summary>
    /// REASONING IS NOT RECORDED. It is shown live because watching it is useful, and it is not part
    /// of what the next front end needs to reconstruct what was SAID — the same division the context
    /// makes, where reasoning does not survive into the next turn.
    /// </summary>
    [Fact]
    public void ReasoningIsNotStored()
    {
        var recorder = Recorder();
        var id = new ChatMessageId(1);

        recorder.AssistantTurnBegan(id);
        recorder.AssistantReasoningAppended(id, "let me think about this");
        recorder.AssistantTurnEnded(id);

        Assert.Empty(_store.Value.Window("session-1"));
    }

    /// <summary>
    /// AND THE STORE IS NOT OPENED UNTIL SOMETHING IS WRITTEN.
    ///
    /// <para>Constructing it creates the database, and a recorder is wired for EVERY session whether
    /// or not it says anything — so an eager one leaves a transcript.db behind for a process that
    /// recorded nothing.</para>
    /// </summary>
    [Fact]
    public void NothingIsCreatedUntilARowIsWritten()
    {
        _ = Recorder();

        Assert.False(File.Exists(new AppPaths(_dir).TranscriptPath));
    }
}

/// <summary>
/// That a session opened through the manager records without anyone asking it to.
///
/// <para>THE RECORDER TESTS ABOVE DRIVE IT DIRECTLY, which proves the writing and not the WIRING —
/// the same gap that let the store ship with a schema and no writer at all. This opens a real
/// session and reads its transcript back.</para>
/// </summary>
public class TranscriptWiringTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-twire-" + Guid.NewGuid().ToString("N"));

    public TranscriptWiringTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (SessionManager Manager, Session Session) Wired()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir,
            CxAgent.Core.Llm.ResolvedConfig.ForTesting(new CxAgent.Core.Llm.MockLlmProvider("m")),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        return (manager, session);
    }

    /// <summary>WHAT THE SESSION SAYS REACHES THE STORE, keyed by the session's own id.</summary>
    [Fact]
    public void AnOpenedSessionRecordsWhatItSays()
    {
        var (manager, session) = Wired();
        using var _ = manager;

        session.Observers!.Said(new Message("a system notice", Severity.Info));

        var rows = manager.Shared.Transcripts!.Value.Window(session.Id);

        Assert.Contains(rows, r => r.Body == "a system notice");
    }

    /// <summary>
    /// AND A RE-WIRE DOES NOT ADD A SECOND RECORDER.
    ///
    /// <para>`SessionFactory.Wire` runs again on every `/model`, resume and setup flow, and the
    /// fan-outs are deliberately KEPT across those — so subscribing unconditionally would leave one
    /// recorder per wire, each writing the same row. The store's upsert would hide that until
    /// somebody counted.</para>
    /// </summary>
    [Fact]
    public void AReWireDoesNotSubscribeASecondRecorder()
    {
        var (manager, session) = Wired();
        using var _ = manager;

        var before = session.Observers!.Count;

        // Re-open is what /model does: same session, wired again.
        manager.Open(session, CxAgent.Core.Llm.ResolvedConfig.ForTesting(new CxAgent.Core.Llm.MockLlmProvider("m2")),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        Assert.Equal(before, session.Observers!.Count);
    }
}
