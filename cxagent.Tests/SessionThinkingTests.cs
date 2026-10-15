using CxAgent.Core.Commands;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The session's thinking level: applied at the next message, never inside a turn; checked against
/// the model once; saved for resume; and the classifier always at the lowest.
/// </summary>
public class SessionThinkingTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "session-thinking-" + Guid.NewGuid().ToString("N"));

    public SessionThinkingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private (SessionManager Manager, Session Session, BufferedChatSink Said) Wired(LevelledMock provider)
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var said = new BufferedChatSink();
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(provider),
            new SessionPorts { Observer = said, ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        return (manager, session, said);
    }

    private static async Task Turn(Session session, string text)
    {
        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit(text));
        await started.Turn;
    }

    [Fact]
    public async Task SetThinking_WhenIdle_AppliesToTheNextRequest()
    {
        var model = new LevelledMock(["low", "medium", "high"]);
        var (manager, session, _) = Wired(model);
        using var _m = manager;

        Assert.Equal(CommandStatus.Changed, session.SetThinking("high"));
        Assert.Equal("high", session.ThinkingLevel);
        await Turn(session, "hi");
        Assert.Equal("high", model.LastLevel);
    }

    // A LEVEL CHOSEN MID-TURN WAITS for the next message, and still applies after a failed turn.
    [Fact]
    public async Task SetThinking_WhileBusy_IsPending_AndAppliesAtTheNextMessage_EvenAfterAFailedTurn()
    {
        var model = new LevelledMock(["low", "high"]);
        var (manager, session, _) = Wired(model);
        using var _m = manager;

        model.State.Hold = new TaskCompletionSource();
        model.State.Throw = true;
        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("first"));
        while (model.State.Seen.IsEmpty) await Task.Delay(5);

        Assert.Equal(CommandStatus.Changed, session.SetThinking("low"));
        Assert.Equal("low", session.PendingThinking);
        Assert.Null(session.ThinkingLevel);

        model.State.Hold.SetResult();
        await started.Turn;
        Assert.All(model.State.Seen, level => Assert.Null(level));

        model.State.Throw = false;
        model.State.Hold = null;
        await Turn(session, "second");
        Assert.Equal("low", model.LastLevel);
        Assert.Null(session.PendingThinking);
    }

    [Fact]
    public void SetThinking_ALevelTheModelDoesNotOffer_IsRefused_NamingTheLevels()
    {
        var (manager, session, said) = Wired(new LevelledMock(["low", "high"]));
        using var _m = manager;

        Assert.Equal(CommandStatus.Refused, session.SetThinking("max"));
        Assert.Contains(said.Notices, n => n.Contains("low, high"));
        Assert.Null(session.ThinkingLevel);
    }

    [Fact]
    public void SetThinking_OnAModelWithNoLevels_SaysWhy()
    {
        var (manager, session, said) = Wired(new LevelledMock([]));
        using var _m = manager;

        Assert.Equal(CommandStatus.Reported, session.SetThinking("high"));
        Assert.Contains(said.Notices, n => n.Contains("no levels"));
    }

    [Fact]
    public void CycleThinking_StartsAtTheFirstLevel_StepsAndWraps()
    {
        var (manager, session, _) = Wired(new LevelledMock(["low", "medium", "high"]));
        using var _m = manager;

        var seen = new List<string?>();
        for (var i = 0; i < 4; i++) { session.CycleThinking(); seen.Add(session.ThinkingLevel); }
        Assert.Equal(["low", "medium", "high", "low"], seen);
    }

    // A CONFIGURED LEVEL THE MODEL REFUSES: warned once, then the model's default — never a 400.
    [Fact]
    public async Task AConfiguredLevelTheModelDoesNotOffer_WarnsOnce_AndFallsBackToTheDefault()
    {
        var model = (LevelledMock)new LevelledMock(["low", "high"]).WithThinking("max");
        var (manager, session, said) = Wired(model);
        using var _m = manager;

        await Turn(session, "one");
        await Turn(session, "two");

        Assert.Single(said.Errors.Concat(said.Notices), n => n.Contains("'max'"));
        Assert.All(model.State.Seen, level => Assert.Null(level));
    }

    [Fact]
    public void SetThinking_WithSave_AsksTheFrontEndToWriteIt()
    {
        var (manager, session, _) = Wired(new LevelledMock(["low", "high"]));
        using var _m = manager;
        string? saved = "unset";
        session.SaveThinkingDefault = level => { saved = level; return true; };

        session.SetThinking("high", save: true);
        Assert.Equal("high", saved);
    }

    [Fact]
    public void SetThinking_Default_ClearsTheLevel()
    {
        var (manager, session, _) = Wired(new LevelledMock(["low", "high"]));
        using var _m = manager;
        session.SetThinking("high");
        session.SetThinking(null);
        Assert.Null(session.ThinkingLevel);
    }

    [Fact]
    public void RestoreThinking_SetsTheLevel_WithoutANotice()
    {
        var (manager, session, said) = Wired(new LevelledMock(["low", "high"]));
        using var _m = manager;
        var before = said.Notices.Count;

        session.RestoreThinking("high");

        Assert.Equal("high", session.ThinkingLevel);
        Assert.Equal(before, said.Notices.Count);
    }

    [Fact]
    public async Task TheLevel_IsSavedWithTheSession()
    {
        var model = new LevelledMock(["low", "high"]);
        var (manager, session, _) = Wired(model);
        using var _m = manager;
        session.SetThinking("high");
        await Turn(session, "hi");

        var snapshot = new FolderSessionStore(new AppPaths(_dir)).LoadById(session.SessionId!);
        Assert.Equal("high", snapshot!.Thinking);
    }

    // SAVED WHEN IT CHANGES, not at the next turn: a level chosen after the last answer, then a
    // quit, otherwise comes back at the level before it.
    [Fact]
    public async Task ALevelChangedAfterTheLastTurn_IsSaved_WithoutAnotherTurn()
    {
        var (manager, session, _) = Wired(new LevelledMock(["low", "high"]));
        using var _m = manager;
        await Turn(session, "hi");

        session.SetThinking("high");

        var snapshot = new FolderSessionStore(new AppPaths(_dir)).LoadById(session.SessionId!);
        Assert.Equal("high", snapshot!.Thinking);
    }

    // AND THE EDIT MODE, which is saved by the same write and has the same gap.
    [Fact]
    public async Task AnEditModeChangedAfterTheLastTurn_IsSaved_WithoutAnotherTurn()
    {
        var (manager, session, _) = Wired(new LevelledMock(["low", "high"]));
        using var _m = manager;
        await Turn(session, "hi");

        session.SetMode(session.Mode with { Edits = EditMode.AcceptEdits });

        var snapshot = new FolderSessionStore(new AppPaths(_dir)).LoadById(session.SessionId!);
        Assert.Equal(EditMode.AcceptEdits, snapshot!.Edits);
    }

    // NOTHING SAVED FOR A SESSION WITH NOTHING IN IT: a folder written here would be offered for
    // resume, and resuming it would bring back an empty conversation.
    [Fact]
    public void ALevelChangedBeforeAnyTurn_SavesNoSession()
    {
        var (manager, session, _) = Wired(new LevelledMock(["low", "high"]));
        using var _m = manager;

        session.SetThinking("high");

        Assert.Null(new FolderSessionStore(new AppPaths(_dir)).LoadById(session.SessionId!));
    }

    // EVERY RESUME PASSES THE WIRE, and the wire restores the level the session was saved at.
    [Fact]
    public void Resume_RestoresTheSavedLevel()
    {
        var model = new LevelledMock(["low", "high"]);
        var (manager, session, said) = Wired(model);
        using var _m = manager;
        var ports = new SessionPorts { Observer = said, ToolObserver = new BufferedJobPanel() };

        manager.Resume(session, new SessionSnapshot("agent-1", [], 0, 0, DateTimeOffset.UtcNow, Thinking: "high"),
            rewire: () => manager.Open(session, ResolvedConfig.ForTesting(model), ports, AgentMode.Single));

        Assert.Equal("high", session.ThinkingLevel);
    }

    [Fact]
    public void ThinkCommand_ThroughSubmit_SetsTheLevel_AndOffersItInThePalette()
    {
        var (manager, session, _) = Wired(new LevelledMock(["low", "high"]));
        using var _m = manager;

        Assert.IsType<Session.SubmitOutcome.Handled>(session.Submit("/think high"));
        Assert.Equal("high", session.ThinkingLevel);
        var offered = session.Values(CompletionSets.ThinkingLevels);
        Assert.Equal(["low", "high", "default"], offered.Select(v => v.Name));
        Assert.Equal("in use", offered.Single(v => v.Name == "high").Summary);
    }

    /// <summary>A provider whose model never answers what it offers — a probe still in flight.</summary>
    private sealed class SilentModel : ILlmProvider, IThinkingProvider
    {
        private readonly TaskCompletionSource<ThinkingOptions> _never = new();
        public string ProviderId => "silent";
        public string DisplayName => "Silent";
        public string ModelId => "silent-1";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => true;
        public string? ThinkingLevel { get; init; }
        public ThinkingOptions? KnownThinkingOptions => null;
        public Task<ThinkingOptions> GetThinkingOptionsAsync(CancellationToken ct = default) => _never.Task;
        public ILlmProvider WithThinking(string? level) => new SilentModel { ThinkingLevel = level };
        public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct) =>
            Task.FromResult(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> m, List<ToolDefinition>? t,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new LlmStreamChunk("ok", null, IsFinal: false);
            yield return new LlmStreamChunk(null, null, IsFinal: true, new LlmUsage(), "end_turn");
        }
    }

    // NEVER WAITS ON A PROBE. These run on the UI thread, where waiting on a probe whose own
    // continuation needs that thread freezes the window for good.
    [Fact]
    public void BeforeTheModelHasSaid_SetThinkingAndThink_AnswerAtOnce_WithoutWaiting()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        using var _m = manager;
        var said = new BufferedChatSink();
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new SilentModel()),
            new SessionPorts { Observer = said, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);

        var answered = Task.Run(() =>
        {
            session.SetThinking("high");
            session.Think("");
        }).Wait(TimeSpan.FromSeconds(2));

        Assert.True(answered, "SetThinking/Think waited on a probe that has not answered");
        Assert.Contains(said.Notices, n => n.Contains("has not said"));
    }

    [Fact]
    public async Task AConfiguredLevel_BeforeTheModelHasSaid_DoesNotHoldUpTheTurn()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        using var _m = manager;
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new SilentModel { ThinkingLevel = "max" }),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        var turn = Task.Run(() => Turn(session, "hi"));
        Assert.True(await Task.WhenAny(turn, Task.Delay(2000)) == turn, "the first turn waited on a probe");
    }

    /// <summary>A model whose answer about its levels arrives when the test says.</summary>
    private sealed class LateModel : ILlmProvider, IThinkingProvider
    {
        public readonly TaskCompletionSource<ThinkingOptions> Answer = new();
        public int Asked;
        public string ProviderId => "late";
        public string DisplayName => "Late";
        public string ModelId => "late-1";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => true;
        public string? ThinkingLevel => null;
        public ThinkingOptions? KnownThinkingOptions => Answer.Task.IsCompleted ? Answer.Task.Result : null;
        public Task<ThinkingOptions> GetThinkingOptionsAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Asked);
            return Answer.Task;
        }
        public ILlmProvider WithThinking(string? level) => this;
        public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct) =>
            Task.FromResult(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> m, List<ToolDefinition>? t,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new LlmStreamChunk(null, null, IsFinal: true, new LlmUsage(), "end_turn");
        }
    }

    // "TRY AGAIN IN A MOMENT" MEANS ONE: asking while the model has not said starts the question,
    // and its answer redraws whatever shows the level.
    [Fact]
    public async Task F8BeforeTheModelHasSaid_AsksIt_AndAnnouncesTheAnswer()
    {
        var model = new LateModel();
        var manager = SessionManager.Create(new AppPaths(_dir));
        using var _m = manager;
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(model),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        var announced = new TaskCompletionSource();
        session.Changed += kind => { if (kind == SessionChangeKind.Thinking) announced.TrySetResult(); };

        var askedBefore = Volatile.Read(ref model.Asked);
        Assert.Equal(CommandStatus.Reported, session.CycleThinking());
        for (var i = 0; i < 200 && Volatile.Read(ref model.Asked) == askedBefore; i++) await Task.Delay(5);
        Assert.True(Volatile.Read(ref model.Asked) > askedBefore, "F8 did not ask the model");

        model.Answer.SetResult(new ThinkingOptions(["off", "on"]));
        Assert.True(await Task.WhenAny(announced.Task, Task.Delay(2000)) == announced.Task);
    }

    [Fact]
    public void TheClassifier_RunsAtTheLowestLevel()
    {
        var registry = ProviderRegistry.FromProviders(
            new Dictionary<string, ILlmProvider> { ["m"] = new LevelledMock(["off", "low"]) }, "m");
        var classifier = PermissionDecider.ClassifierFor("m", registry)!;
        Assert.Equal(ThinkingLevels.Lowest, ((IThinkingProvider)classifier.ProviderForTest).ThinkingLevel);
    }
}
