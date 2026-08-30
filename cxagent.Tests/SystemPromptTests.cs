using CxAgent.Core.Llm;
using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What the agent is told before it starts.
///
/// <para>WHY THE PROMPT IS THIS LONG. A terse, three-sentence prompt is not enough: the live drive
/// showed exactly what that costs — the model ran a test command that matched nothing, read
/// <c>exit_code: 0</c> as proof, and reported success over a file that did not compile. It had never
/// been told to check what its verification actually verified.</para>
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

    /// <summary>Never guess the test command: assuming a framework is how a run reports success over
    /// a filter that matched nothing.</summary>
    [Fact]
    public void Build_ForbidsGuessingTheTestCommand()
    {
        Assert.Contains("do not assume", Build(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The environment block: the cwd, whether it is a git repo, the platform and the date. Ours
    /// stated only the cwd. Each is a fact the model would otherwise guess at, and a wrong guess
    /// about the platform is how `find -printf` ends up on a mac.
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

    /// <summary>
    /// The instruction our old prompt got right, kept: text in a message changes nothing.
    ///
    /// <para>IT USED TO ASSERT THE TOOL NAMES, and that is what made it a test for the bug rather
    /// than the behaviour. The line ordered EVERY agent to "make changes with write_file or
    /// replace_in_file" unconditionally — correct until a selection withholds both, after which the
    /// prompt tells a read-only agent to use tools it does not have. The discipline is what
    /// matters; naming the tools is what tool definitions are for.</para>
    /// </summary>
    [Fact]
    public void Build_KeepsTheUseTheToolsInstruction()
    {
        var p = Build();

        Assert.Contains("make changes with the tools you have", p, StringComparison.Ordinal);

        // NO TOOL NAMED IN THE PROSE. A name here is unconditional text that a selection cannot
        // reach, which is the whole defect class this pins shut.
        Assert.DoesNotContain("write_file", p, StringComparison.Ordinal);
    }

    /// <summary>
    /// Conventions before code: never assume a library is available, look at neighbouring files.
    /// The model did this unprompted on the drive; that is not a guarantee.
    /// </summary>
    [Fact]
    public void Build_TellsTheModelToFollowExistingConventions()
    {
        Assert.Contains("convention", Build(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Terse by default, because every word of preamble is paid for on a local model and re-sent on
    /// every subsequent turn.
    /// </summary>
    [Fact]
    public void Build_AsksForShortAnswers()
    {
        Assert.Contains("concise", Build(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// We ship an http_request tool and said nothing about it. A fetch tool plus an invented URL is
    /// how an agent confidently reads a page that does not exist.
    /// </summary>
    [Fact]
    public void Build_ForbidsInventingAUrlForTheFetchTool()
    {
        var p = Build();

        // THE GUARDRAIL, NOT THE TOOL NAME. The rule is worth keeping for any agent that can reach
        // the network; the name belonged to a tool a selection may have withheld.
        Assert.Contains("never invent a url", p, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http_request", p, StringComparison.Ordinal);
    }

    /// <summary>
    /// NO COMMANDS UNLESS ONE ASKED. The prompt once named /help, /clear, /compress, /mcp and /exit
    /// unconditionally, costing tokens in every request of every session so the model could suggest
    /// what the USER drives anyway — and going stale as the table grew to eleven while the paragraph
    /// kept naming five.
    /// </summary>
    [Fact]
    public void Build_SaysNothingAboutCommandsWhenNoneAskedToBeNamed()
    {
        var p = Build();

        Assert.DoesNotContain("The user's commands", p, StringComparison.Ordinal);
        Assert.DoesNotContain("/compress", p, StringComparison.Ordinal);
        Assert.DoesNotContain("/help", p, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE ONE THAT ASKED IS NAMED WITH ITS SUMMARY. A command earns this by answering a dead
    /// end the model walks into — something needing a real terminal — which it can only suggest if
    /// it knows the name. The host chooses that name, so nothing here hardcodes one.
    /// </summary>
    [Fact]
    public void Build_NamesACommandThatAskedToBeNamed()
    {
        var p = SystemPrompt.Build(Context() with
        {
            ModelFacingCommands = [("/my_shell", "open a terminal for an interactive command")],
        });

        Assert.Contains("/my_shell", p, StringComparison.Ordinal);
        Assert.Contains("open a terminal for an interactive command", p, StringComparison.Ordinal);
    }

    /// <summary>Every turn is a round trip to a local model, so three serial reads cost three
    /// waits.</summary>
    [Fact]
    public void Build_AsksForIndependentToolCallsInOneTurn()
    {
        Assert.Contains("one round trip", Build(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// EXPLAIN A SHELL COMMAND BEFORE RUNNING IT. run_shell goes through a permission prompt, which
    /// shows the command TRUNCATED and carries no reason. Observed on the ConsoleEx drive — two
    /// commands approved on their visible prefix alone, one of which turned out to match zero tests.
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
    /// ONE PROMPT, WITH A SEAM. Variants earn their place when a model family shows a real quirk;
    /// this app has one endpoint. The selector exists so a second prompt is a file rather than a
    /// refactor — but adding variants nobody has measured a need for is guessing.
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
    /// changes throws that away for the whole conversation. Naming the model in the prompt is safe
    /// only when a session cannot switch model — and it is content that varies for no benefit to the
    /// model's reasoning, so it stays out. <c>Build</c> takes the id only to choose a variant
    /// later.</para>
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

    /// <summary>
    /// A plugin's guidance reaches the prompt, attributed to the TOOLS it governs.
    ///
    /// <para>NOT TO THE PLUGIN'S NAME ALONE, which is what the MCP section does for servers. The
    /// model sees a flat list of tools and has no concept of a plugin, so "from the 'lsp-rust'
    /// plugin" names something it cannot act on. Two language-server plugins both say something
    /// about positions; only the tool names say which claim governs which call.</para>
    /// </summary>
    [Fact]
    public void Build_IncludesEachPluginsInstructionsUnderItsToolNames()
    {
        var p = SystemPrompt.Build(Context() with
        {
            PluginInstructions =
            [
                new CxAgent.Core.Plugins.PluginInstructions(
                    "lsp-rust", ["rust_definition", "rust_references"], "Positions are 1-based."),
            ],
        });

        Assert.Contains("Positions are 1-based.", p, StringComparison.Ordinal);
        Assert.Contains("rust_definition, rust_references:", p, StringComparison.Ordinal);
    }

    /// <summary>
    /// A plugin with NO TOOLS still contributes its guidance, attributed to itself.
    ///
    /// <para>THE SHAPE THAT MAKES A PROMPT-ONLY PLUGIN POSSIBLE. A plugin declaring an empty tools
    /// list loads, offers nothing callable, and contributes only prose — house style, a project's
    /// conventions, anything a repository wants every session to know. There is nothing to name in
    /// the heading, so it falls back to the plugin's own name: the reader still needs to know who
    /// said it, and "unattributed paragraph in a system prompt" reads as though the app itself
    /// did.</para>
    /// </summary>
    [Fact]
    public void Build_IncludesAPluginThatHasInstructionsButNoTools()
    {
        var p = SystemPrompt.Build(Context() with
        {
            PluginInstructions =
            [
                new CxAgent.Core.Plugins.PluginInstructions(
                    "house-style", [], "Prefer records over classes for data."),
            ],
        });

        Assert.Contains("Prefer records over classes for data.", p, StringComparison.Ordinal);
        Assert.Contains("From the 'house-style' plugin:", p, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sorted by plugin name, so the prefix does not churn.
    ///
    /// <para>Plugins are held in LOAD ORDER, which is stable within a session and differs between
    /// sessions when two are loaded in a different sequence. An unsorted render would produce a
    /// different prompt for the same set of plugins and invalidate the cache for no change in
    /// content — the same reason the MCP section sorts its servers.</para>
    /// </summary>
    [Fact]
    public void Build_SortsPluginInstructionsByName()
    {
        CxAgent.Core.Plugins.PluginInstructions Block(string name) =>
            new(name, [$"{name}_tool"], $"Guidance from {name}.");

        var loadedOneWay = SystemPrompt.Build(Context() with
        {
            PluginInstructions = [Block("zebra"), Block("alpha")],
        });
        var loadedTheOther = SystemPrompt.Build(Context() with
        {
            PluginInstructions = [Block("alpha"), Block("zebra")],
        });

        Assert.Equal(loadedOneWay, loadedTheOther);
        Assert.True(loadedOneWay.IndexOf("Guidance from alpha.", StringComparison.Ordinal)
                  < loadedOneWay.IndexOf("Guidance from zebra.", StringComparison.Ordinal));
    }

    /// <summary>No plugins, no section — the same early return the MCP block makes, for the same
    /// reason: a heading every prompt carries whether or not it has content is permanent weight.</summary>
    [Fact]
    public void Build_OmitsThePluginSectionWhenThereAreNone()
    {
        var p = SystemPrompt.Build(Context());

        Assert.DoesNotContain("# Plugins", p, StringComparison.Ordinal);
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

        Assert.DoesNotContain("The user's commands", child, StringComparison.Ordinal);

        // AND NOT EVEN ONE THAT ASKED. A child has no user to type a command and no composer to
        // type it into, so the exclusion is not about which commands — it is about the reader.
        var childToldOfOne = SystemPrompt.Build(Context() with
        {
            IsSubAgent = true,
            ModelFacingCommands = [("/my_shell", "open a terminal")],
        });
        Assert.DoesNotContain("/my_shell", childToldOfOne, StringComparison.Ordinal);
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
    /// <para>Measured without this line: asked "across this whole repository, which controls
    /// implement their own keyboard handling — I don't know where they are", the model made TWELVE
    /// read_file calls and spawned nothing, on the exact shape of task its tool description says to
    /// delegate. The description alone does not move it.</para>
    ///
    /// <para>ONE NUDGE, phrased as a rule about where the reading LANDS rather than as a preference
    /// for a tool: a model can check "am I about to read files I will not need afterwards" against
    /// what it is doing.</para>
    /// </summary>
    /// <summary>
    /// THE CONTEXT RULE, and it is here because three statements of it elsewhere did not land.
    ///
    /// <para>The spawn tool has said "Put the TASK in prompt, and what you already KNOW in context"
    /// since spawning existed, and the explore and planner type descriptions repeat it. Measured
    /// across three consecutive drives, the parent wrote "Here's what I know about the codebase:" —
    /// entry points, line numbers, a data-flow summary — into the PROMPT, with context empty, every
    /// time. A planner given its facts that way spent ten turns re-reading what it had been told.</para>
    ///
    /// <para>Pinned because a one-line prompt rule is exactly what gets tidied away, and the failure
    /// it prevents is invisible: an agent that rediscovers what it was given looks like an agent
    /// working, not an agent wasting its run.</para>
    /// </summary>
    [Fact]
    public void FanOut_TellsTheModelWhereKnownFactsGo()
    {
        var fanOut = Parent();

        Assert.Contains("put it in `context`, not in the prompt", fanOut, StringComparison.Ordinal);
        Assert.Contains("rediscovers them", fanOut, StringComparison.Ordinal);
    }

    /// <summary>Single mode cannot spawn, so the rule is noise in its prompt — the same gating every
    /// other spawn line gets.</summary>
    [Fact]
    public void SingleMode_DoesNotCarryTheContextRule()
    {
        Assert.DoesNotContain("put it in `context`, not in the prompt", SingleModeParent(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FanOut_TellsTheModelToDelegateWideReading()
    {
        var fanOut = Parent();

        Assert.Contains("send a sub-agent rather", fanOut, StringComparison.Ordinal);
        Assert.Contains("only when you already know the file", fanOut, StringComparison.Ordinal);

        // WORKED EXAMPLES. Two interventions had already failed to move this model — a sharpened
        // tool description and the rule above — and an example is the lever neither of them had: it
        // shows the SHAPE of the decision rather than asserting it.
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

        // TYPE-MATCHING AS A REASON TO DELEGATE. It names no type: the catalog belongs in the tool
        // description, read at the moment of choosing (D25), and this is the judgement rule that
        // sits beside the others.
        Assert.Contains("If one of the agent types you are offered fits", fanOut, StringComparison.Ordinal);

        // FOUR IS THE CEILING, asserted so it stays one. Four is what the two distinctions take —
        // a counter-example and the split — and past four they stop being a pattern and become a
        // list nobody reads.
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

    /// <summary>
    /// The prompt does not restate the tool's own description.
    ///
    /// <para>ASSERTED ON A PHRASE, NOT THE TOOL NAME. The tool is called <c>task</c>, and "task" is
    /// an ordinary English word the prompt uses in its own sentences ("fits the task at hand"), so a
    /// bare-word check would fail on prose that has nothing to do with the tool.</para>
    /// </summary>
    [Fact]
    public void Parent_IsNotToldWhenToSpawn()
    {
        var parent = Parent();

        Assert.DoesNotContain("task(", parent, StringComparison.Ordinal);
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

    // ---- Skills -----------------------------------------------------------------------------

    private static SkillInfo Skill(string name, string description = "Use when testing.") =>
        new(name, description, $"/tmp/skills/{name}", "# Body\n\nDo it.");

    private static string WithSkills(params SkillInfo[] skills) =>
        SystemPrompt.Build(Context() with { Skills = skills });

    /// <summary>
    /// THE CATALOG IS NAME AND DESCRIPTION ONLY. The body is what the load tool fetches; putting it
    /// here would defeat the entire point — twenty skills of 3k each is 60k of permanent prefix.
    /// </summary>
    [Fact]
    public void Build_RendersTheSkillCatalog_WithoutTheBodies()
    {
        var p = WithSkills(Skill("rtl-aware-development", "Use when implementing RTL/LTR behaviour."));

        Assert.Contains("<name>rtl-aware-development</name>", p, StringComparison.Ordinal);
        Assert.Contains("Use when implementing RTL/LTR behaviour.", p, StringComparison.Ordinal);
        Assert.DoesNotContain("Do it.", p, StringComparison.Ordinal);
    }

    /// <summary>
    /// OMITTED ENTIRELY WITH NO SKILLS — not an empty heading, not "none available". Most sessions
    /// have none, and an empty section is permanent prefix bytes charged to every one of them for a
    /// capability nobody is using. A suite that only ever builds WITH skills never notices the leak.
    /// </summary>
    [Fact]
    public void Build_WithNoSkills_HasNoSkillsSectionAtAll()
    {
        var p = Build();

        Assert.DoesNotContain("# Skills", p, StringComparison.Ordinal);
        Assert.DoesNotContain("available_skills", p, StringComparison.Ordinal);
        Assert.DoesNotContain("skill", p, StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERY AGENT GETS A CATALOG — the main agent is the ordinary case, not an exception, and a
    /// child that cannot see skills would have to be told about them by its parent in prose.
    /// </summary>
    [Fact]
    public void Build_RendersSkills_ForAChildAndForBothParentModes()
    {
        var skills = new[] { Skill("planner-notes") };

        foreach (var p in new[]
                 {
                     SystemPrompt.Build(Context() with { Skills = skills, IsSubAgent = true }),
                     SystemPrompt.Build(Context() with { Skills = skills, CanSpawn = true }),
                     SystemPrompt.Build(Context() with { Skills = skills }),
                 })
            Assert.Contains("<name>planner-notes</name>", p, StringComparison.Ordinal);
    }

    /// <summary>
    /// BYTE-IDENTICAL FROM UNCHANGED INPUT. This text rides in the cached prompt prefix, and the
    /// catalog arrives from Directory.EnumerateDirectories — filesystem order, which is neither
    /// sorted nor stable across machines. Two builds differing would churn the prefix for free.
    /// </summary>
    [Fact]
    public void Build_WithTheSameSkills_IsByteIdenticalAcrossBuilds()
    {
        var skills = new[] { Skill("alpha"), Skill("middle"), Skill("zebra") };

        Assert.Equal(SystemPrompt.Build(Context() with { Skills = skills }),
                     SystemPrompt.Build(Context() with { Skills = skills }));
    }

    /// <summary>
    /// RENDERED IN THE ORDER GIVEN, which the catalog has already sorted. Re-sorting here would put
    /// the guarantee in two places, the second silently wrong the day the first changes.
    /// </summary>
    [Fact]
    public void Build_RendersSkillsInTheOrderSupplied()
    {
        var p = WithSkills(Skill("alpha"), Skill("middle"), Skill("zebra"));

        var a = p.IndexOf("alpha", StringComparison.Ordinal);
        var m = p.IndexOf("middle", StringComparison.Ordinal);
        var z = p.IndexOf("zebra", StringComparison.Ordinal);

        Assert.True(a < m && m < z, "the catalog must render in the order the discovery sorted it");
    }

    /// <summary>The model needs to know HOW to load one, or a catalog is a list of things it cannot
    /// reach.</summary>
    [Fact]
    public void Build_WithSkills_NamesTheToolThatLoadsThem()
    {
        Assert.Contains("skill", WithSkills(Skill("anything")), StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THAT A FILE TOOL IS NOT THE WAY. Naming the tool proved not to be enough: on the first
    /// live drive the model located the SKILL.md with list_files and read it with read_file — the
    /// tool it reaches for whenever it holds a path. The instructions arrived, so the work was
    /// correct and every downstream surface was wrong; without the marker the worker row, the panel
    /// and the compaction notice all reported that no skill was in force.
    /// </summary>
    [Fact]
    public void Build_WithSkills_TellsTheModelNotToReadTheFileDirectly()
    {
        var p = WithSkills(Skill("anything"));

        Assert.Contains("never a file tool", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SKILL.md", p, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE TASK LIST IS NAMED, because nothing else names it at the moment it would help. The list
    /// renders nothing while empty, so an agent that has never written one has no task-list message
    /// in its context and only a schema entry to go on — and a schema entry is not read the way an
    /// instruction is. Measured on one task: one run opened a list and worked through it, another
    /// never called the tool, lost track of which agents it had dispatched, and re-ran a finished
    /// pipeline from the start.
    /// </summary>
    [Fact]
    public void Build_WhenItCanPlan_NamesTheTaskListTool()
    {
        var p = SystemPrompt.Build(Context() with { CanPlan = true });

        Assert.Contains("todowrite", p, StringComparison.Ordinal);
        Assert.Contains("delegated", p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And says nothing when the tool was withheld. A selection can drop todowrite, and telling an
    /// agent to keep a list it cannot write is the same defect as offering a read-only agent
    /// write_file — which is why the block above it deliberately names no tool at all.
    /// </summary>
    [Fact]
    public void Build_WhenItCannotPlan_DoesNotMentionTheTaskListTool()
    {
        var p = SystemPrompt.Build(Context() with { CanPlan = false });

        Assert.DoesNotContain("todowrite", p, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE PROMPT HAS TO SAY THE TOOL EXISTS. On the first drive of question the model hit a
    /// genuinely ambiguous request, wrote "Which one should I change?" as its FINAL MESSAGE, and
    /// ended the turn — which reads as an answer, is not one, and costs the user a turn to repair.
    /// The tool was offered the whole time: a tool description is only read once the model is
    /// already considering that tool.
    /// </summary>
    [Fact]
    public void Build_WhenItCanAsk_SaysNotToEndTheTurnWithAQuestion()
    {
        var p = SystemPrompt.Build(Context() with { CanAskUser = true });

        Assert.Contains("question", p, StringComparison.Ordinal);
        Assert.Contains("end your turn with a question", p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And says nothing when there is nobody to ask. A child has no user, so the lines would
    /// describe machinery it cannot reach — which is what teaches a model to skim its prompt.
    /// </summary>
    /// <summary>
    /// Asserted on the INSTRUCTION, not the word. The tool is called <c>question</c> now, and
    /// "question" appears in unrelated prose ("nobody will answer a question, approve a plan") — so
    /// a bare-word check tests the English rather than the behaviour.
    /// </summary>
    [Fact]
    public void Build_WithNoWayToAsk_SaysNothingAboutAsking()
    {
        Assert.DoesNotContain("call question and wait", Build(), StringComparison.Ordinal);
        Assert.DoesNotContain("call question and wait", Child(), StringComparison.Ordinal);
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
