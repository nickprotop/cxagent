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
    public async Task APassingBuildDoesNotEraseAFailingTEST()
    {
        // THE LIVE FAILURE, from a drive against a ConsoleEx clone. The agent fixed a bug, wrote a
        // test, ran `dotnet test` (1 failed), then rebuilt the test project to keep iterating — and
        // that BUILD SUCCEEDED. One slot held both verdicts, so the passing build overwrote the
        // failing test, the gate saw a clean tree, and the goal reported done with its own new test
        // red. Exactly the "run says done, disk says otherwise" failure this gate exists to stop.
        var dir = Path.Combine(Path.GetTempPath(), "cxa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "x.cs");
            File.WriteAllText(f, "old");

            var provider = new MockLlmProvider();
            provider.EnqueueResponse(Write(f, "new"));
            provider.EnqueueResponse(ShellCall("dotnet test"));       // fails in the sandbox
            // A CLEAN BUILD AFTERWARDS. Exits 0 and prints no failure marker, so on its own it is a
            // passing verdict — it must not be allowed to answer for the test run.
            provider.EnqueueResponse(ShellCall("dotnet build --help >/dev/null 2>&1 || true"));
            for (var i = 0; i < 6; i++) provider.EnqueueResponse(Prose("The fix is complete."));

            var sink = new RecordingSink();
            var state = await Build(provider, sink).RunAsync("gt", Goal("fix the parser bug"),
                CancellationToken.None);

            Assert.Equal(GoalState.Failed, state);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ATestThatWasFIXEDAfterFailingStillCompletes()
    {
        // The other half of the same rule: the verdict is the LAST test run, not any test run.
        // Tracking tests separately must not turn a fixed failure into a permanent one.
        var dir = Path.Combine(Path.GetTempPath(), "cxa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "x.cs");
            File.WriteAllText(f, "old");

            var provider = new MockLlmProvider();
            provider.EnqueueResponse(Write(f, "broken"));
            provider.EnqueueResponse(ShellCall("dotnet test"));       // fails in the sandbox
            provider.EnqueueResponse(Write(f, "fixed"));
            provider.EnqueueResponse(ShellCall("dotnet test --help >/dev/null 2>&1 || true"));
            for (var i = 0; i < 4; i++) provider.EnqueueResponse(Prose("Fixed and verified."));

            var sink = new RecordingSink();
            var state = await Build(provider, sink).RunAsync("gu", Goal("fix the parser bug"),
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


    [Fact]
    public async Task RepeatingTheSameCallWithTheSameResultIsCalledOut()
    {
        // Measured: one drive produced NOTHING in 42 calls, having read MarkupParser.cs six times
        // and searched it five times -- each call returning what it had already returned. A model in
        // that state is not making progress and will not spontaneously leave it; every repeat is a
        // paid turn against the cap. OpenHands names this "same action, same observation" and nudges
        // once before killing, which is the right order: the model may simply have lost track.
        var dir = Path.Combine(Path.GetTempPath(), "cxa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "same.txt");
            File.WriteAllText(f, "unchanging");

            var provider = new MockLlmProvider();
            for (var i = 0; i < 5; i++) provider.EnqueueResponse(ReadCall(f));
            for (var i = 0; i < 4; i++) provider.EnqueueResponse(Prose("Here is what I found."));

            var sink = new RecordingSink();
            await Build(provider, sink).RunAsync("gr", Goal("what does this say?"),
                CancellationToken.None);

            Assert.Contains(provider.LastMessages!, m =>
                m.Role == "user" && m.Content.Contains("same arguments", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ReadingTheSameFileAfterCHANGINGItIsNotCalledOut()
    {
        // The signature includes the RESULT, so a re-read that returns something different is
        // progress, not a loop. Without that, the commonest correct pattern in the whole tool --
        // read, edit, read back -- would be flagged.
        var dir = Path.Combine(Path.GetTempPath(), "cxa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "changing.txt");
            File.WriteAllText(f, "before");

            var provider = new MockLlmProvider();
            provider.EnqueueResponse(ReadCall(f));
            provider.EnqueueResponse(Write(f, "after"));
            provider.EnqueueResponse(ReadCall(f));
            provider.EnqueueResponse(Prose("Updated."));

            var sink = new RecordingSink();
            await Build(provider, sink).RunAsync("gn", Goal("update the file"), CancellationToken.None);

            Assert.DoesNotContain(provider.LastMessages!, m =>
                m.Role == "user" && m.Content.Contains("same arguments", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static LlmResponse ReadCall(string path) => new()
    {
        Text = "",
        ToolCalls = [new ToolCall
        {
            Id = "r1", Name = "read_file",
            Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { path }),
        }],
        Usage = new LlmUsage(),
    };


    [Fact]
    public async Task HittingTheTurnCapAsksForAHandoffSummary()
    {
        // The cap used to print one line and discard everything the model had learned, leaving the
        // user with a half-edited tree and no account of it. opencode injects a forced-stop prompt
        // and takes a summary; SWE-agent auto-submits whatever diff exists. Both salvage.
        var provider = new MockLlmProvider();
        for (var i = 0; i < 40; i++) provider.EnqueueResponse(Prose("thinking"));

        var sink = new RecordingSink();
        var loop = new SingleAgentLoop(provider, PluginRegistry.CreateWithBuiltins(),
            new TokenLedger(null), sink, new NullJobPanel(), logs: null, maxTurns: 2);

        var conversation = Goal("fix the parser");
        var state = await loop.RunAsync("gcap", conversation, CancellationToken.None);

        Assert.Equal(GoalState.Failed, state);

        // The summary turn ran WITHOUT tools, so it cannot start work it has no budget to finish.
        Assert.Empty(provider.LastTools!);
        Assert.Contains(provider.LastMessages!, m =>
            m.Role == "user" && m.Content.Contains("maximum number of steps", StringComparison.OrdinalIgnoreCase));

        // And what it said survives into the session, not just the transcript.
        Assert.Contains(conversation, m => m.Role == "assistant" && m.Content.Contains("thinking"));
    }


    [Fact]
    public async Task AToolUseStopWithNoParsedCallIsRetriedNotTakenAsDone()
    {
        // The server said the turn ended in a tool call and none was parsed. They disagree only when
        // something went wrong in between -- a truncated stream, a malformed arguments blob the
        // accumulator dropped -- and ending the goal there discards a turn the model believed it was
        // mid-way through.
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
            { Text = "", ToolCalls = [], StopReason = "tool_use", Usage = new LlmUsage() });
        for (var i = 0; i < 4; i++) provider.EnqueueResponse(Prose("Here is the answer."));

        var sink = new RecordingSink();
        var state = await Build(provider, sink).RunAsync("gm", Goal("what does this do?"),
            CancellationToken.None);

        Assert.Equal(GoalState.Completed, state);
        Assert.Contains(provider.LastMessages!, m =>
            m.Role == "user" && m.Content.Contains("cut off", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AServerThatAlwaysSaysToolUseCannotSpinTheLoop()
    {
        // Bounded, because a server that misreports on EVERY turn would otherwise never let the goal
        // end -- trading a truncation bug for a hang.
        var provider = new MockLlmProvider();
        for (var i = 0; i < 12; i++)
            provider.EnqueueResponse(new LlmResponse
                { Text = "done", ToolCalls = [], StopReason = "tool_use", Usage = new LlmUsage() });

        var sink = new RecordingSink();
        var state = await Build(provider, sink).RunAsync("gm2", Goal("what does this do?"),
            CancellationToken.None);

        Assert.Equal(GoalState.Completed, state);   // gave up retrying and took it at face value
    }


    [Fact]
    public void ExtractReasoning_ReturnsTheThinkingWhileItIsSTILLOPEN()
    {
        // The mid-stream case, which is the whole point: the opening tag has arrived and the closing
        // one has not, and that is exactly when the model has emitted nothing else.
        var partial = "<think>Checking WrapCellLine for where the flag";
        Assert.Equal("Checking WrapCellLine for where the flag",
            CxAgent.Core.Plugins.Builtin.LlmAgentJobPlugin.ExtractReasoning(partial));
    }

    [Fact]
    public void ExtractReasoning_AndStripReasoning_AreComplements()
    {
        const string full = "<think>weighing options</think>The answer is 4.";

        Assert.Equal("weighing options",
            CxAgent.Core.Plugins.Builtin.LlmAgentJobPlugin.ExtractReasoning(full));
        Assert.Equal("The answer is 4.",
            CxAgent.Core.Plugins.Builtin.LlmAgentJobPlugin.StripReasoning(full).Trim());
    }

    [Fact]
    public void ExtractReasoning_IsEmptyWhenThereIsNoThinking()
    {
        Assert.Equal("", CxAgent.Core.Plugins.Builtin.LlmAgentJobPlugin.ExtractReasoning("plain text"));
        Assert.Equal("", CxAgent.Core.Plugins.Builtin.LlmAgentJobPlugin.ExtractReasoning(null));
    }

    [Fact]
    public async Task ReasoningIsShownInTheBodyAndNeverInTheConversation()
    {
        // IN THE BODY, not the header. It was a one-line header that rewrote itself as each new line
        // of thought arrived, which discarded the reasoning as fast as it appeared and — because
        // nothing cleared the header at the end of a turn — left the last line of thinking welded to
        // the finished message as its title.
        //
        // The conversation must still not carry thinking: a model that sees its own reasoning
        // replayed as content starts treating it as commitment.
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(Prose("<think>weighing the options</think>The answer is 4."));

        var sink = new RecordingSink();
        var conversation = Goal("what is 2+2?");
        await Build(provider, sink).RunAsync("gh", conversation, CancellationToken.None);

        var body = string.Concat(sink.Appended);
        Assert.Contains("weighing the options", body, StringComparison.Ordinal);

        // AMBER, not [dim]. Dim asks the terminal to render the same colour more faintly — a request
        // many terminals ignore and none render identically, and against a dark background the ones
        // that honour it produce grey mush. The colour comes from the shared palette, so the
        // transcript and this text cannot drift apart.
        Assert.Contains(CxAgent.UI.ColorScheme.ThinkingMarkup, body, StringComparison.Ordinal);
        Assert.DoesNotContain("[dim]", body, StringComparison.Ordinal);

        // The reasoning never became a header — the defect this replaced.
        Assert.DoesNotContain(sink.Headers, h => h.Contains("weighing", StringComparison.Ordinal));

        Assert.DoesNotContain(conversation, m => m.Content.Contains("weighing", StringComparison.Ordinal));
        Assert.Contains(conversation, m => m.Content.Contains("The answer is 4.", StringComparison.Ordinal));
    }


    [Fact]
    public async Task TheModelsRawResponseIsLogged()
    {
        // Only tool RESULTS were ever written, so the model's own output — prose, reasoning,
        // markdown — existed nowhere once the screen scrolled. A rendering bug reported from a
        // screenshot was undiagnosable: the input that produced it could not be recovered, and every
        // hypothesis about it stayed a guess. Measured: three wrong diagnoses before this was added.
        var dir = Path.Combine(Path.GetTempPath(), "cxa-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var logs = new LogFileManager(new AppPaths(dir));

            var provider = new MockLlmProvider();
            provider.EnqueueResponse(Prose("### A heading\n\n- **bold** item"));

            var loop = new SingleAgentLoop(provider, PluginRegistry.CreateWithBuiltins(),
                new TokenLedger(null), new RecordingSink(), new NullJobPanel(), logs, maxTurns: 10);

            await loop.RunAsync("goal-1", Goal("what is this?"), CancellationToken.None);

            // The write is fire-and-forget, so poll briefly rather than racing it.
            var goalDir = Path.GetDirectoryName(logs.PathFor("goal-1", "x", "log"))!;
            for (var i = 0; i < 50 && !Directory.Exists(goalDir); i++) await Task.Delay(20);
            for (var i = 0; i < 50 && Directory.GetFiles(goalDir).Length == 0; i++) await Task.Delay(20);

            var written = string.Concat(Directory.GetFiles(goalDir).Select(File.ReadAllText));

            // The MARKDOWN SOURCE, verbatim — that is the whole point. A log holding only the
            // rendered form could not answer "what did the renderer receive?".
            Assert.Contains("### A heading", written, StringComparison.Ordinal);
            Assert.Contains("**bold** item", written, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private sealed class RecordingSink : IChatSink
    {
        public readonly List<string> Errors = [];
        public ChatMessageId AddUserTurn(string text) => new(0);
        public int Begins, Ends;
        public readonly List<string> Headers = [];
        public ChatMessageId BeginAssistantTurn() { Begins++; return new(0); }
        /// <summary>Every body token, in order — reasoning now streams here rather than to the header.</summary>
        public readonly List<string> Appended = [];
        public void AppendAssistant(ChatMessageId id, string token) => Appended.Add(token);
        public void EndAssistantTurn(ChatMessageId id) => Ends++;
        public void ShowGoalResult(GoalState state, int failedCount) { }
        public void ShowError(string message) => Errors.Add(message);
        public void SetAssistantHeader(ChatMessageId id, string header) => Headers.Add(header);
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
