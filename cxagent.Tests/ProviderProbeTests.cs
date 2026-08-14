using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class ProviderProbeTests
{
    private sealed class ThrowingProvider : ILlmProvider
    {
        public string ProviderId => "bad";
        public string DisplayName => "Bad";
        public string ModelId => "test-model";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;
        public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> m, List<ToolDefinition>? t,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { yield break; await Task.CompletedTask; }
    }

    [Fact]
    public async Task Reachable_WhenChatSucceeds()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "pong", StopReason = "end_turn" });

        var r = await ProviderProbe.TestAsync(mock, default);

        Assert.True(r.Reachable);
        Assert.True(r.SupportsTools);      // MockLlmProvider.SupportsToolCalling == true
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task NotReachable_WhenChatThrows_AndErrorIsCarried()
    {
        var r = await ProviderProbe.TestAsync(new ThrowingProvider(), default);

        Assert.False(r.Reachable);
        Assert.False(r.SupportsTools);     // unknown when unreachable — reported as false
        Assert.Contains("connection refused", r.Error);
    }

    private sealed class CancellingProvider : ILlmProvider
    {
        public string ProviderId => "cancels";
        public string DisplayName => "Cancels";
        public string ModelId => "test-model";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;
        public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
            => throw new OperationCanceledException(ct);
        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> m, List<ToolDefinition>? t,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { yield break; await Task.CompletedTask; }
    }

    // Cancellation must propagate, not be swallowed into ProbeResult.Error: otherwise cancelling the
    // wizard mid-probe renders a bogus "could not reach the provider" instead of unwinding the flow.
    [Fact]
    public async Task Cancellation_Propagates_RatherThanBeingReportedAsUnreachable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProviderProbe.TestAsync(new CancellingProvider(), cts.Token));
    }
}
