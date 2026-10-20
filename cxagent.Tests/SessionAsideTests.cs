using CxAgent.Core.Agents;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using System.Collections.Concurrent;
using Xunit;

namespace CxAgent.Tests;

/// <summary>btw mode: what the user types goes to the aside until Esc, and nothing of it reaches the main agent.</summary>
public class SessionAsideTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sessaside-" + Guid.NewGuid().ToString("N"));

    public SessionAsideTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    private sealed class Seen : IAsideObserver
    {
        public ConcurrentQueue<string> Asked { get; } = new();
        public TaskCompletionSource<AsideEnd> Done { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset() => Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void IAsideObserver.Asked(int aside, string question) => Asked.Enqueue(question);
        public void Waiting(int aside, bool waiting) { }
        public void AnswerAppended(int aside, string text) { }
        public void LookedUp(int aside, string row) { }
        public void ThinkingAppended(int aside, string text) { }
        public void ThinkingEnded(int aside, CxAgent.Core.Llm.ThinkingSummary summary) { }
        public void Finished(int aside, AsideEnd end, string? detail) => Done.TrySetResult(end);
    }

    private (SessionManager Manager, Session Session, BufferedChatSink Said, MockLlmProvider Model, Seen Aside) Wired()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var sink = new BufferedChatSink();
        var model = new MockLlmProvider("m");
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(model),
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        var aside = new Seen();
        session.AsideObserver = aside;
        return (manager, session, sink, model, aside);
    }

    private static async Task Spoke(Session session, MockLlmProvider model, string text)
    {
        model.EnqueueResponse(new LlmResponse { Text = "main reply", StopReason = "end_turn" });
        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit(text));
        await started.Turn;
    }

    [Fact]
    public void Btw_BeforeAnyMessage_IsRefused()
    {
        var (manager, session, said, _, _) = Wired();
        using var _m = manager;

        Assert.True(manager.Commands.TryRun(session, "/btw what?"));

        Assert.False(session.InAside);
        Assert.Contains(said.Notices, line => line.Contains("nothing to ask about yet — send a message first"));
    }

    [Fact]
    public async Task Btw_EntersTheMode_AndAsks()
    {
        var (manager, session, _, model, aside) = Wired();
        using var _m = manager;
        await Spoke(session, model, "hello");
        model.EnqueueResponse(new LlmResponse { Text = "side", StopReason = "end_turn" });
        var changes = new List<SessionChangeKind>();
        session.Changed += changes.Add;

        Assert.True(manager.Commands.TryRun(session, "/btw why?"));

        Assert.True(session.InAside);
        Assert.Contains(SessionChangeKind.Aside, changes);
        Assert.Equal(AsideEnd.Answered, await aside.Done.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["why?"], aside.Asked.ToArray());
    }

    [Fact]
    public async Task BareBtw_EntersTheMode_AndWaitsForTheQuestion()
    {
        var (manager, session, _, model, aside) = Wired();
        using var _m = manager;
        await Spoke(session, model, "hello");

        Assert.True(manager.Commands.TryRun(session, "/btw"));

        Assert.True(session.InAside);
        Assert.Empty(aside.Asked);
    }

    /// <summary>Typed text goes to the aside; the main agent never sees it.</summary>
    [Fact]
    public async Task InTheMode_TheUsersTextGoesToTheAside_NotTheMainAgent()
    {
        var (manager, session, _, model, aside) = Wired();
        using var _m = manager;
        await Spoke(session, model, "hello");
        manager.Commands.TryRun(session, "/btw");
        var mainBefore = session.Host!.Context.Messages.Count;
        model.EnqueueResponse(new LlmResponse { Text = "side", StopReason = "end_turn" });

        var outcome = session.Submit("a private follow-up");

        Assert.IsType<Session.SubmitOutcome.Aside>(outcome);
        await aside.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(mainBefore, session.Host!.Context.Messages.Count);
        Assert.DoesNotContain(session.Host!.Context.Messages, m => m.Content.Contains("a private follow-up"));
    }

    /// <summary>A delivery waking the session is not the user typing; it goes to the main agent.</summary>
    [Fact]
    public async Task InTheMode_ASystemDelivery_StillReachesTheMainAgent()
    {
        var (manager, session, _, model, _) = Wired();
        using var _m = manager;
        await Spoke(session, model, "hello");
        manager.Commands.TryRun(session, "/btw");
        model.EnqueueResponse(new LlmResponse { Text = "noted", StopReason = "end_turn" });

        var outcome = session.Submit("job finished", source: Session.SubmitSource.System);

        Assert.IsType<Session.SubmitOutcome.Started>(outcome);
    }

    /// <summary>
    /// btw mode is a conversation and nothing else: a slash is text for the aside, never a command.
    /// Esc returns to where commands run.
    /// </summary>
    [Fact]
    public async Task InTheMode_ASlashIsTextForTheAside_NotACommand()
    {
        var (manager, session, _, model, aside) = Wired();
        using var _m = manager;
        await Spoke(session, model, "hello");
        manager.Commands.TryRun(session, "/btw");
        var mainBefore = session.Host!.Context.Messages.Count;
        model.EnqueueResponse(new LlmResponse { Text = "it wipes the context", StopReason = "end_turn" });

        var outcome = session.Submit("/clear what does this do?");

        Assert.IsType<Session.SubmitOutcome.Aside>(outcome);
        await aside.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["/clear what does this do?"], aside.Asked.ToArray());
        Assert.True(session.InAside);
        Assert.Equal(mainBefore, session.Host!.Context.Messages.Count);
    }

    [Fact]
    public async Task LeaveAside_EndsTheMode()
    {
        var (manager, session, _, model, _) = Wired();
        using var _m = manager;
        await Spoke(session, model, "hello");
        manager.Commands.TryRun(session, "/btw");

        session.LeaveAside();

        Assert.False(session.InAside);
        await Spoke(session, model, "back to main");
    }

    /// <summary>A second /btw stops the first answer before the new aside opens.</summary>
    [Fact]
    public async Task BtwInTheMode_StopsTheOldAside_AndStartsANewOne()
    {
        var (manager, session, _, model, aside) = Wired();
        using var _m = manager;
        await Spoke(session, model, "hello");
        manager.Commands.TryRun(session, "/btw");
        model.EnqueueResponse(new LlmResponse { Text = "two", StopReason = "end_turn" });

        Assert.True(manager.Commands.TryRun(session, "/btw second"));

        Assert.True(session.InAside);
        Assert.Equal(AsideEnd.Answered, await aside.Done.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["second"], aside.Asked.ToArray());
    }

    [Fact]
    public async Task TheMainTurnEnding_DoesNotEndTheMode()
    {
        var (manager, session, _, model, _) = Wired();
        using var _m = manager;
        await Spoke(session, model, "hello");
        manager.Commands.TryRun(session, "/btw");

        model.EnqueueResponse(new LlmResponse { Text = "x", StopReason = "end_turn" });
        var started = Assert.IsType<Session.SubmitOutcome.Started>(
            session.Submit("from a plugin", source: Session.SubmitSource.System));
        await started.Turn;

        Assert.True(session.InAside);
    }

    /// <summary>A cleared conversation has nothing to ask about, and btw mode ends with it.</summary>
    [Fact]
    public async Task Clear_EndsBtwMode_AndTheClearedConversationCannotBeForked()
    {
        var (manager, session, said, model, _) = Wired();
        using var _m = manager;
        await Spoke(session, model, "hello");
        manager.Commands.TryRun(session, "/btw");

        manager.Commands.TryRun(session, "/clear");

        Assert.False(session.InAside);
        manager.Commands.TryRun(session, "/btw what was that?");
        Assert.False(session.InAside);
        Assert.Contains(said.Notices, line => line.Contains("nothing to ask about yet"));
    }

    /// <summary>A closed session's aside must not keep calling the model for a tab that is gone.</summary>
    [Fact]
    public async Task Close_EndsBtwMode()
    {
        var (manager, session, _, model, _) = Wired();
        using var _m = manager;
        await Spoke(session, model, "hello");
        manager.Commands.TryRun(session, "/btw");

        Assert.True(manager.Close(session));

        Assert.False(session.InAside);
    }

    /// <summary>A new host (a /model switch) is a new agent; the old aside's thread belongs to the old one.</summary>
    [Fact]
    public async Task ReplacingTheHost_EndsBtwMode()
    {
        var (manager, session, _, model, _) = Wired();
        using var _m = manager;
        await Spoke(session, model, "hello");
        manager.Commands.TryRun(session, "/btw");

        session.ReplaceHost(
            new AgentHost(new AgentHost.AgentRuntime { Provider = model, Tools = new AgentTools(session.Executors!) },
                new RecordingSink(), new NullJobPanel()),
            model, null, session.Executors!);

        Assert.False(session.InAside);
    }

    [Fact]
    public void Btw_IsInTheCommandTable_AndNeedsAModel()
    {
        var btw = Assert.Single(SessionCommands.All, c => c.Name == "/btw");
        Assert.True(btw.NeedsModel);
    }
}
