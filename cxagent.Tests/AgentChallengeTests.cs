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
public class AgentChallengeTests
{
    private static Agent Build(ILlmProvider provider, RecordingSink sink,
        int? compressAbove = null, NullJobPanel? panel = null) =>
        new(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(null), sink,
            panel ?? new NullJobPanel(), logs: null, maxTurns: 50, compressAbove: compressAbove);

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
        await Build(provider, sink).SendAsync("fix the rendering bug",
            CancellationToken.None);

        var challenges = provider.LastMessages!
            .Count(m => m.Role == "user" && m.Content.Contains("written", StringComparison.OrdinalIgnoreCase));
        Assert.True(challenges >= 2, $"expected repeated challenges, saw {challenges}");
        Assert.Contains(sink.Errors, e => e.Contains("nothing was written", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AChangeGoalThatWroteNothingFAILSRatherThanCompleting()
    {
        // The lie this mode exists to stop, one level up: the run says done, the disk says
        // otherwise, and the user finds out later.
        var provider = new MockLlmProvider();
        for (var i = 0; i < 6; i++) provider.EnqueueResponse(Prose("I have analysed the code."));

        var sink = new RecordingSink();
        await Build(provider, sink).SendAsync("fix the wrapping bug",
            CancellationToken.None);

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
        await Build(provider, sink).SendAsync("fix the parser", CancellationToken.None);

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
        var answer = await Build(provider, sink).SendAsync("how does focus work in this codebase?",
            CancellationToken.None);

        Assert.Empty(sink.Errors);
        Assert.Contains("FocusManager", answer, StringComparison.Ordinal);
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
            await Build(provider, sink).SendAsync("fix the parser bug",
                CancellationToken.None);

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
            await Build(provider, sink).SendAsync("fix the parser bug",
                CancellationToken.None);

            Assert.Empty(sink.Errors);
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
            await Build(provider, sink).SendAsync("fix the parser bug",
                CancellationToken.None);

            Assert.Contains(sink.Errors, e => e.Contains("build did not succeed", StringComparison.OrdinalIgnoreCase));
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
            await Build(provider, sink).SendAsync("fix the parser bug",
                CancellationToken.None);

            Assert.Empty(sink.Errors);
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
            await Build(provider, sink).SendAsync("update the notes",
                CancellationToken.None);

            Assert.Empty(sink.Errors);
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
            await Build(provider, sink).SendAsync("fix the thing",
                CancellationToken.None);

            Assert.Empty(sink.Errors);
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
        await Build(provider, sink).SendAsync("how does focus work?", CancellationToken.None);

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
            Build(provider, sink).SendAsync("fix it", CancellationToken.None));

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
            await Build(provider, sink).SendAsync("what does this say?",
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
            await Build(provider, sink).SendAsync("update the file", CancellationToken.None);

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
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(),
            new TokenLedger(null), sink, new NullJobPanel(), logs: null, maxTurns: 2);

        var answer = await agent.SendAsync("fix the parser", CancellationToken.None);

        Assert.Contains(sink.Errors, e => e.Contains("stopped after 2 turns", StringComparison.OrdinalIgnoreCase));

        // The summary turn ran WITHOUT tools, so it cannot start work it has no budget to finish.
        Assert.Empty(provider.LastTools!);
        Assert.Contains(provider.LastMessages!, m =>
            m.Role == "user" && m.Content.Contains("maximum number of steps", StringComparison.OrdinalIgnoreCase));

        // The salvaged summary is RETURNED — it is the answer on this path, and the caller is what
        // puts it on the transcript.
        Assert.Contains("thinking", answer);
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
        await Build(provider, sink).SendAsync("what does this do?",
            CancellationToken.None);

        Assert.Empty(sink.Errors);
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
        await Build(provider, sink).SendAsync("what does this do?",
            CancellationToken.None);

        Assert.Empty(sink.Errors);   // gave up retrying and took it at face value
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
        var answer = await Build(provider, sink).SendAsync("what is 2+2?", CancellationToken.None);

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

        // THE RETURNED ANSWER is what the caller puts on the transcript, so it is what must be clean
        // of reasoning — a model that sees its own thinking replayed as content starts treating it as
        // commitment.
        Assert.DoesNotContain("weighing", answer, StringComparison.Ordinal);
        Assert.Contains("The answer is 4.", answer, StringComparison.Ordinal);
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

            var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(),
                new TokenLedger(null), new RecordingSink(), new NullJobPanel(), logs, maxTurns: 10);

            await agent.SendAsync("what is this?", CancellationToken.None);

            // UNDER THE AGENT'S OWN ID — that is the directory a user is told to look in, and it is
            // stable for the session rather than changing with each prompt.
            // The write is fire-and-forget, so poll briefly rather than racing it.
            var agentDir = Path.GetDirectoryName(logs.PathFor(agent.Id, "x", "log"))!;
            for (var i = 0; i < 50 && !Directory.Exists(agentDir); i++) await Task.Delay(20);
            for (var i = 0; i < 50 && Directory.GetFiles(agentDir).Length == 0; i++) await Task.Delay(20);

            var written = string.Concat(Directory.GetFiles(agentDir).Select(File.ReadAllText));

            // The MARKDOWN SOURCE, verbatim — that is the whole point. A log holding only the
            // rendered form could not answer "what did the renderer receive?".
            Assert.Contains("### A heading", written, StringComparison.Ordinal);
            Assert.Contains("**bold** item", written, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// TWO PROMPTS, ONE DIRECTORY, TURNS ASCENDING ACROSS THEM.
    ///
    /// <para>The failure this pins: an id minted per user message scattered one linear session across
    /// a directory per prompt, each restarting its turn numbering at 000 — so the run you wanted to
    /// read was split several ways with nothing saying which came first. This is the automated form of
    /// the manual two-prompt check in the task brief, which needs a live model; the mechanism is
    /// deterministic, so a mock drives it exactly as well.</para>
    /// </summary>
    [Fact]
    public async Task TwoPromptsLogToOneDirectory_WithTurnsNumberedStraightThrough()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxa-logdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var logs = new LogFileManager(new AppPaths(dir));

            var provider = new MockLlmProvider();
            for (var i = 0; i < 4; i++) provider.EnqueueResponse(Prose("ok"));

            var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(),
                new TokenLedger(null), new RecordingSink(), new NullJobPanel(), logs, maxTurns: 10);

            await agent.SendAsync("say hello", CancellationToken.None);
            await agent.SendAsync("say goodbye", CancellationToken.None);

            var agentDir = Path.GetDirectoryName(logs.PathFor(agent.Id, "x", "log"))!;
            // Fire-and-forget writes: poll for the second prompt's context log rather than racing it.
            for (var i = 0; i < 50 && Directory.GetFiles(agentDir, "context-*.log").Length < 2; i++)
                await Task.Delay(20);

            // ONE directory for the whole session — not one per prompt.
            Assert.Equal(new[] { agent.Id }, Directory.GetDirectories(Path.Combine(dir, "logs"))
                .Select(Path.GetFileName).ToArray());

            // Turn 001 EXISTS, which is the whole point: the second prompt continued the numbering
            // instead of overwriting context-000.
            Assert.True(File.Exists(Path.Combine(agentDir, "context-000.log")));
            Assert.True(File.Exists(Path.Combine(agentDir, "context-001.log")));

            // And the header inside says so too — this is the line the manual check greps for.
            var second = await File.ReadAllTextAsync(Path.Combine(agentDir, "context-001.log"));
            Assert.Contains("=== turn 001", second, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // --- Context pressure ------------------------------------------------------------------------

    [Fact]
    public async Task ContextOverTheThreshold_CompressesMidGoal_AndSaysSo()
    {
        // THE LIVE FAILURE. AgentHost's auto-compression sat in a `finally` around the whole goal,
        // and a request is ONE SendAsync that loops internally — so the check fired only after the
        // run that blew past it. Measured live at 1.16M input tokens against a 40,000 threshold,
        // never once compressing. The bound has to be inside the loop, which is what this pins.
        var provider = new MockLlmProvider();

        // The first turn MUST CARRY A TOOL CALL, so the loop continues to a second turn and reaches
        // its pre-send check. A prose turn would return immediately and there would be no "mid" for
        // the compression to happen in the middle of — that case is now handled by the NEXT prompt's
        // pre-send check, which compresses before anything over the threshold is ever sent.
        provider.EnqueueResponse(HeavyWithCall("looking into it", inputTokens: 5_000));
        provider.EnqueueResponse(Prose("summary of the earlier work"));
        for (var i = 0; i < 6; i++) provider.EnqueueResponse(Prose("done"));

        var sink = new RecordingSink();
        var panel = new NullJobPanel();
        await Build(provider, sink, compressAbove: 1_000, panel: panel)
            .SendAsync("do something long", CancellationToken.None);

        // A JOB ROW, so it carries the spinner, the one-line summary and an expandable body like any
        // other piece of work. Asserting on the JOB rather than on transcript text also pins the
        // thing that matters: silent memory loss is the failure, and a row the user can expand to
        // read the summary is what makes a lossy step auditable.
        var row = Assert.Single(panel.Jobs.Where(j => j.PluginType == "compress"
                                                   && j.State == JobState.Succeeded));

        Assert.Contains("over", row.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("messages", row.Result!.Output!["content"]!.ToString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextUnderTheThreshold_DoesNotCompress()
    {
        // The common case, and it must cost nothing: one comparison per turn and no provider call.
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(Heavy("all done", inputTokens: 100));
        for (var i = 0; i < 4; i++) provider.EnqueueResponse(Prose("done"));

        var sink = new RecordingSink();
        var panel = new NullJobPanel();
        await Build(provider, sink, compressAbove: 50_000, panel: panel)
            .SendAsync("do something short", CancellationToken.None);

        Assert.DoesNotContain(panel.Jobs, j => j.PluginType == "compress");
    }

    [Fact]
    public async Task NoThreshold_NeverCompresses()
    {
        // A null threshold is "never", not "always" — a fan-out worker reaching this code path must
        // not start summarising its own context because nobody configured a number.
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(Heavy("all done", inputTokens: 999_999));
        for (var i = 0; i < 4; i++) provider.EnqueueResponse(Prose("done"));

        var sink = new RecordingSink();
        var panel = new NullJobPanel();
        await Build(provider, sink, compressAbove: null, panel: panel)
            .SendAsync("do something", CancellationToken.None);

        Assert.DoesNotContain(panel.Jobs, j => j.PluginType == "compress");
    }

    /// <summary>A response reporting a given input-token count, which is what the trigger reads.</summary>
    private static LlmResponse Heavy(string text, int inputTokens) =>
        new() { Text = text, ToolCalls = [], Usage = new LlmUsage { InputTokens = inputTokens } };

    /// <summary>
    /// The same, but carrying a tool call so the loop runs ANOTHER turn. Needed to observe anything
    /// that happens at the top of a subsequent turn: a response with no tool calls ends the request
    /// there, so a check placed before the next send is never reached.
    /// </summary>
    private static LlmResponse HeavyWithCall(string text, int inputTokens) => new()
    {
        Text = text,
        ToolCalls = [new ToolCall
        {
            Id = "h1", Name = "run_shell",
            Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { command = "echo hi" }),
        }],
        Usage = new LlmUsage { InputTokens = inputTokens },
    };

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
        public void ShowError(string message) => Errors.Add(message);
        public void SetAssistantHeader(ChatMessageId id, string header) => Headers.Add(header);
        public void ShowSystemMessage(string message) { }
        public void ShowApprovalRequest(string? detail = null) { }
    }

}
