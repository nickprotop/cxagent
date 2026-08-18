using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Text left over when a turn ends starts another one, inside the same Send.
///
/// <para>WHAT THIS REPLACED. The drain was the tail of an <c>async void</c> method in the composition
/// root that ended by writing into the composer and calling SubmitComposer — so the queue drained by
/// RECURSION THROUGH THE UI, and no single layer showed the cycle. It is a loop in Session now.</para>
///
/// <para>A FALLBACK, NOT THE MAIN PATH: a correction typed mid-turn is normally taken by the turn
/// itself at its next tool barrier. What reaches the loop is text typed after the LAST barrier, which
/// is what these tests arrange by queueing while a turn with no tool calls is in flight.</para>
/// </summary>
public class DrainLoopTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "drain-" + Guid.NewGuid().ToString("N"));

    public DrainLoopTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    /// <summary>Records every prompt the model is asked, and lets the test steer mid-flight.</summary>
    private sealed class RecordingProvider : ILlmProvider
    {
        public List<string> Prompts { get; } = [];
        public Action? OnCall { get; set; }

        public string ProviderId => "rec";
        public string ModelId => "rec-model";
        public string DisplayName => "Rec";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            lock (Prompts)
                Prompts.Add(messages.LastOrDefault(m => m.Role == "user")?.Content ?? "");

            OnCall?.Invoke();
            return Task.FromResult(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, null, true);
        }
    }

    private Session Wired(ILlmProvider provider, out SessionManager manager)
    {
        manager = SessionManager.Create(new AppPaths(_dir));
        return manager.Open(_dir, ResolvedConfig.ForTesting(provider),
            new SessionPorts { Observer = new BufferedChatSink(), Tools = new BufferedJobPanel() },
            AgentMode.Single);
    }

    // THE CORE CLAIM: text queued during the turn goes out as a second turn, from the same Send.
    [Fact]
    public async Task TextQueuedDuringATurn_IsSentWhenItEnds()
    {
        var provider = new RecordingProvider();
        var session = Wired(provider, out var manager);
        using var _ = manager;

        // Queue from inside the provider call — that is "after the last barrier", the only text the
        // loop is responsible for.
        provider.OnCall = () => { provider.OnCall = null; session.Steer("the follow-up"); };

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("the first"));
        await started.Turn;

        Assert.Equal(["the first", "the follow-up"], provider.Prompts);
        Assert.Null(session.PendingSteer);
    }

    // TWO QUEUED LINES ARRIVE AS ONE PROMPT, newline-joined — the D18 rule, which the loop must keep:
    // two messages are usually one thought completed, and splitting them discards half.
    [Fact]
    public async Task TwoQueuedLines_ArriveJoined_AsOneFollowUpTurn()
    {
        var provider = new RecordingProvider();
        var session = Wired(provider, out var manager);
        using var _ = manager;

        provider.OnCall = () =>
        {
            provider.OnCall = null;
            session.Steer("first line");
            session.Steer("second line");
        };

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("go"));
        await started.Turn;

        Assert.Equal(["go", "first line\nsecond line"], provider.Prompts);
    }

    // AND IT TERMINATES. Nothing queued means one turn — a loop that re-entered on an empty queue
    // would spin forever, which is the failure a naive `while (true)` invites.
    [Fact]
    public async Task NothingQueued_RunsExactlyOneTurn()
    {
        var provider = new RecordingProvider();
        var session = Wired(provider, out var manager);
        using var _ = manager;

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("only this"));
        await started.Turn;

        Assert.Equal(["only this"], provider.Prompts);
    }

    // CANCELLING ENDS THE DRAIN AND HANDS THE TEXT BACK. Escape means the user changed their mind,
    // not that they confirmed what they typed — so the loop must not send it on the next lap.
    [Fact]
    public async Task CancellingDuringATurn_DoesNotSendWhatWasQueued()
    {
        var provider = new RecordingProvider();
        var session = Wired(provider, out var manager);
        using var _ = manager;

        string? handedBack = null;
        session.Cancelled += text => handedBack = text;

        provider.OnCall = () =>
        {
            provider.OnCall = null;
            session.Steer("never mind this");
            session.CancelTurn();
        };

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("go"));
        await started.Turn;

        Assert.Equal("never mind this", handedBack);
        Assert.DoesNotContain("never mind this", provider.Prompts);
    }
}
