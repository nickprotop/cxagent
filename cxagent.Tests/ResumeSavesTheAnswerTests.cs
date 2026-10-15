using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The saved session holds the whole exchange, the final answer included — so a resumed session
/// (the TUI's --resume, a headless -c) remembers what it was last told, not only what it last asked.
/// </summary>
public class ResumeSavesTheAnswerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "resans-" + Guid.NewGuid().ToString("N"));
    public ResumeSavesTheAnswerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    [Theory]
    [InlineData(AgentMode.Single)]
    [InlineData(AgentMode.FanOut)]
    public async Task APlainAnswer_IsInTheSavedContext(AgentMode mode)
    {
        var model = new MockLlmProvider("m");
        model.EnqueueResponse(new LlmResponse { Text = "the word is heron", StopReason = "end_turn" });
        var paths = new AppPaths(_dir);
        using var manager = SessionManager.Create(paths);
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(model),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, mode);

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.SubmitRaw("what is the word?"));
        await started.Turn.WaitAsync(TimeSpan.FromSeconds(5));

        var saved = new FolderSessionStore(paths).LoadLatestUncontinued(_dir);
        Assert.NotNull(saved);
        Assert.Contains(saved.Context, m => m.Role == "assistant" && m.Content.Contains("heron"));
    }

    /// <summary>
    /// A GOAL STOPPED BEFORE ANY ANSWER IS STILL SAVED. Ctrl+C on a headless run's first turn reports
    /// a session id; that id has to open, with the goal that was asked.
    /// </summary>
    [Fact]
    public async Task AGoalCancelledBeforeAnyAnswer_IsStillSaved()
    {
        var paths = new AppPaths(_dir);
        using var manager = SessionManager.Create(paths);
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new NeverAnswers()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.SubmitRaw("a long goal"));
        await Task.Delay(100);
        session.CancelTurn();
        await started.Turn.WaitAsync(TimeSpan.FromSeconds(5));

        var saved = new FolderSessionStore(paths).LoadLatestUncontinued(_dir);
        Assert.NotNull(saved);
        Assert.Contains(saved.Context, m => m.Role == "user" && m.Content.Contains("a long goal"));
    }

    private sealed class NeverAnswers : ILlmProvider
    {
        public string ProviderId => "never";
        public string DisplayName => "Never";
        public string ModelId => "never-model";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => true;
        public async Task<LlmResponse> ChatAsync(List<CxAgent.Core.Models.ChatMessage> m,
            List<ToolDefinition>? t, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        }
        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<CxAgent.Core.Models.ChatMessage> m,
            List<ToolDefinition>? t,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }
}
