using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What happens when the model stops WITHOUT writing on a goal that asked for a change.
///
/// <para>Measured against a real bug hunt: the model investigated competently, stalled before
/// reaching the edit, answered the single challenge with PROSE rather than a tool call, and the loop
/// accepted that as done — twice in a row, 55 tool calls across two runs, nothing written either
/// time, and <c>GoalState.Completed</c> reported over an unchanged working tree. One nudge only
/// catches a model that FORGOT to write; it does nothing about one that has stalled, which is the
/// commoner case on a hard task.</para>
/// </summary>
public class SingleAgentLoopChallengeTests
{
    private static SingleAgentLoop Build(MockLlmProvider provider, RecordingSink sink) =>
        new(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null), sink,
            new NullJobPanel(), logs: null, maxTurns: 50);

    private static List<ChatMessage> Goal(string text) =>
        [new ChatMessage { Role = "user", Content = text, Timestamp = DateTimeOffset.UtcNow }];

    private static LlmResponse Prose(string text) =>
        new() { Text = text, ToolCalls = [], Usage = new LlmUsage() };

    [Fact]
    public async Task StallingWithoutWritingIsChallengedMoreThanOnce()
    {
        // Three prose turns in a row: the old loop challenged once, accepted the second, and
        // returned Completed. Every one of them must now be challenged.
        var provider = new MockLlmProvider();
        for (var i = 0; i < 4; i++) provider.EnqueueResponse(Prose($"Here is what I found ({i})."));

        var sink = new RecordingSink();
        var state = await Build(provider, sink).RunAsync("g1", Goal("fix the rendering bug"),
            CancellationToken.None);

        var challenges = provider.LastMessages!
            .Count(m => m.Role == "user" && m.Content.Contains("written", StringComparison.OrdinalIgnoreCase));
        Assert.True(challenges >= 2, $"expected repeated challenges, saw {challenges}");
        Assert.Equal(GoalState.Failed, state);
    }

    [Fact]
    public async Task AChangeGoalThatWroteNothingFAILSRatherThanCompleting()
    {
        // The lie this mode exists to stop, one level up: the run says done, the disk says
        // otherwise, and the user finds out later.
        var provider = new MockLlmProvider();
        for (var i = 0; i < 6; i++) provider.EnqueueResponse(Prose("I have analysed the code."));

        var sink = new RecordingSink();
        var state = await Build(provider, sink).RunAsync("g2", Goal("fix the wrapping bug"),
            CancellationToken.None);

        Assert.Equal(GoalState.Failed, state);
        Assert.Contains(sink.Errors, e => e.Contains("nothing was written", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnExplicitCANNOTEndsTheGoalWithoutFurtherChallenges()
    {
        // Challenging a model that has already said it cannot proceed just burns turns to hear the
        // same thing louder.
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(Prose("Let me look."));
        provider.EnqueueResponse(Prose("CANNOT: the file is generated at build time."));
        for (var i = 0; i < 4; i++) provider.EnqueueResponse(Prose("still cannot"));

        var sink = new RecordingSink();
        await Build(provider, sink).RunAsync("g3", Goal("fix the parser"), CancellationToken.None);

        var challenges = provider.LastMessages!
            .Count(m => m.Role == "user" && m.Content.Contains("written", StringComparison.OrdinalIgnoreCase));
        Assert.True(challenges <= 1, $"a CANNOT reply should stop the challenges, saw {challenges}");
    }

    [Fact]
    public async Task AQuestionIsNeverChallengedAndStillCompletes()
    {
        // The guard must stay conservative. Failing a question that was only ever a question would
        // be worse than missing one edit.
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(Prose("Focus is decided by FocusManager."));

        var sink = new RecordingSink();
        var state = await Build(provider, sink).RunAsync("g4",
            Goal("how does focus work in this codebase?"), CancellationToken.None);

        Assert.Equal(GoalState.Completed, state);
        Assert.Empty(sink.Errors);
    }

    private sealed class RecordingSink : IChatSink
    {
        public readonly List<string> Errors = [];
        public ChatMessageId AddUserTurn(string text) => new(0);
        public ChatMessageId BeginAssistantTurn() => new(0);
        public void AppendAssistant(ChatMessageId id, string token) { }
        public void EndAssistantTurn(ChatMessageId id) { }
        public void ShowGoalResult(GoalState state, int failedCount) { }
        public void ShowError(string message) => Errors.Add(message);
        public void ShowSystemMessage(string message) { }
        public void ShowApprovalRequest(string? detail = null) { }
    }

    private sealed class NullJobPanel : IJobPanel
    {
        public void SetJobs(IReadOnlyList<Job> jobs) { }
        public void UpdateJob(Job job) { }
        public void UpdateResources(string jobId, ResourceSnapshot snapshot) { }
        public void AppendText(string jobId, string delta) { }
        public bool AwaitingApproval { get; set; }
        public void SetDraftMode(bool on) { }
    }
}
