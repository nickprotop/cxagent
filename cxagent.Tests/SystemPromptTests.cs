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

    /// <summary>Committing is the user's decision. We hand the model run_shell, so nothing else
    /// stops it running `git commit`.</summary>
    [Fact]
    public void Build_TellsTheModelNotToCommitUnasked()
    {
        Assert.Contains("do not commit", Build(), StringComparison.OrdinalIgnoreCase);
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

    /// <summary>The prompt is a constant cost on every turn, re-sent in full each time. It has to
    /// stay worth its size on a local model's window.</summary>
    [Fact]
    public void Build_StaysUnderAReasonableSize()
    {
        Assert.True(Build().Length < 4_000, $"the system prompt grew to {Build().Length} chars");
    }
}
