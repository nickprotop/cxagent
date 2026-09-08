using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <see cref="Session.SubmitOutcome.Started.Result"/>: the turn's final assistant text, carried
/// beside the plain <c>Turn</c> that most callers already await.
///
/// <para>THE FAILURE MODE THIS GUARDS AGAINST IS A HANG, NOT A WRONG VALUE. Result is backed by a
/// <c>TaskCompletionSource</c> that must complete on every exit from the turn loop — success,
/// cancellation, and a provider exception — or a caller that awaits it (a plugin submitting with
/// wantResult) waits forever. Each test here corresponds to one of those exits.</para>
/// </summary>
public class TurnResultTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "turn-result-" + Guid.NewGuid().ToString("N"));

    public TurnResultTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    /// <summary>A fake provider whose reply (or failure) is set per test.</summary>
    private sealed class RecordingProvider : ILlmProvider
    {
        public string? Reply { get; set; }
        public Exception? Throw { get; set; }

        public string ProviderId => "rec";
        public string ModelId => "rec-model";
        public string DisplayName => "Rec";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            if (Throw is not null) throw Throw;
            return Task.FromResult(new LlmResponse { Text = Reply ?? "", StopReason = "end_turn" });
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
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
    }

    [Fact]
    public async Task A_started_turn_yields_its_final_assistant_text()
    {
        var session = Wired(new RecordingProvider { Reply = "All 2983 passed." }, out _);

        var outcome = session.Submit("run the tests");

        var started = Assert.IsType<Session.SubmitOutcome.Started>(outcome);
        Assert.Equal("All 2983 passed.", await started.Result);
    }

    /// <summary>
    /// No exception, no hang: the model returning nothing is SendOutcome.Silent, not an error, and
    /// Result must complete with null rather than leave an awaiter hanging on an empty answer.
    /// </summary>
    [Fact]
    public async Task A_turn_with_no_assistant_text_completes_with_null()
    {
        var session = Wired(new RecordingProvider { Reply = "" }, out _);

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("say nothing"));

        Assert.Null(await started.Result);
    }

    /// <summary>
    /// THE HANG THIS TASK EXISTS TO PREVENT. A provider that throws is caught by RunTurnAsync's
    /// backstop (Session.Turn.cs), which reports the error through Say and returns — and must also
    /// complete Result, or a plugin awaiting it hangs on every failed turn.
    /// </summary>
    [Fact]
    public async Task A_failed_turn_completes_Result_instead_of_hanging()
    {
        var session = Wired(new RecordingProvider { Throw = new InvalidOperationException("boom") },
            out _);

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("do it"));

        // A timeout here, not an assertion failure, is the failure mode this test is written to
        // catch — see A_failed_turn_completes_Result_instead_of_hanging's summary.
        var completed = await Task.WhenAny(started.Result, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(started.Result, completed);
        Assert.Null(await started.Result);
    }

    /// <summary>
    /// CancelTurn() drives RunTurnAsync's OperationCanceledException path, the third exit besides
    /// success and failure. Same requirement: Result completes rather than hanging.
    /// </summary>
    [Fact]
    public async Task A_cancelled_turn_completes_Result_instead_of_hanging()
    {
        var gate = new TaskCompletionSource();
        var provider = new BlockingProvider(gate.Task);
        var session = Wired(provider, out _);

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("do it"));
        session.CancelTurn();
        gate.TrySetResult();

        var completed = await Task.WhenAny(started.Result, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(started.Result, completed);
        Assert.Null(await started.Result);
    }

    /// <summary>Blocks ChatAsync on a caller-controlled gate, so a test can cancel mid-call.</summary>
    private sealed class BlockingProvider(Task gate) : ILlmProvider
    {
        public string ProviderId => "block";
        public string ModelId => "block-model";
        public string DisplayName => "Block";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            using var reg = ct.Register(() => { });
            await gate.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            return new LlmResponse { Text = "unreachable", StopReason = "end_turn" };
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, null, true);
        }
    }
}
