using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What the agent is told before it starts.
///
/// <para>Adapted from opencode's <c>session/prompt/default.txt</c> and its runtime <c>&lt;env&gt;</c>
/// block. Our own was three sentences; the live drive showed exactly what that costs — the model ran
/// a test command that matched nothing, read <c>exit_code: 0</c> as proof, and reported success over
/// a file that did not compile. It had never been told to check what its verification actually
/// verified.</para>
/// </summary>
public class SystemPromptTests
{
    private static SystemPromptContext Context(string model = "qwen3.6-35b") =>
        new(
            WorkingDirectory: "/tmp/project",
            IsGitRepo: true,
            Platform: "linux",
            Today: new DateOnly(2026, 8, 10),
            ModelId: model);

    private static string Build(string model = "qwen3.6-35b") =>
        SystemPrompt.Build(new SystemPromptContext(
            WorkingDirectory: "/tmp/project",
            IsGitRepo: true,
            Platform: "linux",
            Today: new DateOnly(2026, 8, 10),
            ModelId: model));

    /// <summary>
    /// THE FAILURE THE LIVE DRIVE FOUND. A test run that reports zero tests is not a pass, and a
    /// command that exits 0 having built nothing has verified nothing. This is the one instruction
    /// that had to exist — and unlike a gate it does not care whether the toolchain is dotnet, make,
    /// cargo or an assembler.
    /// </summary>
    [Fact]
    public void Build_TellsTheModelToCheckWhatItsVerificationActuallyVerified()
    {
        var p = Build();

        Assert.Contains("zero tests", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exits 0", p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Never guess the test command — opencode's "NEVER assume specific test framework".</summary>
    [Fact]
    public void Build_ForbidsGuessingTheTestCommand()
    {
        Assert.Contains("do not assume", Build(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The environment block. opencode states the cwd, whether it is a git repo, the platform and the
    /// date; ours stated only the cwd. Each is a fact the model would otherwise guess at, and a wrong
    /// guess about the platform is how `find -printf` ends up on a mac.
    /// </summary>
    [Fact]
    public void Build_StatesTheEnvironment()
    {
        var p = Build();

        Assert.Contains("/tmp/project", p, StringComparison.Ordinal);
        Assert.Contains("linux", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026", p, StringComparison.Ordinal);
        Assert.Contains("git", p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A non-repo says so rather than staying silent — absence of a claim reads as unknown,
    /// and the model will run git commands to find out.</summary>
    [Fact]
    public void Build_SaysWhenItIsNotAGitRepo()
    {
        var p = SystemPrompt.Build(new SystemPromptContext(
            WorkingDirectory: "/tmp/plain", IsGitRepo: false, Platform: "linux",
            Today: new DateOnly(2026, 8, 10), ModelId: "m"));

        Assert.Contains("Is a git repo: no", p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The instruction our old prompt got right, kept: text in a message changes nothing.</summary>
    [Fact]
    public void Build_KeepsTheUseTheToolsInstruction()
    {
        var p = Build();

        Assert.Contains("write_file", p, StringComparison.Ordinal);
        Assert.Contains("replace_in_file", p, StringComparison.Ordinal);
    }

    /// <summary>
    /// Conventions before code. opencode's "NEVER assume that a given library is available… look at
    /// neighbouring files". The model did this unprompted on the drive; that is not a guarantee.
    /// </summary>
    [Fact]
    public void Build_TellsTheModelToFollowExistingConventions()
    {
        Assert.Contains("convention", Build(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Terse by default. opencode caps answers at four lines with worked examples, because every
    /// word of preamble is paid for on a local model and re-sent on every subsequent turn.
    /// </summary>
    [Fact]
    public void Build_AsksForShortAnswers()
    {
        Assert.Contains("concise", Build(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// We ship an http_request tool and said nothing about it. A fetch tool plus an invented URL is
    /// how an agent confidently reads a page that does not exist — which is why opencode's first
    /// substantive line is this same guardrail.
    /// </summary>
    [Fact]
    public void Build_ForbidsInventingAUrlForTheFetchTool()
    {
        var p = Build();

        Assert.Contains("http_request", p, StringComparison.Ordinal);
        Assert.Contains("never invent a url", p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The model cannot run the slash commands — the app intercepts them before a turn starts — but
    /// it should know they exist, so it can tell a user to run /compress when the context is tight
    /// rather than answering a typed "/help" as if it were prose.
    /// </summary>
    [Fact]
    public void Build_NamesTheCommandsTheAppHandles()
    {
        var p = Build();

        Assert.Contains("/compress", p, StringComparison.Ordinal);
        Assert.Contains("/help", p, StringComparison.Ordinal);
        Assert.Contains("cannot run them", p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every turn is a round trip to a local model, so three serial reads cost three
    /// waits. opencode says this twice — once in the default prompt and again for GPT.</summary>
    [Fact]
    public void Build_AsksForIndependentToolCallsInOneTurn()
    {
        Assert.Contains("one round trip", Build(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// EXPLAIN A SHELL COMMAND BEFORE RUNNING IT. This matters more here than it does for opencode:
    /// run_shell goes through a permission prompt, which shows the command TRUNCATED and carries no
    /// reason. Observed on the ConsoleEx drive — two commands approved on their visible prefix
    /// alone, one of which turned out to match zero tests.
    /// </summary>
    [Fact]
    public void Build_AsksTheModelToExplainAShellCommandBeforeRunningIt()
    {
        var p = Build();

        Assert.Contains("shell command", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approve", p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Committing is the user's decision. We hand the model run_shell, so nothing else
    /// stops it running `git commit`.</summary>
    [Fact]
    public void Build_TellsTheModelNotToCommitUnasked()
    {
        Assert.Contains("do not commit", Build(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// We DO render markdown — MainWindow installs a MarkdownStyle for the transcript — and never
    /// said so. A model that does not know whether formatting survives either avoids it or emits
    /// what a monospace pane cannot show.
    /// </summary>
    [Fact]
    public void Build_SaysTheTranscriptRendersMarkdown()
    {
        Assert.Contains("markdown", Build(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A tool is not a communication channel. `echo "let me look at that"` costs a permission prompt
    /// to say something that belongs in the reply — and the user must approve it to hear it.
    /// </summary>
    [Fact]
    public void Build_ForbidsTalkingToTheUserThroughATool()
    {
        var p = Build();

        Assert.Contains("never through a tool", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("echo", p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A refusal explained at length reads as preachy. One sentence and an alternative.</summary>
    [Fact]
    public void Build_AsksForAShortRefusal()
    {
        Assert.Contains("will not do something", Build(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>We hand the model write_file. Nothing else stops it committing a key.</summary>
    [Fact]
    public void Build_ForbidsWritingSecrets()
    {
        Assert.Contains("secret", Build(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ONE PROMPT, WITH A SEAM. opencode ships nine variants because it faces nine model families
    /// with real quirks; this app has one endpoint. The selector exists so a second prompt is a file
    /// rather than a refactor — but adding variants nobody has measured a need for is guessing.
    /// </summary>
    [Fact]
    public void Build_IsTheSamePromptForEveryModel_ForNow()
    {
        Assert.Equal(Build("claude-sonnet-4"), Build("qwen3.6-35b"));
    }

    /// <summary>
    /// THE MODEL ID NEVER REACHES THE PROMPT — it is a selector, not content.
    ///
    /// <para>The system message sits at position 0 and is re-sent verbatim on every turn, so it is
    /// the prompt-cache prefix: cached reads are around a tenth of the input price, and a prefix that
    /// changes throws that away for the whole conversation. opencode DOES put the model name in
    /// theirs ("You are powered by the model named…"), which is fine when a session cannot switch
    /// model — but it is content that varies for no benefit to the model's reasoning, so it stays
    /// out of ours. <c>Build</c> takes the id only to choose a variant later.</para>
    /// </summary>
    [Fact]
    public void Build_NeverPutsTheModelIdInThePrompt()
    {
        Assert.DoesNotContain("qwen3.6-35b", Build("qwen3.6-35b"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("claude-sonnet-4", Build("claude-sonnet-4"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Everything in the prompt is stable for the life of a session, so the cached prefix survives.
    ///
    /// <para>The date is the one field that could drift — a session running past midnight would get
    /// a different prompt and invalidate the cache. It does not, because the system message is built
    /// ONCE (Agent inserts it only when no system message exists) and is pinned above compression.
    /// This asserts the builder itself is pure so that stays true if the caller ever changes.</para>
    /// </summary>
    [Fact]
    public void Build_IsDeterministic_ForTheSameEnvironment()
    {
        Assert.Equal(Build(), Build());
    }

    /// <summary>The prompt is a constant cost on every turn, re-sent in full each time. It has to
    /// stay worth its size on a local model's window.</summary>
    [Fact]
    public void Build_StaysUnderAReasonableSize()
    {
        Assert.True(Build().Length < 4_000, $"the system prompt grew to {Build().Length} chars");
    }
    // ---- MCP server instructions ---------------------------------------------------------------

    /// <summary>
    /// A SERVER'S OWN INSTRUCTIONS REACH THE MODEL.
    ///
    /// <para>Two channels, not one. A tool's description rides on the tool definition and answers
    /// "what does this one call do". The server's instructions answer "how do I use this server at
    /// all" — ordering, roots, conventions — which the author wrote precisely BECAUSE no per-tool
    /// schema could carry it. Shipping the schema and dropping the prose gives the model the shape of
    /// the tools and none of the guidance.</para>
    /// </summary>
    [Fact]
    public void Build_IncludesEachConnectedServersInstructions()
    {
        var p = SystemPrompt.Build(Context() with
        {
            McpInstructions = new Dictionary<string, string>
            {
                ["db"] = "Call list_tables before querying.",
                ["docs"] = "Resolve the library id first.",
            },
        });

        Assert.Contains("Call list_tables before querying.", p, StringComparison.Ordinal);
        Assert.Contains("Resolve the library id first.", p, StringComparison.Ordinal);
    }

    /// <summary>Attributed to the server that said it. An unattributed paragraph in a system prompt
    /// reads as though the app itself said it — the same rule project instructions follow.</summary>
    [Fact]
    public void Build_AttributesInstructionsToTheServerThatSentThem()
    {
        var p = SystemPrompt.Build(Context() with
        {
            McpInstructions = new Dictionary<string, string> { ["db"] = "Call list_tables first." },
        });

        var line = p.Split('\n').First(l => l.Contains("db", StringComparison.Ordinal)
                                          && l.Contains("server", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("db", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// WITH NO SERVERS THE PROMPT IS BYTE-IDENTICAL TO TODAY'S.
    ///
    /// <para>The system message is the prompt-cache prefix. A block that appeared even when empty
    /// would change it for every user who has no MCP configured at all — paying a cache miss for a
    /// feature they are not using.</para>
    /// </summary>
    [Fact]
    public void Build_WithNoServers_IsUnchanged()
    {
        var withNone = SystemPrompt.Build(Context() with
        {
            McpInstructions = new Dictionary<string, string>(),
        });

        Assert.Equal(Build(), withNone);

        // The SECTION specifically, not the string "MCP": /mcp is listed among the user's commands
        // for every user, servers or not, so a bare substring check reads as a regression the moment
        // that line exists.
        Assert.DoesNotContain("# MCP servers", withNone, StringComparison.Ordinal);
    }

    /// <summary>A server that sent no instructions contributes nothing — most servers send none, and
    /// an empty heading per server would be noise in every prompt.</summary>
    [Fact]
    public void Build_AServerWithNoInstructions_ContributesNoBlock()
    {
        var p = SystemPrompt.Build(Context() with
        {
            McpInstructions = new Dictionary<string, string> { ["quiet"] = "   " },
        });

        Assert.Equal(Build(), p);
    }

    // ---- D24/D26: the two prompts ----------------------------------------------------------

    private static string Child() => SystemPrompt.Build(Context() with { IsSubAgent = true });

    /// <summary>A parent that CAN delegate — fan-out mode. The obligation lines are for this agent.</summary>
    private static string Parent() => SystemPrompt.Build(Context() with { CanSpawn = true });

    /// <summary>A parent in SINGLE mode: no spawn tool, so no spawn machinery in its prompt.</summary>
    private static string SingleModeParent() => SystemPrompt.Build(Context());

    /// <summary>
    /// THE COMMANDS BLOCK IS DROPPED FOR A CHILD — the one part of the session prompt that is
    /// actively WRONG rather than merely unhelpful. It names /help, /clear, /compress, /mcp and /exit
    /// to an agent with NO USER AND NO COMPOSER, telling it to suggest commands to someone who will
    /// never see them.
    /// </summary>
    [Fact]
    public void Child_IsNotToldAboutCommandsItCannotRunForAUserItDoesNotHave()
    {
        var child = Child();

        Assert.DoesNotContain("/clear", child, StringComparison.Ordinal);
        Assert.DoesNotContain("/compress", child, StringComparison.Ordinal);
        Assert.DoesNotContain("/exit", child, StringComparison.Ordinal);
        Assert.DoesNotContain("The user's commands", child, StringComparison.Ordinal);

        // The parent still gets them: this is a difference between the two prompts, not a deletion.
        Assert.Contains("/clear", Parent(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A CHILD IS TOLD IT IS ONE, which nothing did before. A child that believes it is in a
    /// conversation closes with "let me know if you'd like me to check the other files" — and the
    /// parent receives a question nobody can answer.
    /// </summary>
    [Fact]
    public void Child_IsToldItsFinalMessageIsTheWholeAnswer()
    {
        var child = Child();

        Assert.Contains("You are a sub-agent", child, StringComparison.Ordinal);
        Assert.Contains("There is no follow-up", child, StringComparison.Ordinal);
        Assert.Contains("do not offer next steps", child, StringComparison.Ordinal);
    }

    /// <summary>
    /// # Answering is REPLACED, not appended to. Two Answering sections would give the child two sets
    /// of instructions that disagree — one written for a person reading a terminal, one for the model
    /// that is actually reading it.
    /// </summary>
    [Fact]
    public void Child_HasExactlyOneAnsweringSection()
    {
        var child = Child();

        Assert.Equal(1, CountOf(child, "# Answering"));
        // And the human-facing guidance is gone rather than sitting alongside it.
        Assert.DoesNotContain("goes to a terminal", child, StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERYTHING ELSE IS KEPT. env, conventions and verifying are as true for a child as for a
    /// parent — verifying MORE so, since the child is the one actually running the commands.
    /// </summary>
    [Fact]
    public void Child_KeepsTheEnvironmentConventionsAndVerifying()
    {
        var child = Child();

        Assert.Contains("<env>", child, StringComparison.Ordinal);
        Assert.Contains("# Following conventions", child, StringComparison.Ordinal);
        Assert.Contains("# Verifying", child, StringComparison.Ordinal);
        Assert.Contains("A command that exits 0 has not necessarily verified anything",
            child, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE PARENT GETS THE THREE OBLIGATION LINES (D26). The verifying one matters most: without it
    /// that entire section is dead on the delegated path, because every rule in it is written about
    /// output THE MODEL READ, and a child's summary is neither the output nor the model's reading of
    /// it.
    /// </summary>
    [Fact]
    public void Parent_IsToldAChildsReportIsAClaimNotAVerification()
    {
        var parent = Parent();

        Assert.Contains("A sub-agent's report is a claim, not a verification", parent, StringComparison.Ordinal);
        Assert.Contains("accountable for a sub-agent's work", parent, StringComparison.Ordinal);
        Assert.Contains("The user cannot see a sub-agent's work", parent, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE PARENT IS TOLD NOTHING ABOUT WHEN TO SPAWN (D25). That belongs in the tool
    /// description, read at the moment of choosing rather than paid for on every turn of every
    /// session — including the ones that never spawn.
    /// </summary>
    /// <summary>
    /// A SINGLE-MODE PARENT IS TOLD NOTHING ABOUT SUB-AGENTS AT ALL.
    ///
    /// <para>It has no spawn tool, so all three obligation lines describe machinery it cannot reach —
    /// the same argument that keeps them off a CHILD, which also cannot spawn. A prompt discussing
    /// capabilities the reader does not have is noise, and noise in a system prompt is what teaches a
    /// model to skim it.</para>
    ///
    /// <para>The useful consequence: single mode's prompt is byte-identical to what shipped before
    /// sub-agents existed, so turning the feature off really does turn it off.</para>
    /// </summary>
    [Fact]
    public void SingleMode_SaysNothingAboutSubAgentsAtAll()
    {
        var single = SingleModeParent();

        Assert.DoesNotContain("sub-agent", single, StringComparison.OrdinalIgnoreCase);

        // ...while the same prompt in fan-out mode does carry them, so this is a difference between
        // modes rather than the lines having been deleted.
        Assert.Contains("sub-agent", Parent(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// FAN-OUT MODE PUSHES THE MODEL TO DELEGATE WIDE READING.
    ///
    /// <para>Measured before this line existed: asked "across this whole repository, which controls
    /// implement their own keyboard handling — I don't know where they are", the model made TWELVE
    /// read_file calls and spawned nothing, on the exact shape of task its tool description says to
    /// delegate. The description alone did not move it.</para>
    ///
    /// <para>opencode carries FOUR such nudges in its system prompt for the same reason. This is one,
    /// phrased as a rule about where the reading LANDS rather than as a preference for a tool: a model
    /// can check "am I about to read files I will not need afterwards" against what it is doing.</para>
    /// </summary>
    [Fact]
    public void FanOut_TellsTheModelToDelegateWideReading()
    {
        var fanOut = Parent();

        Assert.Contains("send a sub-agent rather", fanOut, StringComparison.Ordinal);
        Assert.Contains("only when you already know the file", fanOut, StringComparison.Ordinal);

        // WORKED EXAMPLES, in opencode's shape. Two interventions had already failed to move this
        // model — a sharpened tool description and the rule above — and an example is the lever they
        // have that we did not: it shows the SHAPE of the decision rather than asserting it.
        Assert.Contains("<example>", fanOut, StringComparison.Ordinal);

        // AND A COUNTER-EXAMPLE. Positives alone teach "spawn when asked about code", and a model
        // that over-corrects into delegating a one-file read pays a full run to learn what a single
        // read_file would have said. The pair is what draws the line.
        Assert.Contains("reads the file directly", fanOut, StringComparison.Ordinal);

        // AND THE MIXED TASK. The other three are pure lookups, which move a "where is X?" question
        // and leave a two-halved task untouched — measured: find-every-X-and-fix-the-safe-ones ran
        // entirely inline at 168,249 chars, when the finding half was a textbook delegation.
        Assert.Contains("the finding is a search, the deciding needs this conversation",
            fanOut, StringComparison.Ordinal);

        // FOUR IS THE CEILING, asserted so it stays one. opencode ships two Task-tool examples; ours
        // carries a counter-example and a split, one distinction more than theirs. Past four they
        // stop being a pattern and become a list nobody reads.
        Assert.Equal(4, CountOf(fanOut, "<example>"));
    }

    /// <summary>
    /// AND SINGLE MODE PAYS NOTHING FOR IT. The nudge is gated on CanSpawn like every other
    /// sub-agent line, so a session that cannot delegate is not told to — its prompt stays
    /// byte-identical to what shipped before sub-agents existed.
    /// </summary>
    [Fact]
    public void SingleMode_IsNotToldToDelegate()
    {
        Assert.DoesNotContain("send a sub-agent rather", SingleModeParent(), StringComparison.Ordinal);
    }

    [Fact]
    public void Parent_IsNotToldWhenToSpawn()
    {
        var parent = Parent();

        Assert.DoesNotContain("spawn_agent", parent, StringComparison.Ordinal);
        Assert.DoesNotContain("Do NOT use it", parent, StringComparison.Ordinal);
    }

    /// <summary>A child is not given the three obligation lines: it cannot spawn, so all three concern
    /// a capability it does not have.</summary>
    [Fact]
    public void Child_IsNotGivenTheParentsObligationLines()
    {
        var child = Child();

        Assert.DoesNotContain("accountable for a sub-agent's work", child, StringComparison.Ordinal);
        Assert.DoesNotContain("is a claim, not a verification", child, StringComparison.Ordinal);
    }

    /// <summary>
    /// EACH PROMPT IS STABLE FOR ITS OWN AGENT'S LIFE. IsSubAgent is fixed at construction, so a
    /// child's prefix is byte-identical turn after turn exactly as a parent's is — two prefixes per
    /// session rather than one, which is correct because they are two different agents.
    /// </summary>
    [Fact]
    public void BothPrompts_AreStableAcrossRepeatedBuilds()
    {
        Assert.Equal(Child(), Child());
        Assert.Equal(Parent(), Parent());
        Assert.NotEqual(Child(), Parent());
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
