using CxAgent.Core.Sessions;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Reading the command line. A pure function over <c>string[]</c>, which is the point of extracting
/// it: <c>AppBootstrap.Run</c> builds a console driver and takes over the terminal, so nothing about
/// argument handling could be tested while it lived there.
/// </summary>
public class CommandLineTests
{
    /// <summary>
    /// NO ARGUMENTS IS FAN-OUT. The default was Single on the reasoning that delegation should be a
    /// choice a user makes rather than one they discover — but a capability nobody discovers is a
    /// capability nobody has, and this model reaches for `--mode fan-out` no more readily than it
    /// reaches for the spawn tool unprompted.
    ///
    /// <para>The asymmetry decides it: a fan-out session that never spawns has paid for a slightly
    /// longer system prompt, while a single-mode session that WANTED to delegate cannot, and gives no
    /// hint that it could. `/mode single` is one keystroke away for anyone who wants it.</para>
    /// </summary>
    [Fact]
    public void NoArguments_IsFanOut_WithNoMock()
    {
        var options = CommandLine.Parse([]);

        Assert.Equal(AgentMode.FanOut, options.Mode);
        Assert.False(options.UseMock);
        Assert.Null(options.Error);
    }

    [Theory]
    [InlineData("fan-out")]
    [InlineData("fanout")]
    public void ModeFanOut_StartsInFanOut(string value)
    {
        Assert.Equal(AgentMode.FanOut, CommandLine.Parse(["--mode", value]).Mode);
    }

    /// <summary>`--mode=x` is what scripts and shell completions generate; rejecting it would be a
    /// puzzle rather than a lesson.</summary>
    [Fact]
    public void TheEqualsForm_IsAccepted()
    {
        Assert.Equal(AgentMode.FanOut, CommandLine.Parse(["--mode=fan-out"]).Mode);
    }

    /// <summary>
    /// --mock IS A PROVIDER CHOICE, NOT A MODE, and the two are orthogonal on purpose: a mock session
    /// is exactly where someone drives the UI, and it must be able to do that in either mode.
    /// </summary>
    [Fact]
    public void MockAndMode_AreIndependent()
    {
        var options = CommandLine.Parse(["--mock", "--mode", "fan-out"]);

        Assert.True(options.UseMock);
        Assert.Equal(AgentMode.FanOut, options.Mode);
        Assert.Null(options.Error);
    }

    /// <summary>
    /// AN UNKNOWN MODE STOPS THE APP rather than defaulting. A user who typed a near-miss and
    /// silently got single mode concludes sub-agents are broken — the error must say what to type.
    /// </summary>
    [Fact]
    public void AnUnknownMode_IsAnError_ThatNamesTheValidValues()
    {
        var options = CommandLine.Parse(["--mode", "sideways"]);

        Assert.NotNull(options.Error);
        Assert.Contains("single", options.Error!, StringComparison.Ordinal);
        Assert.Contains("fan-out", options.Error!, StringComparison.Ordinal);
    }

    /// <summary>`--mode single` still opts out — the default changed, the flag did not.</summary>
    [Fact]
    public void ModeSingle_StillOptsOut()
    {
        Assert.Equal(AgentMode.Single, CommandLine.Parse(["--mode", "single"]).Mode);
    }

    [Fact]
    public void ModeWithNoValue_IsAnError()
    {
        Assert.NotNull(CommandLine.Parse(["--mode"]).Error);
    }

    /// <summary>An argument nobody understood is a typo, and starting anyway runs a session the user
    /// did not ask for.</summary>
    [Fact]
    public void AnUnknownArgument_IsAnError()
    {
        var options = CommandLine.Parse(["--fanout"]);

        Assert.NotNull(options.Error);
        Assert.Contains("--fanout", options.Error!, StringComparison.Ordinal);
    }

    /// <summary>The existing flag keeps working exactly as it did — this change adds an argument, it
    /// does not reshape the one that was there.</summary>
    [Fact]
    public void MockAlone_StillWorks()
    {
        var options = CommandLine.Parse(["--mock"]);

        Assert.True(options.UseMock);
        Assert.Equal(AgentMode.FanOut, options.Mode);   // the default, unaffected by --mock
        Assert.Null(options.Error);
    }

    // --- sessions and resume ---
    /// <summary>
    /// FOUR STATES, Claude Code's split: -c continues the most recent, -r ID resumes that one, and a
    /// bare --resume asks to pick — which the TUI answers by listing, never by guessing.
    /// </summary>
    [Fact]
    public void Resume_HasFourDistinctStates()
    {
        Assert.Equal(ResumeKind.None, CommandLine.Parse([]).Resume.Kind);
        Assert.Equal(ResumeKind.Continue, CommandLine.Parse(["-c"]).Resume.Kind);
        Assert.Equal(ResumeKind.Continue, CommandLine.Parse(["--continue"]).Resume.Kind);
        Assert.Equal(ResumeKind.Pick, CommandLine.Parse(["--resume"]).Resume.Kind);

        var named = CommandLine.Parse(["-r", "5PSCPG"]).Resume;
        Assert.Equal(ResumeKind.ById, named.Kind);
        Assert.Equal("5PSCPG", named.Uid);
    }



    [Fact]
    public void Resume_AcceptsTheEqualsForm()
    {
        Assert.Equal("5PSCPG", CommandLine.Parse(["--resume=5PSCPG"]).Resume.Uid);
    }

    [Fact]
    public void Resume_WithAnEmptyEqualsValue_IsAnError()
    {
        Assert.NotNull(CommandLine.Parse(["--resume="]).Error);
    }

    /// <summary>
    /// A FLAG IS NOT AN ID. `--resume --mock` asks to continue the most recent session with the mock
    /// provider; swallowing the flag would look for a session named "--mock" and silently drop the
    /// provider choice.
    /// </summary>
    [Fact]
    public void Resume_DoesNotSwallowTheFlagThatFollowsIt()
    {
        var options = CommandLine.Parse(["--resume", "--mock"]);

        Assert.True(options.Resume.Wanted);
        Assert.Null(options.Resume.Uid);
        Assert.True(options.UseMock);
        Assert.Null(options.Error);
    }

    [Fact]
    public void Sessions_AsksToPrintAndExit()
    {
        var options = CommandLine.Parse(["--sessions"]);

        Assert.True(options.ListSessions);
        Assert.False(options.ListAllSessions);
        Assert.Null(options.Error);
    }

    /// <summary>Spelled the way the command spells it, so one thing is learned once.</summary>
    [Fact]
    public void Sessions_TakesAllAsABareWord()
    {
        var options = CommandLine.Parse(["--sessions", "all"]);

        Assert.True(options.ListSessions);
        Assert.True(options.ListAllSessions);
        Assert.Null(options.Error);
    }

    /// <summary>
    /// ONE ASKS A QUESTION, THE OTHER STARTS WORK. Honouring both means doing half of what was typed
    /// with no way for the user to tell which half.
    /// </summary>
    [Fact]
    public void Sessions_CombinedWithResume_IsAnError()
    {
        Assert.NotNull(CommandLine.Parse(["--sessions", "--resume"]).Error);
    }

    [Fact]
    public void Sessions_CombinesWithTheOtherFlags()
    {
        var options = CommandLine.Parse(["--mock", "--resume", "5PSCPG", "--mode", "single"]);

        Assert.True(options.UseMock);
        Assert.Equal("5PSCPG", options.Resume.Uid);
        Assert.Equal(AgentMode.Single, options.Mode);
        Assert.Null(options.Error);
    }

    // --- version and model ---

    [Fact]
    public void Version_AsksToPrintAndExit()
    {
        Assert.True(CommandLine.Parse(["--version"]).ShowVersion);
        Assert.True(CommandLine.Parse(["-v"]).ShowVersion);
        Assert.False(CommandLine.Parse([]).ShowVersion);
    }

    /// <summary>
    /// --model NAMES A CONFIGURED INSTANCE, not a model id. A model belongs to an instance here,
    /// along with its endpoint and its context window — accepting a bare id would mean inventing an
    /// instance with no window, and an unknown window is the one thing that silently breaks
    /// compaction.
    /// </summary>
    [Fact]
    public void Model_TakesAnInstanceName_InBothForms()
    {
        Assert.Equal("claude", CommandLine.Parse(["--model", "claude"]).Instance);
        Assert.Equal("claude", CommandLine.Parse(["--model=claude"]).Instance);
        Assert.Null(CommandLine.Parse([]).Instance);
    }

    [Fact]
    public void Model_WithNoValue_IsAnError()
    {
        Assert.NotNull(CommandLine.Parse(["--model"]).Error);
        Assert.NotNull(CommandLine.Parse(["--model="]).Error);

        // A FLAG IS NOT A VALUE — "--model --mock" is a missing name, not an instance called --mock.
        Assert.NotNull(CommandLine.Parse(["--model", "--mock"]).Error);
    }

    [Fact]
    public void Model_CombinesWithTheOtherFlags()
    {
        var options = CommandLine.Parse(["--model", "claude", "--mode", "single"]);

        Assert.Equal("claude", options.Instance);
        Assert.Equal(AgentMode.Single, options.Mode);
        Assert.Null(options.Error);
    }

    // --- --config-dir -------------------------------------------------------------------

    [Fact]
    public void ConfigDirTakesTheNextArgument()
        => Assert.Equal("/tmp/scratch", CommandLine.Parse(["--config-dir", "/tmp/scratch"]).ConfigDir);

    [Fact]
    public void ConfigDirIsNullByDefault()
    {
        // NULL FALLS THROUGH to AppPaths' own resolution — XDG_CONFIG_HOME, then the platform
        // location. The flag must not change where cxagent looks when nobody passed it.
        Assert.Null(CommandLine.Parse([]).ConfigDir);
    }

    [Fact]
    public void ConfigDirWithoutAPathIsAnError()
    {
        // A BARE --config-dir MUST NOT SILENTLY MEAN "DEFAULT". Someone who typed the flag and
        // forgot the path wants to know, not to have their real config quietly used instead — this
        // flag exists precisely to keep a test run away from it.
        Assert.Contains("needs a directory", CommandLine.Parse(["--config-dir"]).Error ?? "");
    }

    [Fact]
    public void ConfigDirDoesNotSwallowTheNextFlag()
    {
        var options = CommandLine.Parse(["--config-dir", "--mock"]);

        Assert.Contains("needs a directory", options.Error ?? "");
    }

    [Fact]
    public void ConfigDirComposesWithOtherFlags()
    {
        var options = CommandLine.Parse(["--config-dir", "/tmp/x", "--mock", "--mode", "single"]);

        Assert.Equal("/tmp/x", options.ConfigDir);
        Assert.True(options.UseMock);
        Assert.Equal(AgentMode.Single, options.Mode);
    }

    [Fact]
    public void TheUnknownArgumentMessageListsIt()
    {
        // The message is the only discovery path — there is no --help.
        Assert.Contains("--config-dir", CommandLine.Parse(["--bogus"]).Error ?? "");
    }
}

/// <summary>Headless options: Claude Code's names, one name each, and refusals that cannot be mistaken for acceptance.</summary>
public class HeadlessCommandLineTests
{
    [Fact]
    public void Print_WithText_IsAHeadlessGoal()
    {
        var options = CommandLine.Parse(["-p", "summarise README"]);

        Assert.Null(options.Error);
        Assert.Equal("summarise README", options.Headless!.Goal);
        Assert.Equal(OutputFormat.Text, options.Headless.Output);
    }

    /// <summary>Claude Code's order works too: options first, the goal as the last word.</summary>
    [Fact]
    public void TheGoal_CanFollowTheOptions()
    {
        var options = CommandLine.Parse(["-p", "--output-format", "json", "summarise README"]);

        Assert.Null(options.Error);
        Assert.Equal("summarise README", options.Headless!.Goal);
        Assert.Equal(OutputFormat.Json, options.Headless.Output);
    }

    [Fact]
    public void AWordWithoutPrint_OrASecondGoal_IsStillAnError()
    {
        Assert.Contains("unknown argument", CommandLine.Parse(["summarise"]).Error);
        Assert.NotNull(CommandLine.Parse(["-p", "one", "two"]).Error);
    }

    [Fact]
    public void Print_WithoutText_ReadsTheGoalFromStdin()
    {
        var options = CommandLine.Parse(["--print", "--output-format", "json"]);

        Assert.Null(options.Error);
        Assert.Null(options.Headless!.Goal);
        Assert.Equal(OutputFormat.Json, options.Headless.Output);
    }

    [Theory]
    [InlineData("text", OutputFormat.Text)]
    [InlineData("json", OutputFormat.Json)]
    [InlineData("stream-json", OutputFormat.StreamJson)]
    public void OutputFormat_TakesClaudesValues(string value, OutputFormat expected) =>
        Assert.Equal(expected, CommandLine.Parse(["-p", "x", "--output-format", value]).Headless!.Output);

    [Theory]
    [InlineData("default", HeadlessPermissions.Default)]
    [InlineData("dontAsk", HeadlessPermissions.Default)]
    [InlineData("acceptEdits", HeadlessPermissions.AcceptEdits)]
    [InlineData("auto", HeadlessPermissions.Auto)]
    [InlineData("bypassPermissions", HeadlessPermissions.Bypass)]
    public void PermissionMode_MapsToCxagentsModes(string value, HeadlessPermissions expected) =>
        Assert.Equal(expected, CommandLine.Parse(["-p", "x", "--permission-mode", value]).Headless!.Permissions);

    [Fact]
    public void PlanMode_IsRefused() =>
        Assert.Contains("plan", CommandLine.Parse(["-p", "x", "--permission-mode", "plan"]).Error);

    [Fact]
    public void SkipPermissions_IsBypass_AndTrustsTheFolder()
    {
        var h = CommandLine.Parse(["-p", "x", "--dangerously-skip-permissions"]).Headless!;

        Assert.Equal(HeadlessPermissions.Bypass, h.Permissions);
        Assert.True(h.TrustsFolder);
    }

    [Fact]
    public void Plugins_AreRepeatableAndCommaSeparated()
    {
        var h = CommandLine.Parse(["-p", "x", "--plugin", "a,b", "--plugin", "c"]).Headless!;

        Assert.Equal(["a", "b", "c"], h.Plugins);
        Assert.False(h.NoPlugins);
        Assert.True(CommandLine.Parse(["-p", "x", "--plugin", "none"]).Headless!.NoPlugins);
    }

    [Fact]
    public void MaxTurns_MustBeAPositiveNumber()
    {
        Assert.Equal(7, CommandLine.Parse(["-p", "x", "--max-turns", "7"]).Headless!.MaxTurns);
        Assert.NotNull(CommandLine.Parse(["-p", "x", "--max-turns", "0"]).Error);
        Assert.NotNull(CommandLine.Parse(["-p", "x", "--max-turns", "lots"]).Error);
    }

    [Fact]
    public void Thinking_MustBeAKnownLevel()
    {
        Assert.Equal("high", CommandLine.Parse(["-p", "x", "--thinking", "high"]).Headless!.Thinking);
        foreach (string[] args in new string[][] { ["-p", "x", "--thinking"], ["-p", "x", "--thinking", "extreme"] })
            Assert.Contains("off, on, minimal, low, medium, high, xhigh, max", CommandLine.Parse(args).Error);
    }

    [Theory]
    [InlineData("--output-format", "json")]
    [InlineData("--trust", null)]
    [InlineData("--plugin", "a")]
    [InlineData("--max-turns", "3")]
    [InlineData("--thinking", "high")]
    [InlineData("--dangerously-skip-permissions", null)]
    [InlineData("--include-partial-messages", null)]
    [InlineData("--permission-mode", "auto")]
    public void HeadlessOnlyOptions_WithoutPrint_AreAUsageError(string option, string? value)
    {
        string[] args = value is null ? [option] : [option, value];

        Assert.Contains("-p", CommandLine.Parse(args).Error);
    }

    [Theory]
    [InlineData("--allowedTools")]
    [InlineData("--disallowedTools")]
    [InlineData("--tools")]
    [InlineData("--append-system-prompt")]
    [InlineData("--system-prompt")]
    [InlineData("--append-system-prompt-file")]
    [InlineData("--system-prompt-file")]
    [InlineData("--mcp-config")]
    [InlineData("--json-schema")]
    [InlineData("--max-budget-usd")]
    [InlineData("--bare")]
    [InlineData("--no-session-persistence")]
    [InlineData("--input-format")]
    public void ClaudeOptions_CxagentLacks_AreRefusedByName(string option) =>
        Assert.Equal($"{option} is not supported by cxagent",
            CommandLine.Parse(["-p", "x", option, "value"]).Error);

    [Fact]
    public void BareResume_WithPrint_PointsToContinue() =>
        Assert.Contains("-c", CommandLine.Parse(["-p", "x", "--resume"]).Error);

    [Fact]
    public void Sessions_WithPrint_IsAUsageError() =>
        Assert.NotNull(CommandLine.Parse(["-p", "x", "--sessions"]).Error);

    [Fact]
    public void Verbose_AndPartialMessages_AreRead()
    {
        var h = CommandLine.Parse(["-p", "x", "--output-format", "stream-json", "--verbose",
            "--include-partial-messages"]).Headless!;

        Assert.True(h.Verbose);
        Assert.True(h.IncludePartialMessages);
    }

    [Fact]
    public void Instance_IsUnknown_AsItAlwaysWas() =>
        Assert.Contains("unknown argument", CommandLine.Parse(["--instance", "x"]).Error);

    [Fact]
    public void TheHelp_ListsModel_NotInstance()
    {
        Assert.DoesNotContain("--instance", Usage.Text);
        Assert.Contains("--print", Usage.Text);
        Assert.Contains("--continue", Usage.Text);
    }
}
