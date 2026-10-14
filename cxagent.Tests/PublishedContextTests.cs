using CxAgent.Core.Agents;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using System.Text.Json;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a side question forks from: the conversation exactly as the main agent last sent it, written
/// by the loop's own thread at a moment it is not mutating the list.
/// </summary>
public class PublishedContextTests
{
    /// <summary>Records, at each call, what was sent and what the agent had published by then.</summary>
    private sealed class ProbingProvider(Func<PublishedContext?> probe, params LlmResponse[] replies) : ILlmProvider
    {
        private readonly Queue<LlmResponse> _replies = new(replies);
        public List<(List<ChatMessage> Sent, List<ToolDefinition>? Tools, PublishedContext? Seen)> Calls { get; } = new();

        public string ProviderId => "probe";
        public string DisplayName => "Probe";
        public string ModelId => "probe-model";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => true;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls.Add(([.. messages], tools, probe()));
            var r = _replies.Dequeue();
            for (var i = 0; i < r.ToolCalls.Count - 1; i++) yield return new LlmStreamChunk(null, r.ToolCalls[i], false);
            yield return new LlmStreamChunk(r.Text, r.ToolCalls.LastOrDefault(), true, r.Usage, r.StopReason);
            await Task.CompletedTask;
        }
    }

    private static AgentHost Host(ILlmProvider provider) =>
        new(new AgentHost.AgentRuntime { Provider = provider, Executors = JobRegistry.CreateWithBuiltins() },
            new RecordingSink(), new NullJobPanel());

    private static ToolCall Read(string path) => new()
    {
        Id = "c1", Name = "read_file",
        Arguments = JsonDocument.Parse($"{{\"path\":{JsonSerializer.Serialize(path)}}}").RootElement,
    };

    [Fact]
    public void NothingIsPublished_BeforeAnyRequest()
    {
        Assert.Null(Host(new MockLlmProvider()).Published);
    }

    /// <summary>Each request publishes itself first, so the snapshot IS the request — never mid-tool-call.</summary>
    [Fact]
    public async Task EachRequest_IsPublishedBeforeItIsSent()
    {
        AgentHost? host = null;
        var provider = new ProbingProvider(() => host!.Published,
            new LlmResponse { ToolCalls = [Read("/nonexistent/x.txt")], StopReason = "tool_use" },
            new LlmResponse { Text = "done", StopReason = "end_turn" });
        host = Host(provider);

        await host.RunAsync("goal", CancellationToken.None);

        Assert.Equal(2, provider.Calls.Count);
        foreach (var (sent, tools, seen) in provider.Calls)
        {
            Assert.NotNull(seen);
            Assert.Equal(sent, seen!.Messages);
            Assert.Equal(tools!.Select(t => t.Name), seen.Tools.Select(t => t.Name));
        }

        // THE SECOND REQUEST CARRIES THE TOOL RESULT: every call in it is answered.
        var second = provider.Calls[1].Seen!.Messages;
        Assert.Equal("tool", second[^1].Role);
    }

    /// <summary>At turn end the answer is published too, so an aside asked afterwards sees it.</summary>
    [Fact]
    public async Task AFinishedTurn_PublishesItsAnswer()
    {
        var host = Host(new ProbingProvider(() => null,
            new LlmResponse { Text = "the answer", StopReason = "end_turn" }));

        await host.RunAsync("goal", CancellationToken.None);

        var last = host.Published!.Messages[^1];
        Assert.Equal("assistant", last.Role);
        Assert.Equal("the answer", last.Content);
    }

    /// <summary>A copy, not the live list: the main loop appending later must not change it.</summary>
    [Fact]
    public async Task ThePublishedList_IsACopy()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "one", StopReason = "end_turn" });
        mock.EnqueueResponse(new LlmResponse { Text = "two", StopReason = "end_turn" });
        var host = Host(mock);

        await host.RunAsync("first", CancellationToken.None);
        var before = host.Published!;
        var count = before.Messages.Count;
        await host.RunAsync("second", CancellationToken.None);

        Assert.Equal(count, before.Messages.Count);
        Assert.NotSame(before, host.Published);
    }
}
