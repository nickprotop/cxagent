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
    private static SingleAgentLoop Build(ILlmProvider provider, RecordingSink sink) =>
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


    private static LlmResponse ShellCall(string command) => new()
    {
        Text = "",
        ToolCalls = [new ToolCall
        {
            Id = "s1", Name = "run_shell",
            Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { command }),
        }],
        Usage = new LlmUsage(),
    };

    [Fact]
    public async Task AWrittenEditThatDoesNotBuildFAILSRatherThanCompleting()
    {
        // THE LIVE FAILURE. An agent found the bug, wrote a correct diagnosis, and its patch did not
        // compile (`error CS1612`). "Build FAILED" is in the transcript, and it reported success in
        // the same turn. `wrote` was true, so the no-write gate saw nothing wrong -- this is the one
        // that has to catch it.
        var dir = Path.Combine(Path.GetTempPath(), "cxa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "x.cs");
            File.WriteAllText(f, "old");

            var provider = new MockLlmProvider();
            provider.EnqueueResponse(Write(f, "new"));
            provider.EnqueueResponse(ShellCall("dotnet build"));            // returns a failure
            for (var i = 0; i < 6; i++) provider.EnqueueResponse(Prose("The fix is complete."));

            var sink = new RecordingSink();
            var state = await Build(provider, sink).RunAsync("gb", Goal("fix the parser bug"),
                CancellationToken.None);

            Assert.Equal(GoalState.Failed, state);
            Assert.Contains(sink.Errors, e => e.Contains("build did not succeed", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ABuildThatWasFIXEDAfterFailingStillCompletes()
    {
        // The verdict is the LAST build, not any build. A model that breaks the tree, notices, fixes
        // it and stops has finished the job -- failing it there would punish exactly the behaviour
        // this gate exists to encourage.
        var dir = Path.Combine(Path.GetTempPath(), "cxa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "x.cs");
            File.WriteAllText(f, "old");

            var provider = new MockLlmProvider();
            provider.EnqueueResponse(Write(f, "broken"));
            provider.EnqueueResponse(ShellCall("dotnet build"));      // fails in the sandbox
            provider.EnqueueResponse(Write(f, "fixed"));
            // A REAL build verb, so it replaces the earlier verdict. `true` exits 0 and prints
            // nothing, so no failure marker is present -- the second build is clean.
            provider.EnqueueResponse(ShellCall("dotnet build --help >/dev/null 2>&1 || true"));
            for (var i = 0; i < 4; i++) provider.EnqueueResponse(Prose("Fixed and verified."));

            var sink = new RecordingSink();
            var state = await Build(provider, sink).RunAsync("gc", Goal("fix the parser bug"),
                CancellationToken.None);

            Assert.Equal(GoalState.Completed, state);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task AGoalThatNeverBuiltIsNotFailedForIt()
    {
        // No build was run, so there is no verdict -- and inventing one would fail every goal in a
        // repo with no build command, or one whose change is a document.
        var dir = Path.Combine(Path.GetTempPath(), "cxa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "notes.md");
            File.WriteAllText(f, "old");

            var provider = new MockLlmProvider();
            provider.EnqueueResponse(Write(f, "new"));
            provider.EnqueueResponse(Prose("Updated the notes."));

            var sink = new RecordingSink();
            var state = await Build(provider, sink).RunAsync("gd", Goal("update the notes"),
                CancellationToken.None);

            Assert.Equal(GoalState.Completed, state);
            Assert.Empty(sink.Errors);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ANonBuildShellCommandDoesNotSetTheVerdict()
    {
        // `grep` printing the word "FAILED" out of a log must not fail the goal. Only build and test
        // commands set the verdict; everything else is left alone.
        var dir = Path.Combine(Path.GetTempPath(), "cxa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "x.cs");
            File.WriteAllText(f, "old");

            var provider = new MockLlmProvider();
            provider.EnqueueResponse(Write(f, "new"));
            provider.EnqueueResponse(ShellCall("echo 'Build FAILED'"));    // NOT a build command
            provider.EnqueueResponse(Prose("Done."));

            var sink = new RecordingSink();
            var state = await Build(provider, sink).RunAsync("ge", Goal("fix the thing"),
                CancellationToken.None);

            Assert.Equal(GoalState.Completed, state);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static LlmResponse Write(string path, string content) => new()
    {
        Text = "",
        ToolCalls = [new ToolCall
        {
            Id = "w1", Name = "write_file",
            Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { path, content }),
        }],
        Usage = new LlmUsage(),
    };


    [Fact]
    public async Task EveryTurnOpensAndClosesExactlyOneAssistantTurn()
    {
        // THE SPINNER. A turn is created with thinking:true and the control clears that flag when
        // body content arrives, so the turn must be OPEN while the model is being called -- that is
        // the part that takes seconds to minutes locally. It used to be opened and closed together
        // AFTER the response arrived, so between a tool result and the next response the transcript
        // sat still, with no way to tell a model that is thinking from one that has died.
        //
        // Balance is the testable half: every Begin must have its End, or a spinner is left running
        // over a finished goal -- which says "still working" about something already over.
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(Prose("Looking."));
        provider.EnqueueResponse(Prose("Focus is decided by FocusManager."));

        var sink = new RecordingSink();
        await Build(provider, sink).RunAsync("gs", Goal("how does focus work?"), CancellationToken.None);

        Assert.True(sink.Begins > 0, "no assistant turn was ever opened");
        Assert.Equal(sink.Begins, sink.Ends);
    }

    [Fact]
    public async Task TheAssistantTurnIsClosedEvenWhenTheProviderThrows()
    {
        // A spinner left running after a failure is worse than no spinner.
        var provider = new ThrowingProvider();
        var sink = new RecordingSink();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Build(provider, sink).RunAsync("gt", Goal("fix it"), CancellationToken.None));

        Assert.Equal(sink.Begins, sink.Ends);
    }

    private sealed class ThrowingProvider : ILlmProvider
    {
        public string ProviderId => "throwing";
        public string DisplayName => "Throwing";
        public string ModelId => "throwing-1";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => true;
        public ILlmProvider WithModel(string model) => this;
        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition> tools,
            CancellationToken ct) => throw new InvalidOperationException("boom");
        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition> tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class RecordingSink : IChatSink
    {
        public readonly List<string> Errors = [];
        public ChatMessageId AddUserTurn(string text) => new(0);
        public int Begins, Ends;
        public ChatMessageId BeginAssistantTurn() { Begins++; return new(0); }
        public void AppendAssistant(ChatMessageId id, string token) { }
        public void EndAssistantTurn(ChatMessageId id) => Ends++;
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
