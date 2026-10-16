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

    // A MESSAGE QUEUED DURING THE TURN IS THE NEXT MESSAGE. It runs as a later lap of the same
    // turn, which starts a new tool loop — so the level chosen meanwhile applies to it, not to the
    // message after it.
    [Fact]
    public async Task ALevelChosenMidTurn_AppliesToAMessageQueuedInThatTurn()
    {
        var model = new LevelledMock(["low", "high"]);
        var (manager, session, _) = Wired(model);
        using var _m = manager;

        model.State.Hold = new TaskCompletionSource();
        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("first"));
        while (model.State.Seen.IsEmpty) await Task.Delay(5);

        session.SetThinking("low");
        Assert.IsType<Session.SubmitOutcome.Queued>(session.Submit("second"));
        model.State.Hold.SetResult();
        await started.Turn;

        Assert.Equal([null, "low"], model.State.Seen);
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

    // THE LEVEL IN USE IS NOT A CHANGE: no new provider, so no rebuilt prompt cache and no notice
    // that something was set.
    [Fact]
    public void SetThinking_TheLevelInUse_KeepsTheProvider_AndSaysSo()
    {
        var (manager, session, said) = Wired(new LevelledMock(["low", "high"]));
        using var _m = manager;
        session.SetThinking("high");
        var provider = session.Provider;

        Assert.Equal(CommandStatus.Reported, session.SetThinking("high"));

        Assert.Same(provider, session.Provider);
        Assert.Equal("Thinking is already high.", said.Notices[^1]);
    }

    // CHOSEN BACK MID-TURN: picking a level and then the one in use leaves nothing pending.
    [Fact]
    public async Task ChoosingTheLevelInUseMidTurn_LeavesNothingPending()
    {
        var model = new LevelledMock(["low", "high"]);
        var (manager, session, said) = Wired(model);
        using var _m = manager;

        model.State.Hold = new TaskCompletionSource();
        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("first"));
        while (model.State.Seen.IsEmpty) await Task.Delay(5);
        var provider = session.Provider;

        session.SetThinking("low");
        session.SetThinking(null);
        Assert.Null(session.PendingThinking);

        model.State.Hold.SetResult();
        await started.Turn;
        Assert.Same(provider, session.Provider);
        Assert.DoesNotContain(said.Notices, n => n.StartsWith("Thinking set to", StringComparison.Ordinal));
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

    // A SESSION WIRED OVER A REAL PROVIDER LIST, the one /model and a new tab read from.
    private (SessionManager Manager, Session Session) WiredWithCatalog(LevelledMock provider)
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var catalog = ProviderRegistry.FromProviders(new Dictionary<string, ILlmProvider> { ["test"] = provider }, "test");
        var resolution = ResolvedConfig.ForTesting(provider).WithCatalog(new ProviderCatalog(Instances: catalog));
        var session = manager.Open(_dir, resolution,
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        session.SaveThinkingDefault = _ => true;
        return (manager, session);
    }

    private static string? CatalogLevel(Session session) =>
        ((IThinkingProvider)session.Resolution!.Providers!.Use("test")!.Provider).ThinkingLevel;

    // --save IS THE NEW DEFAULT AT ONCE: /model back to this instance, in any tab, gets it.
    [Fact]
    public void ThinkWithSave_ChangesTheProviderList()
    {
        var (manager, session) = WiredWithCatalog(new LevelledMock(["low", "high"]));
        using var _m = manager;

        session.SetThinking("high", save: true);

        Assert.Equal("high", CatalogLevel(session));
    }

    // WITHOUT --save, THIS SESSION ONLY.
    [Fact]
    public void ThinkWithoutSave_LeavesTheProviderList()
    {
        var (manager, session) = WiredWithCatalog(new LevelledMock(["low", "high"]));
        using var _m = manager;

        session.SetThinking("high");

        Assert.Null(CatalogLevel(session));
    }

    // A NEW TAB IS A NEW SESSION, and starts at the saved default — not at a level this tab chose for
    // itself.
    [Fact]
    public void ANewSession_StartsAtTheSavedDefault_NotThisSessionsLevel()
    {
        var (manager, session) = WiredWithCatalog(new LevelledMock(["low", "high"]));
        using var _m = manager;

        session.SetThinking("high");
        Assert.Null(((IThinkingProvider)session.NewSessionResolution!.Model!.Provider).ThinkingLevel);

        session.SetThinking("low", save: true);
        Assert.Equal("low", ((IThinkingProvider)session.NewSessionResolution!.Model!.Provider).ThinkingLevel);
    }

    // "DEFAULT" IS A CHOICE, saved as one: null in the header means nothing was saved, and a resume
    // would then come back at the config's level instead of the model's own.
    [Fact]
    public async Task TheModelsDefault_IsSavedAsDefault_NotAsNothing()
    {
        var model = (LevelledMock)new LevelledMock(["low", "high"]).WithThinking("high");
        var (manager, session, _) = Wired(model);
        using var _m = manager;
        session.SetThinking(null);
        await Turn(session, "hi");

        var snapshot = new FolderSessionStore(new AppPaths(_dir)).LoadById(session.SessionId!);
        Assert.Equal("default", snapshot!.Thinking);
    }

    [Fact]
    public void Resume_OfASessionLeftAtDefault_ComesBackAtDefault()
    {
        var model = (LevelledMock)new LevelledMock(["low", "high"]).WithThinking("high");
        var (manager, session, said) = Wired(model);
        using var _m = manager;
        var ports = new SessionPorts { Observer = said, ToolObserver = new BufferedJobPanel() };

        manager.Resume(session, new SessionSnapshot("agent-1", [], 0, 0, DateTimeOffset.UtcNow, Thinking: "default"),
            rewire: () => manager.Open(session, ResolvedConfig.ForTesting(model), ports, AgentMode.Single));

        Assert.Null(session.ThinkingLevel);
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
