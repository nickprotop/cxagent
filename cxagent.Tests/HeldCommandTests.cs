using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>A command typed while a turn runs waits in one slot and runs when the turn ends.</summary>
public class HeldCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "held-" + Guid.NewGuid().ToString("N"));
    public HeldCommandTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    private sealed record Wired(SessionManager Manager, Session Session, BufferedChatSink Observer, HeldProvider Model);

    // A MODEL WHOSE FIRST ANSWER WAITS. The mock answers at once, so a turn started by Submit can
    // end before the test types the command it means to hold — and the test then passes or fails by
    // scheduling. Waiting for Release makes "a turn is running" a fact rather than a race.
    private Wired Open(int blockOn = 1, bool throwOnRelease = false, bool failOnRelease = false)
    {
        var manager = SessionManager.Create(new AppPaths(_dir), buildGate: _ => PermissionGate.AllowAll);
        var observer = new BufferedChatSink();
        var model = new HeldProvider(blockOn, throwOnRelease) { FailOnRelease = failOnRelease };
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(model),
            new SessionPorts { Observer = observer, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return new Wired(manager, session, observer, model);
    }

    // BLOCKON NAMES WHICH CALL WAITS: btw needs a reply already published to fork from, so a test
    // entering it lets the first turn finish and holds the second. THROWONRELEASE ends the waiting
    // call with an OperationCanceledException nobody asked for — a timeout's shape — which reaches
    // the turn's cancellation exit without CancelTurn having emptied the slot first.
    private sealed class HeldProvider(int blockOn, bool throwOnRelease) : ILlmProvider
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public List<ChatMessage>? LastMessages { get; private set; }
        public int ChatCallCount => Volatile.Read(ref _calls);
        public string ProviderId => "held";
        public string DisplayName => "held";
        public string ModelId => "held";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;

        public void Release() => _release.TrySetResult();

        /// <summary>The waiting answer fails as a provider does — not a cancel, an error.</summary>
        public bool FailOnRelease { get; init; }

        public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            LastMessages = [.. messages];
            if (Interlocked.Increment(ref _calls) == blockOn)
            {
                await _release.Task.WaitAsync(ct);
                if (throwOnRelease) throw new OperationCanceledException("timed out");
                if (FailOnRelease) throw new HttpRequestException("the provider failed");
            }
            return new LlmResponse { Text = "ok", StopReason = "end_turn" };
        }

        // THE AGENT ALWAYS STREAMS (Agent.StreamTurnAsync), whatever SupportsStreaming says — so this
        // is the path a turn takes, and it must wait the same way. A stream that yields nothing ends
        // the first turn at once and no command is ever typed into a running one.
        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var answer = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(answer.Text, null, IsFinal: true, StopReason: answer.StopReason);
        }
    }

    private void CommandFile(string name, string text)
    {
        var folder = Path.Combine(_dir, ".cxagent", "commands");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".md"), text);
    }

    [Fact]
    public void ACommandTypedMidTurn_IsHeld_NotSteered()
    {
        CommandFile("commit", "Commit it.");
        var w = Open();
        using var _ = w.Manager;
        using var busy = w.Session.PretendBusyForTesting();

        Assert.IsType<Session.SubmitOutcome.Queued>(w.Session.Submit("/commit"));

        Assert.Equal("/commit", w.Session.HeldCommand);
        Assert.Null(w.Session.PendingSteer);
    }

    [Fact]
    public void ASecondHeldCommand_ReplacesTheFirst_AndSaysSo()
    {
        CommandFile("a", "A.");
        CommandFile("b", "B.");
        var w = Open();
        using var _ = w.Manager;
        using var busy = w.Session.PretendBusyForTesting();

        w.Session.Submit("/a");
        w.Session.Submit("/b");

        Assert.Equal("/b", w.Session.HeldCommand);
        Assert.Contains(w.Observer.Notices, n => n.Contains("/a") && n.Contains("not run"));
    }

    [Fact]
    public async Task TheHeldCommand_RunsWhenTheTurnEnds_ExpandedThen()
    {
        CommandFile("commit", "Commit: !`cat state.txt`");
        File.WriteAllText(Path.Combine(_dir, "state.txt"), "before");
        var w = Open();
        using var _ = w.Manager;

        var first = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("hello"));
        w.Session.Submit("/commit");
        File.WriteAllText(Path.Combine(_dir, "state.txt"), "after");
        w.Model.Release();
        await first.Turn;
        await WaitUntil(() => w.Model.ChatCallCount == 2 && !w.Session.IsBusy);

        Assert.Contains(w.Model.LastMessages!, m => m.Role == "user" && m.Content.Contains("Commit: after"));
        Assert.Null(w.Session.HeldCommand);
    }

    [Fact]
    public void ACancel_WithNothingSteered_HandsTheHeldCommandBack()
    {
        CommandFile("commit", "Commit it.");
        var w = Open();
        using var _ = w.Manager;
        string? back = null;
        w.Session.Cancelled += (text, _) => back = text;

        var first = w.Session.Submit("hello");
        var second = w.Session.Submit("/commit");
        var cancelled = w.Session.CancelTurn();

        // THE STATE ON FAILURE, NOT JUST THE VALUE: this passes thousands of times and has failed
        // once under a loaded full-suite run, and "expected /commit, got null" alone cannot say which
        // of the four ways the slot could have emptied was taken.
        Assert.True(back == "/commit",
            $"back={back ?? "null"}; first={first.GetType().Name}; second={second.GetType().Name}; "
            + $"cancelled={cancelled}; calls={w.Model.ChatCallCount}; "
            + $"notices=[{string.Join(" | ", w.Observer.Notices)}]; errors=[{string.Join(" | ", w.Observer.Errors)}]");
    }

    [Fact]
    public void ACancel_WithSteeredText_ReturnsTheText_AndNamesTheCommand()
    {
        CommandFile("commit", "Commit it.");
        var w = Open();
        using var _ = w.Manager;
        string? back = null;
        w.Session.Cancelled += (text, _) => back = text;

        w.Session.Submit("hello");
        w.Session.Submit("also this");
        w.Session.Submit("/commit");
        w.Session.CancelTurn();

        Assert.Equal("also this", back);
        Assert.Contains(w.Observer.Notices, n => n.Contains("/commit was not run"));
    }

    [Fact]
    public async Task AHeldCommandWhoseFileWentAway_SaysUnknown()
    {
        CommandFile("gone", "Gone.");
        var w = Open();
        using var _ = w.Manager;

        var first = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("hello"));
        w.Session.Submit("/gone");
        File.Delete(Path.Combine(_dir, ".cxagent", "commands", "gone.md"));
        w.Model.Release();
        await first.Turn;

        Assert.Contains(w.Observer.Notices, n => n.Contains("Unknown command '/gone'"));
    }

    [Fact]
    public void Clear_DropsTheHeldCommand()
    {
        CommandFile("commit", "Commit it.");
        var w = Open();
        using var _ = w.Manager;
        using (w.Session.PretendBusyForTesting()) w.Session.Submit("/commit");

        w.Session.ClearContext();

        Assert.Null(w.Session.HeldCommand);
        Assert.Contains(w.Observer.Notices, n => n.Contains("/commit was not run"));
    }

    [Fact]
    public void HeldChanged_ReportsTheSlot()
    {
        CommandFile("commit", "Commit it.");
        var w = Open();
        using var _ = w.Manager;
        var seen = new List<string?>();
        w.Session.HeldChanged += seen.Add;

        using (w.Session.PretendBusyForTesting()) w.Session.Submit("/commit");
        w.Session.ClearContext();

        Assert.Equal(["/commit", null], seen);
    }

    private sealed class AskedAside : IAsideObserver
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Asked { get; } = new();
        void IAsideObserver.Asked(int aside, string question) => Asked.Enqueue(question);
        public void Waiting(int aside, bool waiting) { }
        public void AnswerAppended(int aside, string text) { }
        public void LookedUp(int aside, string row) { }
        public void ThinkingAppended(int aside, string text) { }
        public void ThinkingEnded(int aside, CxAgent.Core.Llm.ThinkingSummary summary) { }
        public void Finished(int aside, AsideEnd end, string? detail) { }
    }

    [Fact]
    public async Task AHeldCommand_RunsInTheMainConversation_EvenInBtwMode()
    {
        CommandFile("commit", "Commit it.");
        var w = Open(blockOn: 2);
        using var _ = w.Manager;
        var aside = new AskedAside();
        w.Session.AsideObserver = aside;
        await Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("warm up")).Turn;

        var running = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("hello"));
        Assert.IsType<Session.SubmitOutcome.Queued>(w.Session.Submit("/commit"));
        w.Session.Btw("");
        Assert.True(w.Session.InAside);
        w.Model.Release();
        await running.Turn;
        await WaitUntil(() => w.Model.ChatCallCount == 3 && !w.Session.IsBusy);

        Assert.Contains(w.Session.Host!.Context.Messages, m => m.Role == "user" && m.Content.Contains("Commit it."));
        Assert.DoesNotContain("/commit", aside.Asked);
        Assert.Null(w.Session.HeldCommand);
    }

    // THE CHECK-THEN-HOLD RACE, STAGED. RunCommand and Send read IsBusy and then call Hold; a turn
    // can end between the two, its tail finding the slot still empty. Calling Hold on an idle session
    // is that moment exactly — the line arrives after the only code that would have run it.
    [Fact]
    public async Task ALineHeldAfterTheTurnEnded_RunsAtOnce()
    {
        CommandFile("commit", "Commit it.");
        var w = Open();
        using var _ = w.Manager;
        w.Model.Release();

        w.Session.Hold("/commit");
        await WaitUntil(() => w.Model.ChatCallCount == 1 && !w.Session.IsBusy);

        Assert.Null(w.Session.HeldCommand);
        Assert.Contains(w.Model.LastMessages!, m => m.Role == "user" && m.Content.Contains("Commit it."));
    }

    [Fact]
    public async Task ATurnCancelledWithoutCancelTurn_RunsNothingAfter_AndNamesTheHeldCommand()
    {
        CommandFile("commit", "Commit it.");
        var w = Open(throwOnRelease: true);
        using var _ = w.Manager;

        var running = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("hello"));
        w.Session.Submit("/commit");
        w.Model.Release();
        await running.Turn;

        Assert.Equal(SendOutcome.Cancelled, await running.Outcome);
        Assert.False(w.Session.IsBusy);
        Assert.Null(w.Session.HeldCommand);
        Assert.Equal(1, w.Model.ChatCallCount);
        Assert.Contains(w.Observer.Notices, n => n.Contains("/commit was not run"));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    /// <summary>Closing the session drops a held command, and says so, rather than leaving it to run nowhere.</summary>
    [Fact]
    public void Close_DropsTheHeldCommand_AndNamesIt()
    {
        CommandFile("commit", "Commit it.");
        var w = Open();
        using var _ = w.Manager;
        using (w.Session.PretendBusyForTesting()) w.Session.Submit("/commit");

        Assert.True(w.Manager.Close(w.Session));

        Assert.Null(w.Session.HeldCommand);
        Assert.Contains(w.Observer.Notices, n => n.Contains("/commit was not run"));
    }

    /// <summary>
    /// A held command that starts no turn — its file is gone — must not leave a plugin's queued goal
    /// waiting for some later, unrelated turn.
    /// </summary>
    [Fact]
    public async Task AHeldCommandThatStartsNoTurn_StillLetsAQueuedPluginGoalRun()
    {
        CommandFile("gone", "Gone.");
        var w = Open();
        using var _ = w.Manager;

        var first = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("hello"));
        w.Session.Submit("/gone");
        Assert.True(w.Session.Plugins.SubmitQueue.TryEnqueue("p", "the plugin's goal", out var refusal), refusal);
        File.Delete(Path.Combine(_dir, ".cxagent", "commands", "gone.md"));
        w.Model.Release();
        await first.Turn;

        await WaitUntil(() => w.Model.ChatCallCount == 2 && !w.Session.IsBusy);
        Assert.Contains(w.Model.LastMessages!, m => m.Role == "user" && m.Content.Contains("the plugin's goal"));
    }

    /// <summary>
    /// The TUI wipes its scrollback when it hears ContextCleared, so the "was not run" notice must be
    /// said AFTER that, or it is wiped with everything else.
    /// </summary>
    [Fact]
    public void Clear_NamesTheDroppedCommand_AfterTheClear()
    {
        CommandFile("commit", "Commit it.");
        var w = Open();
        using var _ = w.Manager;
        using (w.Session.PretendBusyForTesting()) w.Session.Submit("/commit");
        var noticesAtClear = -1;
        w.Session.Changed += kind =>
        {
            if (kind == SessionChangeKind.ContextCleared) noticesAtClear = w.Observer.Notices.Count;
        };

        w.Session.ClearContext();

        var said = w.Observer.Notices.ToList().FindIndex(n => n.Contains("/commit was not run"));
        Assert.True(said >= noticesAtClear, $"said at {said}, cleared at {noticesAtClear}");
    }

    /// <summary>A turn that fails still runs the command held behind it — the spec's first row.</summary>
    [Fact]
    public async Task AFailedTurn_StillRunsTheHeldCommand()
    {
        CommandFile("commit", "Commit it.");
        var w = Open(failOnRelease: true);
        using var _ = w.Manager;

        var first = Assert.IsType<Session.SubmitOutcome.Started>(w.Session.Submit("hello"));
        w.Session.Submit("/commit");
        w.Model.Release();
        await first.Turn;

        for (var i = 0; i < 300 && !(w.Model.ChatCallCount >= 2 && !w.Session.IsBusy); i++) await Task.Delay(10);
        Assert.True(w.Model.ChatCallCount >= 2 && !w.Session.IsBusy,
            $"calls={w.Model.ChatCallCount} busy={w.Session.IsBusy} held={w.Session.HeldCommand} "
            + $"notices=[{string.Join(" | ", w.Observer.Notices)}] errors=[{string.Join(" | ", w.Observer.Errors)}]");
        Assert.Contains(w.Model.LastMessages!, m => m.Role == "user" && m.Content == "Commit it.");
    }
}
