using System.Diagnostics;
using CxAgent.Core.Colleagues;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// How much of a colleague's experience an incarnation starts with, and what counts as one codebase.
/// </summary>
public sealed class ColleagueBudgetTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("colleague-budget-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Budget_is_a_quarter_capped()
    {
        Assert.Equal(32_000, ExperienceBudget.For(128_000));
        Assert.Equal(50_000, ExperienceBudget.For(200_000));
        Assert.Equal(100_000, ExperienceBudget.For(1_000_000));
    }

    [Fact]
    public void Small_window_is_refused_naming_the_minimum()
    {
        var why = ExperienceBudget.WindowRefusal(64_000, "local:qwen");

        Assert.NotNull(why);
        Assert.Contains("128k", why);
        Assert.Contains("local:qwen", why);
        Assert.Null(ExperienceBudget.WindowRefusal(128_000, "local:qwen"));
    }

    [Fact]
    public void Unknown_window_is_refused_naming_the_setting() =>
        Assert.Contains("contextWindow", ExperienceBudget.WindowRefusal(null, "x"));

    [Fact]
    public void Preload_puts_core_then_current_then_recent_while_budget_lasts()
    {
        var block = new string('w', 4_000); // 1,000 tokens each
        var experience = new Experience("C" + block, new Dictionary<string, string>
        {
            ["a"] = "A" + block, ["b"] = "B" + block, ["c"] = "K" + block,
        });
        var now = DateTimeOffset.UtcNow;
        Workplace At(string key, int daysAgo) => new(key, "/x/" + key, 1, now.AddDays(-daysAgo));

        var text = ExperienceBudget.Preload(experience, "b", [At("c", 1), At("a", 2), At("b", 0)], budget: 3_100);

        var core = text.IndexOf("C" + block, StringComparison.Ordinal);
        var current = text.IndexOf("B" + block, StringComparison.Ordinal);
        var recent = text.IndexOf("K" + block, StringComparison.Ordinal);
        Assert.True(core >= 0 && current > core && recent > current);
        Assert.DoesNotContain("A" + block, text);
    }

    [Fact]
    public void Worktree_and_main_checkout_are_one_codebase()
    {
        var main = Path.Combine(_temp, "main");
        Directory.CreateDirectory(main);
        Git(main, "init -q");
        Git(main, "-c user.email=t@t -c user.name=t commit -q --allow-empty -m first");
        var tree = Path.Combine(_temp, "tree");
        Git(main, $"worktree add -q \"{tree}\"");

        Assert.Equal(Codebase.Key(main), Codebase.Key(tree));
        Assert.Equal(Codebase.Key(main), Codebase.Key(Path.Combine(main, ".")));
        Directory.CreateDirectory(Path.Combine(tree, "src"));
        Assert.Equal(Codebase.Key(main), Codebase.Key(Path.Combine(tree, "src")));
    }

    [Fact]
    public void Folder_outside_git_is_its_own_codebase()
    {
        var plain = Path.Combine(_temp, "plain");
        Directory.CreateDirectory(plain);

        Assert.Equal(plain, Codebase.Root(plain));
        Assert.StartsWith("plain-", Codebase.Key(plain));
    }

    private static void Git(string dir, string args)
    {
        using var git = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = dir, RedirectStandardError = true, RedirectStandardOutput = true })!;
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
    }
}
