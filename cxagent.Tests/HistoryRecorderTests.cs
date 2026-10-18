using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Config;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// history.jsonl — every model message once, in order, whatever compaction, /clear or a rewrite does to the context.
/// </summary>
public class HistoryRecorderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("history-").FullName;
    private string PathOf => Path.Combine(_dir, "history.jsonl");
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ChatMessage User(string text) => new() { Role = "user", Content = text, Timestamp = DateTimeOffset.UtcNow };
    private static ChatMessage Assistant(string text) => new() { Role = "assistant", Content = text, Timestamp = DateTimeOffset.UtcNow };
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);
    private static ChatMessage User(string text, int minute) => new() { Role = "user", Content = text, Timestamp = T0.AddMinutes(minute) };
    private static ChatMessage Assistant(string text, int minute) => new() { Role = "assistant", Content = text, Timestamp = T0.AddMinutes(minute) };

    private IReadOnlyList<HistoryLine> Lines() => HistoryRecorder.Read(PathOf);

    [Fact]
    public void EachMessage_IsWrittenOnce_InOrder()
    {
        var recorder = new HistoryRecorder(PathOf);
        var context = new List<ChatMessage> { User("one") };
        recorder.Record(context);
        context.Add(Assistant("two"));
        recorder.Record(context);
        recorder.Record(context);

        Assert.Equal(["one", "two"], Lines().Select(l => l.Message!.Content));
    }

    /// <summary>A RESEND IS NEW: its twin is still in the list, so it is not a rewrite.</summary>
    [Fact]
    public void TheSameTextTwice_IsRecordedTwice()
    {
        var recorder = new HistoryRecorder(PathOf);
        var context = new List<ChatMessage> { User("continue"), Assistant("ok") };
        recorder.Record(context);
        context.Add(User("continue"));
        recorder.Record(context);

        Assert.Equal(2, Lines().Count(l => l.Message!.Content == "continue"));
    }

    /// <summary>A REWRITE IS NOT NEW: DropThinkingBlocks and an unchanged task list replace the object, not the message.</summary>
    [Fact]
    public void ARewrittenMessage_IsNotRecordedAgain()
    {
        var recorder = new HistoryRecorder(PathOf);
        var answer = Assistant("two");
        var context = new List<ChatMessage> { User("one"), answer };
        recorder.Record(context);
        context[1] = answer with { ThinkingBlocks = null };
        recorder.Record(context);

        Assert.Equal(2, Lines().Count);
    }

    /// <summary>COMPACTION DROPS MESSAGES FROM THE CONTEXT, NEVER FROM THE RECORD; the summary is marked.</summary>
    [Fact]
    public void Compaction_KeepsWhatItDropped_AndMarksTheSummary()
    {
        var recorder = new HistoryRecorder(PathOf);
        var system = new ChatMessage { Role = "system", Content = "sys" };
        var context = new List<ChatMessage> { system, User("a"), Assistant("b"), User("c") };
        recorder.Record(context);
        context.RemoveRange(1, 2);
        context.Insert(1, Assistant(CxAgent.Core.Sessions.SessionCompressor.SummaryMarker + "a then b]"));
        context.Add(Assistant("d"));
        recorder.Record(context);

        var kinds = Lines().Select(l => l.Kind == HistoryLine.Kinds.Message ? l.Message!.Content : l.Kind).ToList();
        Assert.Equal(["sys", "a", "b", "c", HistoryLine.Kinds.Compacted,
            CxAgent.Core.Sessions.SessionCompressor.SummaryMarker + "a then b]", "d"], kinds);
    }

    [Fact]
    public void Thinking_IsKept_ThoughTheMessageNeverSavesIt()
    {
        var recorder = new HistoryRecorder(PathOf);
        var answer = Assistant("x") with { ThinkingBlocks = [new CxAgent.Core.Llm.ThinkingBlock(false, "because", null, null)] };
        recorder.Record([answer]);

        Assert.Equal(["because"], Lines().Single().Thinking);
    }

    /// <summary>
    /// A MODEL THAT REASONS IN ITS TEXT — llama.cpp's &lt;think&gt; — returns no thinking blocks; its reasoning arrives as
    /// text, and is kept for the reply it preceded.
    /// </summary>
    [Fact]
    public void StreamedReasoning_IsKept_ForTheNextReply()
    {
        var recorder = new HistoryRecorder(PathOf);
        recorder.NoteThinking("weighing it up");
        recorder.Record([User("q"), Assistant("a")]);

        Assert.Null(Lines()[0].Thinking);
        Assert.Equal(["weighing it up"], Lines()[1].Thinking);
    }

    /// <summary>
    /// A RESEND IN THE SAME INTERVAL AS A COMPACTION IS STILL NEW: compaction drops the older "continue", and the new
    /// one must not be taken for a rewrite of it.
    /// </summary>
    [Fact]
    public void TheSameText_AfterACompactionDroppedItsTwin_IsRecorded()
    {
        var recorder = new HistoryRecorder(PathOf);
        var system = new ChatMessage { Role = "system", Content = "sys" };
        var context = new List<ChatMessage> { system, User("continue", 1), Assistant("ok", 2) };
        recorder.Record(context);

        context.RemoveRange(1, 2);
        context.Insert(1, Assistant(CxAgent.Core.Sessions.SessionCompressor.SummaryMarker + "earlier]", 3));
        context.Add(User("continue", 4));
        recorder.Record(context);

        Assert.Equal(2, Lines().Count(l => l.Message?.Content == "continue"));
    }

    /// <summary>REASONING BELONGS TO ITS OWN REPLY: one that came as blocks uses up the noted text, so no later reply inherits it.</summary>
    [Fact]
    public void NotedReasoning_NeverLandsOnALaterReply()
    {
        var recorder = new HistoryRecorder(PathOf);
        var withBlocks = Assistant("first", 1) with { ThinkingBlocks = [new CxAgent.Core.Llm.ThinkingBlock(false, "block thought", null, null)] };
        recorder.NoteThinking("text thought");
        recorder.Record([User("q", 0), withBlocks]);
        recorder.Record([User("q", 0), withBlocks, Assistant("second", 2)]);
        recorder.NoteThinking("stale");
        recorder.NoteThinking(null);
        recorder.Record([User("q", 0), withBlocks, Assistant("second", 2), Assistant("third", 3)]);

        Assert.Null(Lines().Single(l => l.Message?.Content == "second").Thinking);
        Assert.Null(Lines().Single(l => l.Message?.Content == "third").Thinking);
    }

    /// <summary>
    /// A COMPACTION THAT COULD NOT SUMMARISE STILL SAYS SO: the fallback drops the oldest half with no summary, and the
    /// history marks where — a dropped task list is the one removal that is not a compaction.
    /// </summary>
    [Fact]
    public void ATruncationWithoutASummary_IsMarked_AndATaskListDroppedIsNot()
    {
        var recorder = new HistoryRecorder(PathOf);
        var plan = new ChatMessage { Role = "user", Content = "- [ ] step", IsTaskList = true, Timestamp = T0 };
        var context = new List<ChatMessage> { User("a", 1), Assistant("b", 2), plan };
        recorder.Record(context);
        context.Remove(plan);
        recorder.Record(context);
        Assert.DoesNotContain(Lines(), l => l.Kind == HistoryLine.Kinds.Compacted);

        context.RemoveRange(0, 2);
        context.Add(User("c", 3));
        recorder.Record(context);

        Assert.Equal([HistoryLine.Kinds.Compacted, "c"],
            Lines().Skip(3).Select(l => l.Kind == HistoryLine.Kinds.Message ? l.Message!.Content : l.Kind));
    }

    [Fact]
    public void AnIncomingLabel_GoesOnTheNextPrompt()
    {
        var recorder = new HistoryRecorder(PathOf);
        recorder.NoteIncoming(Incoming.Brief);
        recorder.Record([User("find the thing")]);

        Assert.Equal(Incoming.Brief, Lines().Single().From);
    }

    [Fact]
    public void Clear_AndResume_AreMarked_AndAResumeRewritesNothing()
    {
        var recorder = new HistoryRecorder(PathOf);
        var loaded = new List<ChatMessage> { User("old"), Assistant("old answer") };
        recorder.ContinuesFrom("PREVIOUSAGENT", loaded);
        recorder.Record(loaded);
        recorder.NoteCleared();
        recorder.Record([User("fresh")]);

        var lines = Lines();
        Assert.Equal(HistoryLine.Kinds.Continues, lines[0].Kind);
        Assert.Equal("PREVIOUSAGENT", lines[0].Previous);
        Assert.Equal([HistoryLine.Kinds.Continues, HistoryLine.Kinds.Cleared, HistoryLine.Kinds.Message],
            lines.Select(l => l.Kind));
    }

    [Fact]
    public void ATornLastLine_IsSkipped()
    {
        var recorder = new HistoryRecorder(PathOf);
        recorder.Record([User("whole")]);
        File.AppendAllText(PathOf, "{\"q\":2,\"at\":");

        Assert.Single(Lines());
    }
}

/// <summary>
/// history.jsonl from a live session: the session's agent, a sub-agent, /clear and a re-wire each write where an
/// export will read.
/// </summary>
public class HistoryWiringTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("history-wiring-").FullName;
    private SessionManager? _manager;
    private readonly BufferedJobPanel _panel = new();

    public void Dispose()
    {
        _manager?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static LlmResponse Answer(string text) => new() { Text = text, StopReason = "end_turn" };
    private static LlmResponse Use(params ToolCall[] calls) => new() { Text = "", StopReason = "tool_use", ToolCalls = [.. calls] };

    private (Session Session, ResolvedConfig Config, BufferedChatSink Sink) Open(ILlmProvider parent, ILlmProvider? child = null)
    {
        var providers = new Dictionary<string, ILlmProvider> { ["local"] = parent, ["kid"] = child ?? parent };
        var types = new Dictionary<string, AgentTypeConfig>();
        foreach (var name in BuiltinAgentTypes.All.Select(t => t.Name).Append(AgentTypeCatalog.DefaultTypeName))
            types[name] = new AgentTypeConfig("", Provider: "kid");
        var registry = ProviderRegistry.FromProviders(providers, "local");
        var config = new ResolvedConfig(registry.Use("local"), new ProviderCatalog(Instances: registry, AgentTypes: types), []);
        _manager = SessionManager.Create(new ProcessSetup { Paths = new AppPaths(_dir) });
        var sink = new BufferedChatSink();
        var session = _manager.Open(_dir, config, new SessionPorts { Observer = sink, ToolObserver = _panel },
            AgentMode.FanOut);
        return (session, config, sink);
    }

    private static async Task Turn(Session s, string text)
    {
        await Assert.IsType<Session.SubmitOutcome.Started>(s.Submit(text)).Turn;
        Assert.True(await Poll.Until(() => !s.IsBusy && !s.TurnOwned), "the session never went idle");
    }

    private string HistoryOf(string agentId) => Path.Combine(_dir, "logs", agentId, "history.jsonl");

    [Fact]
    public async Task ASession_RecordsItsTurns_WithTheUserLabelled()
    {
        var (s, _, _) = Open(new ScriptedProvider(Answer("first answer"), Answer("second answer")));
        await Turn(s, "first question");
        await Turn(s, "second question");

        var lines = HistoryRecorder.Read(HistoryOf(s.SessionId!)).Where(l => l.Message is { Role: "user" or "assistant" }).ToList();
        Assert.Equal(["first question", "first answer", "second question", "second answer"], lines.Select(l => l.Message!.Content));
        Assert.Equal(Incoming.You, lines[0].From);
    }

    [Fact]
    public async Task ASubAgent_RecordsItsOwnHistory_UnderItsParent_StartingWithItsBrief()
    {
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1", background: false)), Answer("done"));
        var (s, _, _) = Open(parent, new ScriptedProvider(Answer("found it")));
        await Turn(s, "go");

        var childDir = Assert.Single(Directory.GetDirectories(Path.Combine(_dir, "logs", s.SessionId!)));
        var lines = HistoryRecorder.Read(Path.Combine(childDir, "history.jsonl"));
        var brief = lines.First(l => l.Message is { Role: "user", ToolCallId: null });
        Assert.Equal("find the thing", brief.Message!.Content);
        Assert.Equal(Incoming.Brief, brief.From);
        Assert.Contains(lines, l => l.Message is { Role: "assistant", Content: "found it" });

        // THE SPAWN IS RECORDED WHEN IT WAS MADE, not when the next request went out after the child finished — or
        // a timeline would put the child's work before the call that started it.
        var spawn = HistoryRecorder.Read(HistoryOf(s.SessionId!)).First(l => l.Message?.ToolCalls is { Count: > 0 });
        Assert.True(spawn.At <= brief.At, $"spawn recorded at {spawn.At:O}, its brief at {brief.At:O}");
    }

    [Fact]
    public async Task Clear_IsMarked_AndWhatCameBeforeStays()
    {
        var (s, _, _) = Open(new ScriptedProvider(Answer("one"), Answer("two")));
        await Turn(s, "before");
        s.ClearContext();
        await Turn(s, "after");

        var kinds = HistoryRecorder.Read(HistoryOf(s.SessionId!))
            .Select(l => l.Kind == HistoryLine.Kinds.Message ? l.Message!.Content : l.Kind)
            .Where(k => k is "before" or "after" or HistoryLine.Kinds.Cleared).ToList();
        Assert.Equal(["before", HistoryLine.Kinds.Cleared, "after"], kinds);
    }

    /// <summary>/sessions export NAMES A SESSION AS /sessions DOES: a row number here, an id anywhere.</summary>
    [Fact]
    public async Task ExportFolderOf_FindsAListedSession_AndSaysWhenNothingMatches()
    {
        var (s, _, sink) = Open(new ScriptedProvider(Answer("one")));
        await Turn(s, "a question");
        s.SaveNow();

        Assert.Equal(s.SessionId, s.ExportFolderOf("1"));
        Assert.Equal(s.SessionId, s.ExportFolderOf(s.SessionId![..8]));
        Assert.Null(s.ExportFolderOf("ZZZZZZZZ"));
        Assert.Contains(sink.Notices, n => n.Contains("No session matches"));
    }

    /// <summary>A NUDGE IS LABELLED AS ONE: cxagent asking the model to continue is not the user speaking.</summary>
    [Fact]
    public async Task AReplyCutOffAtTheLimit_IsFollowedByALabelledNudge()
    {
        var (s, _, _) = Open(new ScriptedProvider(
            new LlmResponse { Text = "partial", StopReason = "max_tokens" }, Answer("the rest")));
        await Turn(s, "write it");

        var users = HistoryRecorder.Read(HistoryOf(s.SessionId!)).Where(l => l.Message is { Role: "user", ToolCallId: null }).ToList();
        Assert.Equal(Incoming.You, users[0].From);
        Assert.Contains(users, l => l.From == Incoming.Nudge && l.Message!.Content.Contains("cut off"));
    }

    /// <summary>
    /// A NOTE LEFT IN THE MAILBOX IS NOT THE USER'S PROMPT: drained ahead of the prompt with no sender of its own, it
    /// must not take the label the prompt was given.
    /// </summary>
    [Fact]
    public async Task ANoteDrainedBeforeAPrompt_DoesNotTakeThePromptsLabel()
    {
        var (s, _, _) = Open(new ScriptedProvider(Answer("ok")));
        Assert.True(s.LeaveNote("a background command finished"));
        await Turn(s, "the question");

        var users = HistoryRecorder.Read(HistoryOf(s.SessionId!)).Where(l => l.Message is { Role: "user", ToolCallId: null }).ToList();
        Assert.Equal(Incoming.Delivery, users.Single(l => l.Message!.Content.Contains("finished")).From);
        Assert.Equal(Incoming.You, users.Single(l => l.Message!.Content == "the question").From);
    }

    /// <summary>
    /// THE COMPACTION'S ROW SHOWS ITS SUMMARY, not the system prompt pinned ahead of it — the first message is the
    /// system prompt, so reading "the first message" put the whole prompt in the transcript.
    /// </summary>
    [Fact]
    public async Task ACompactionsRow_ShowsTheSummary()
    {
        var (s, _, _) = Open(new ScriptedProvider(Answer("one"), Answer("two"), Answer("they asked twice")));
        await Turn(s, "first");
        await Turn(s, "second");
        await s.CompressNow(CancellationToken.None)!;

        var row = _panel.Jobs.Single(j => j.JobType == "compress");
        var body = row.Result!.Output["content"]!.ToString()!;
        Assert.Contains("they asked twice", body);
        Assert.DoesNotContain("You are cxagent", body);
    }

    /// <summary>/compress IS RECORDED AS IT HAPPENS: a model switch right after it must not lose the summary and its marker.</summary>
    [Fact]
    public async Task ACompressThenARewire_KeepsTheSummaryInTheHistory()
    {
        var (s, config, sink) = Open(new ScriptedProvider(Answer("one"), Answer("two"), Answer("they asked twice")));
        await Turn(s, "first");
        await Turn(s, "second");
        var before = s.SessionId!;
        await s.CompressNow(CancellationToken.None)!;
        Assert.True(s.CarryToNextWire());
        _manager!.Open(s, config, new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() }, s.Mode);

        var lines = HistoryRecorder.Read(HistoryOf(before));
        Assert.Contains(lines, l => l.Kind == HistoryLine.Kinds.Compacted);
        Assert.Contains(lines, l => l.Message?.Content.Contains("they asked twice") == true);
    }

    /// <summary>A RE-WIRE AFTER /clear STILL CONTINUES THE HISTORY: the context is empty, the conversation before it is not gone.</summary>
    [Fact]
    public async Task ARewireAfterClear_StillContinuesTheHistory()
    {
        var (s, config, sink) = Open(new ScriptedProvider(Answer("one")));
        await Turn(s, "before");
        var before = s.SessionId!;
        s.ClearContext();
        Assert.True(s.CarryToNextWire());
        _manager!.Open(s, config, new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() }, s.Mode);

        var first = HistoryRecorder.Read(HistoryOf(s.SessionId!)).FirstOrDefault();
        Assert.Equal(HistoryLine.Kinds.Continues, first?.Kind);
        Assert.Equal(before, first?.Previous);
    }

    /// <summary>A RE-WIRE IS A NEW AGENT CARRYING THE CONVERSATION: its history names the old one and repeats nothing.</summary>
    [Fact]
    public async Task ARewire_ContinuesTheOldHistory_WithoutRepeatingIt()
    {
        var (s, config, sink) = Open(new ScriptedProvider(Answer("one"), Answer("two")));
        await Turn(s, "before");
        var before = s.SessionId!;
        Assert.True(s.CarryToNextWire());
        _manager!.Open(s, config, new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() }, s.Mode);
        Assert.NotEqual(before, s.SessionId);
        await Turn(s, "after");

        var lines = HistoryRecorder.Read(HistoryOf(s.SessionId!));
        Assert.Equal(HistoryLine.Kinds.Continues, lines[0].Kind);
        Assert.Equal(before, lines[0].Previous);
        Assert.DoesNotContain(lines, l => l.Message?.Content == "before");
        Assert.Contains(lines, l => l.Message?.Content == "after");
    }
}
