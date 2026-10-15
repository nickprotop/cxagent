using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using CxAgent.Headless;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class HeadlessPromptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hprompt-" + Guid.NewGuid().ToString("N"));
    public HeadlessPromptTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (Exception) { } }

    private PermissionRequest Req(PermissionKind kind, string display, string? rule = null)
    {
        var cfg = Path.Combine(_root, ".cfg");
        Directory.CreateDirectory(cfg);
        return new PermissionRequest(kind, display, rule)
        {
            Policy = new PermissionPolicy(_root, new PermissionRulesStore(new AppPaths(cfg))),
        };
    }

    [Fact]
    public async Task ByDefault_EverythingThatReachesThePrompt_IsDenied()
    {
        var prompt = new HeadlessPrompt(bypass: false);

        Assert.Equal(PermissionChoice.Deny, await prompt.Answer(Req(PermissionKind.Shell, "ls"), false, default));
        Assert.Equal(PermissionChoice.Deny,
            await prompt.Answer(Req(PermissionKind.FileWrite, Path.Combine(_root, "a.txt")), false, default));
    }

    [Fact]
    public async Task UnderBypass_ARequestIsApprovedOnce_NeverStored()
    {
        var prompt = new HeadlessPrompt(bypass: true);

        Assert.Equal(PermissionChoice.Once, await prompt.Answer(Req(PermissionKind.Shell, "git push"), true, default));
    }

    [Fact]
    public async Task UnderBypass_AProtectedWrite_IsStillDenied()
    {
        var prompt = new HeadlessPrompt(bypass: true);

        Assert.Equal(PermissionChoice.Deny, await prompt.Answer(
            Req(PermissionKind.FileWrite, Path.Combine(_root, ".git", "hooks", "pre-commit")), false, default));
    }

    /// <summary>
    /// A PROTECTED FOLDER ANYWHERE, not only at the top of the working folder: run from a subfolder,
    /// nested in a vendored checkout, or in the home directory, its contents still run later.
    /// </summary>
    [Theory]
    [InlineData("../.git/hooks/pre-commit")]
    [InlineData("vendor/lib/.git/hooks/post-merge")]
    [InlineData("~/.claude/settings.json")]
    public async Task UnderBypass_AProtectedFolderAnywhere_IsDenied(string target)
    {
        var sub = Path.Combine(_root, "src");
        Directory.CreateDirectory(sub);
        var path = target.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), target[2..])
            : Path.GetFullPath(Path.Combine(sub, target));
        var cfg = Path.Combine(_root, ".cfg");
        Directory.CreateDirectory(cfg);
        var request = new PermissionRequest(PermissionKind.FileWrite, path, null)
        {
            Policy = new PermissionPolicy(sub, new PermissionRulesStore(new AppPaths(cfg))),
        };

        Assert.Equal(PermissionChoice.Deny, await new HeadlessPrompt(bypass: true).Answer(request, false, default));
    }

    /// <summary>Bypass approves actions, not foreign code: a plugin needs a stored rule or --plugin.</summary>
    [Fact]
    public async Task UnderBypass_APluginLoad_IsDenied_UnlessApprovedForTheRun()
    {
        var prompt = new HeadlessPrompt(bypass: true);
        Assert.Equal(PermissionChoice.Deny,
            await prompt.Answer(Req(PermissionKind.Plugin, "load x", "HASH1"), false, default));

        prompt.ApprovePluginForRun("HASH1");
        Assert.Equal(PermissionChoice.Once,
            await prompt.Answer(Req(PermissionKind.Plugin, "load x", "HASH1"), false, default));
    }
}

public class HeadlessGoalTests
{
    private static ComposedGoal Compose(string? goal, string? stdin) =>
        HeadlessGoal.Compose(new GoalInput(goal, stdin is not null, new StringReader(stdin ?? "")));

    [Fact]
    public void AGoalAlone_IsTheGoal() => Assert.Equal("summarise", Compose("summarise", null).Text);

    [Fact]
    public void NoGoal_StdinIsTheGoal() => Assert.Equal("from a file", Compose(null, "from a file\n").Text);

    [Fact]
    public void AGoalAndStdin_AttachesStdinUnderTheGoal()
    {
        var text = Compose("review this", "diff --git a b").Text!;

        Assert.StartsWith("review this", text);
        Assert.Contains("Input piped to this run:", text);
        Assert.Contains("diff --git a b", text);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData(null, "  \n ")]
    public void NothingToRun_IsAnError(string? goal, string? stdin) => Assert.NotNull(Compose(goal, stdin).Error);

    [Fact]
    public void NonTextStdin_IsRefused() => Assert.Contains("not text", Compose("x", "a\0b").Error);

    /// <summary>An endless pipe must not be read forever: reading stops one past the cap.</summary>
    [Fact]
    public void AnEndlessPipe_IsCutAtTheCap()
    {
        var composed = HeadlessGoal.Compose(new GoalInput("x", true, new EndlessReader()));

        Assert.True(composed.Truncated);
        Assert.True(composed.Text!.Length < HeadlessGoal.PipeCap + 500);
    }

    private sealed class EndlessReader : TextReader
    {
        public override int Read(char[] buffer, int index, int count)
        {
            Array.Fill(buffer, 'y', index, count);
            return count;
        }
        public override int Read() => 'y';
        public override int Peek() => 'y';
    }
}

public class HeadlessEndsTests
{
    [Theory]
    [InlineData(SendOutcome.Completed, false, HeadlessEnd.Answered, 0, "success")]
    [InlineData(SendOutcome.Capped, false, HeadlessEnd.TurnLimit, 3, "error_max_turns")]
    [InlineData(SendOutcome.Failed, false, HeadlessEnd.Failed, 1, "error_during_execution")]
    [InlineData(SendOutcome.Stuck, false, HeadlessEnd.Failed, 1, "error_during_execution")]
    [InlineData(SendOutcome.Silent, false, HeadlessEnd.Failed, 1, "error_during_execution")]
    [InlineData(SendOutcome.Cancelled, false, HeadlessEnd.Interrupted, 130, "error_during_execution")]
    [InlineData(SendOutcome.Cancelled, true, HeadlessEnd.Terminated, 143, "error_during_execution")]
    public void Outcomes_MapToEnds_ExitCodes_AndSubtypes(SendOutcome outcome, bool terminated,
        HeadlessEnd end, int code, string subtype)
    {
        Assert.Equal(end, HeadlessEnds.From(outcome, terminated));
        Assert.Equal(code, HeadlessEnds.ExitCode(end));
        Assert.Equal(subtype, HeadlessEnds.Subtype(end));
    }
}

public class HeadlessPluginsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hplug-" + Guid.NewGuid().ToString("N"));
    public HeadlessPluginsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    private PluginSelection Selection(HeadlessOptions options, params string[] configured) => new(
        configured.ToDictionary(n => n, n => new PluginConfig(n + ".dll")),
        options, [_dir], new PermissionRulesStore(new AppPaths(_dir)), _dir);

    [Fact]
    public void ANamedPlugin_ThatIsNotConfigured_IsAnError() =>
        Assert.Contains("lsp", HeadlessPlugins.Select(Selection(
            new HeadlessOptions { Plugins = ["nope"] }, "lsp")).Error);

    [Fact]
    public void PluginNone_LoadsNothing()
    {
        var choice = HeadlessPlugins.Select(Selection(new HeadlessOptions { NoPlugins = true }, "lsp"));

        Assert.Empty(choice.Load);
        Assert.Null(choice.Error);
    }

    /// <summary>Unapproved and unnamed: skipped BEFORE its code runs, and listed.</summary>
    [Fact]
    public void AnUnapprovedPlugin_IsSkipped_AndListed()
    {
        var choice = HeadlessPlugins.Select(Selection(new HeadlessOptions(), "lsp"));

        Assert.Empty(choice.Load);
        Assert.Contains(choice.Skipped, s => s.StartsWith("lsp"));
    }

    /// <summary>Naming a plugin is the intent: enabled:false keeps it out by default, not out of reach.</summary>
    [Fact]
    public void ADisabledPlugin_NamedOnTheCommandLine_IsLoaded()
    {
        File.WriteAllText(Path.Combine(_dir, "lsp.dll"), "");
        var configured = new Dictionary<string, PluginConfig> { ["lsp"] = new("lsp.dll", Enabled: false) };

        var choice = HeadlessPlugins.Select(new PluginSelection(configured,
            new HeadlessOptions { Plugins = ["lsp"] }, [_dir], new PermissionRulesStore(new AppPaths(_dir)), _dir));

        Assert.True(choice.Load["lsp"].Enabled);
    }
}
