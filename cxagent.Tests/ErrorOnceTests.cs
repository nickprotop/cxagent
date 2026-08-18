using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A failing turn reports ONCE. Not twice, and not zero.
///
/// <para>It was handled in two places, differently: AgentHost caught and called <c>_sink.Failed</c>,
/// while the composition root's wrapper caught and wrote to its own transcript. Both land in the same
/// transcript, so collapsing one without the other either duplicates the line or loses it.</para>
/// </summary>
public class ErrorOnceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "err-" + Guid.NewGuid().ToString("N"));

    public ErrorOnceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private sealed class ThrowingProvider : ILlmProvider
    {
        public string ProviderId => "boom";
        public string ModelId => "boom-model";
        public string DisplayName => "Boom";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
            => throw new InvalidOperationException("the provider exploded");

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> m,
            List<ToolDefinition>? t,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw new InvalidOperationException("the provider exploded");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    /// <summary>Counts every line the session emits, whichever channel it uses.</summary>
    private sealed class LineCounter : ISessionObserver
    {
        public List<string> Lines { get; } = [];
        private void Note(string s) { lock (Lines) Lines.Add(s); }

        public void Said(string markup) => Note(markup);
        public void Failed(string message) => Note(message);
        public void UserTurnAdded(ChatMessageId id, string text) { }
        public void AssistantTurnBegan(ChatMessageId id) { }
        public void AssistantTextAppended(ChatMessageId id, string text) { }
        public void AssistantReasoningAppended(ChatMessageId id, string text) { }
        public void AssistantTurnEnded(ChatMessageId id) { }
        public void AssistantLabelled(ChatMessageId id, string label) { }
    }

    [Fact]
    public async Task AFailingTurn_ReportsExactlyOnce()
    {
        var counter = new LineCounter();
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new ThrowingProvider()),
            new SessionPorts { Observer = counter, ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("go"));
        await started.Turn;

        var mentions = counter.Lines.Count(l => l.Contains("exploded", StringComparison.Ordinal));
        Assert.True(mentions == 1,
            $"expected one report, got {mentions}: [{string.Join(" | ", counter.Lines)}]");
    }
}
