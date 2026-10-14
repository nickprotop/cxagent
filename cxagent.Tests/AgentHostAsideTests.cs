using CxAgent.Core.Agents;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>An aside's cost is real and counted, and the main agent's own figures never see it.</summary>
public class AgentHostAsideTests
{
    private sealed class Quiet : IAsideObserver
    {
        public void Asked(int aside, string question) { }
        public void Waiting(int aside, bool waiting) { }
        public void AnswerAppended(int aside, string text) { }
        public void LookedUp(int aside, string row) { }
        public void Finished(int aside, AsideEnd end, string? detail) { }
    }

    private static AgentHost Host(ILlmProvider provider) =>
        new(new AgentHost.AgentRuntime { Provider = provider, Executors = JobRegistry.CreateWithBuiltins() },
            new RecordingSink(), new NullJobPanel());

    [Fact]
    public void NoAside_BeforeAnythingWasSent()
    {
        Assert.Null(Host(new MockLlmProvider()).OpenAside(1, new Quiet()));
    }

    /// <summary>
    /// A resumed session has nothing published until its first new request, but it has the
    /// conversation the user is looking at, and that is what a side question is about.
    /// </summary>
    [Fact]
    public async Task AResumedSession_IsAskedAbout_BeforeItsFirstNewMessage()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "you decided X", StopReason = "end_turn" });
        var host = new AgentHost(
            new AgentHost.AgentRuntime { Provider = mock, Executors = JobRegistry.CreateWithBuiltins() },
            new RecordingSink(), new NullJobPanel(),
            resume: new CxAgent.Core.Storage.SessionSnapshot(
                "AAAA01KZXC",
                [new ChatMessage { Role = "user", Content = "earlier work" },
                 new ChatMessage { Role = "assistant", Content = "we chose X" }],
                InputTokens: 10, OutputTokens: 5, UpdatedAt: DateTimeOffset.UtcNow));

        var runner = host.OpenAside(1, new Quiet());
        Assert.NotNull(runner);
        await runner.Ask("what did we decide?")!;

        Assert.Contains(mock.LastMessages!, m => m.Content == "we chose X");
        Assert.Equal(["glob", "grep", "read_file"], mock.LastTools!.Select(t => t.Name).Order());
        Assert.Equal(2, host.Context.Messages.Count);
    }

    /// <summary>After /compress the fork is the compressed conversation, not the larger one it replaced.</summary>
    [Fact]
    public async Task AfterCompress_TheForkIsTheCompressedConversation()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "main", StopReason = "end_turn" });
        mock.EnqueueResponse(new LlmResponse { Text = "a short summary", StopReason = "end_turn" });
        var host = Host(mock);
        await host.RunAsync("goal", CancellationToken.None);

        await host.CompressNowAsync(CancellationToken.None);

        Assert.Equal(host.Context.Messages, host.Published!.Messages);
    }

    [Fact]
    public async Task AsideSpend_IsWorkerSpend_AndTheMainContextIsUntouched()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "main", StopReason = "end_turn" }
            with { Usage = new LlmUsage { InputTokens = 100, OutputTokens = 10, CachedInputTokens = 90, CacheReported = true } });
        mock.EnqueueResponse(new LlmResponse { Text = "aside", StopReason = "end_turn" }
            with { Usage = new LlmUsage { InputTokens = 50, OutputTokens = 5, CachedInputTokens = 0, CacheReported = true } });
        var host = Host(mock);
        await host.RunAsync("goal", CancellationToken.None);
        var ownBefore = host.Ledger.CacheHitRateByAgent.Own;
        var messagesBefore = host.Context.Messages.ToList();
        var updates = 0;
        host.TokensUpdated += (_, _) => updates++;

        await host.OpenAside(1, new Quiet())!.Ask("a private question")!;

        Assert.Equal(55, host.Ledger.SubAgentTokens);
        Assert.Equal(ownBefore, host.Ledger.CacheHitRateByAgent.Own);
        Assert.Equal(messagesBefore, host.Context.Messages);
        Assert.DoesNotContain(host.Context.Messages, m => m.Content.Contains("a private question"));
        Assert.True(updates > 0);
    }
}
