using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// One scan for command files and skills: nearest first, one entry per name, the winner's flags
/// deciding both what the user can type and what the model sees.
/// </summary>
public class InvocableCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "invocables-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _config;
    private readonly string _home;

    public InvocableCatalogTests()
    {
        _repo = Dir("repo");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        _config = Dir("config");
        _home = Dir("home");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (Exception) { } }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static string Command(string folder, string name, string text = "do it") =>
        Write(Path.Combine(folder, name + ".md"), text);

    private static string Skill(string skillsFolder, string name, string extra = "") =>
        Write(Path.Combine(skillsFolder, name, "SKILL.md"),
            $"---\ndescription: the {name} skill\n{extra}---\nbody of {name}\n");

    private InvocableCatalogResult Find(string? cwd = null) =>
        InvocableCatalog.Find(new CatalogRoots(cwd ?? _repo, _config, _home));

    [Fact]
    public void ACommandFile_WithNoFrontmatter_IsDescribedByItsFirstLine()
    {
        Command(Path.Combine(_repo, ".claude", "commands"), "review", "\nReview the change.\nThen more.");

        var review = Assert.Single(Find().Entries);

        Assert.Equal("review", review.Name);
        Assert.Equal(InvocableKind.Command, review.Kind);
        Assert.Equal("Review the change.", review.Description);
        Assert.True(review.Typeable);
    }

    [Fact]
    public void CxagentCommands_BeatClaudeCommands_AtOneLevel()
    {
        Command(Path.Combine(_repo, ".claude", "commands"), "review", "claude's");
        var mine = Command(Path.Combine(_repo, ".cxagent", "commands"), "review", "mine");

        var result = Find();

        Assert.Equal(mine, Assert.Single(result.Entries).Path);
        Assert.Equal(mine, Assert.Single(result.Shadowed).Winner.Path);
    }

    [Fact]
    public void ACommand_BeatsASkill_AtOneLevel_AndHidesItFromTheModel()
    {
        Skill(Path.Combine(_repo, ".cxagent", "skills"), "review");
        Command(Path.Combine(_repo, ".claude", "commands"), "review");

        var result = Find();

        Assert.Equal(InvocableKind.Command, Assert.Single(result.Entries).Kind);
        Assert.Empty(result.ModelSkills);
    }

    [Fact]
    public void ANearerSkill_BeatsAFartherCommand()
    {
        var sub = Dir("repo", "src", "api");
        Command(Path.Combine(_repo, ".claude", "commands"), "review");
        Skill(Path.Combine(sub, ".agents", "skills"), "review");

        Assert.Equal(InvocableKind.Skill, Assert.Single(Find(sub).Entries).Kind);
    }

    [Fact]
    public void TheProject_BeatsConfig_WhichBeatsHome()
    {
        Command(Path.Combine(_home, ".claude", "commands"), "a");
        Command(Path.Combine(_config, "commands"), "a");
        Command(Path.Combine(_home, ".claude", "commands"), "b");
        Command(Path.Combine(_config, "commands"), "b");
        var project = Command(Path.Combine(_repo, ".claude", "commands"), "a");

        var result = Find();

        Assert.Equal(project, result.Entries.Single(e => e.Name == "a").Path);
        Assert.StartsWith(_config, result.Entries.Single(e => e.Name == "b").Path);
        Assert.Equal(3, result.Shadowed.Count);
    }

    /// <summary>Skills now merge per name: a project's skills no longer hide the config dir's.</summary>
    [Fact]
    public void Skills_MergePerName_AcrossFolders()
    {
        Skill(Path.Combine(_repo, ".cxagent", "skills"), "one");
        Skill(Path.Combine(_config, "skills"), "two");
        Skill(Path.Combine(_home, ".claude", "skills"), "three");

        Assert.Equal(["one", "three", "two"], Find().ModelSkills.Select(s => s.Name).Order().ToArray());
    }

    [Fact]
    public void TheWalk_StopsAtTheRepoRoot()
    {
        Command(Path.Combine(_root, ".claude", "commands"), "outside");

        Assert.Empty(Find().Entries);
    }

    [Fact]
    public void WithNoRepo_OnlyTheWorkingFolderIsWalked()
    {
        var loose = Dir("loose", "deeper");
        Command(Path.Combine(_root, "loose", ".claude", "commands"), "parent");
        Command(Path.Combine(loose, ".claude", "commands"), "here");

        Assert.Equal(["here"], Find(loose).Entries.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void ASubfolder_IsALabel_NotPartOfTheName()
    {
        Command(Path.Combine(_repo, ".claude", "commands", "frontend"), "component");

        var entry = Assert.Single(Find().Entries);

        Assert.Equal("component", entry.Name);
        Assert.Equal("frontend", entry.Label);
    }

    [Fact]
    public void TwoSubfolders_DeclaringOneName_AtOneLevel_AreBothSkipped()
    {
        var commands = Path.Combine(_repo, ".claude", "commands");
        Command(Path.Combine(commands, "a"), "x");
        Command(Path.Combine(commands, "b"), "x");

        var result = Find();

        Assert.Empty(result.Entries);
        Assert.Equal(2, result.Problems.Count(p => p.Reason.Contains("more than one")));
    }

    [Fact]
    public void ACommandWithAnInvalidName_IsSkipped_AndReported()
    {
        Command(Path.Combine(_repo, ".claude", "commands"), "dotnet.test");

        var result = Find();

        Assert.Empty(result.Entries);
        Assert.Contains(result.Problems, p => p.Reason.Contains("letters, digits"));
    }

    /// <summary>The name rule is about typing: a skill named that way still loads for the model.</summary>
    [Fact]
    public void ASkillWithAnUntypeableName_StillReachesTheModel()
    {
        Skill(Path.Combine(_repo, ".agents", "skills"), "dotnet.testing");

        var result = Find();

        var skill = Assert.Single(result.Entries);
        Assert.False(skill.Typeable);
        Assert.Single(result.ModelSkills);
        Assert.Null(result.Typed("dotnet.testing"));
    }

    [Fact]
    public void YouOnly_IsTypeable_ButHiddenFromTheModel()
    {
        Skill(Path.Combine(_repo, ".cxagent", "skills"), "deploy", "disable-model-invocation: true\n");

        var result = Find();

        Assert.NotNull(result.Typed("deploy"));
        Assert.Empty(result.ModelSkills);
    }

    /// <summary>The winner decides both views: a nearer "you only" hides a farther loadable one.</summary>
    [Fact]
    public void ANearerYouOnly_HidesAFartherLoadableSkill_FromTheModel()
    {
        Skill(Path.Combine(_repo, ".cxagent", "skills"), "deploy", "disable-model-invocation: true\n");
        Skill(Path.Combine(_config, "skills"), "deploy");

        Assert.Empty(Find().ModelSkills);
    }

    [Fact]
    public void NotUserInvocable_CannotBeTyped()
    {
        Skill(Path.Combine(_repo, ".cxagent", "skills"), "style", "user-invocable: false\n");

        var result = Find();

        Assert.Null(result.Typed("style"));
        Assert.Single(result.ModelSkills);
    }

    [Fact]
    public void AllowedToolsAndModel_AreRecordedAsIgnored()
    {
        Command(Path.Combine(_repo, ".claude", "commands"), "commit",
            "---\ndescription: commit it\nallowed-tools: Bash(git add:*)\nmodel: claude-3-5-haiku\nargument-hint: [message]\n---\nCommit.");

        var entry = Assert.Single(Find().Entries);

        Assert.Equal(["allowed-tools", "model"], entry.IgnoredFields);
        Assert.Equal("[message]", entry.ArgumentHint);
        Assert.Equal("Commit.", entry.Body);
    }

    [Fact]
    public void UnclosedFrontmatter_IsSkipped_AndReported()
    {
        Command(Path.Combine(_repo, ".claude", "commands"), "broken", "---\ndescription: never closed\nbody");

        var result = Find();

        Assert.Empty(result.Entries);
        Assert.Contains(result.Problems, p => p.Reason.Contains("never closed"));
    }

    [Fact]
    public void Frontmatter_ToleratesBomAndCrlf()
    {
        Command(Path.Combine(_repo, ".claude", "commands"), "win",
            "﻿---\r\ndescription: from windows\r\n---\r\nBody line.\r\n");

        var entry = Assert.Single(Find().Entries);

        Assert.Equal("from windows", entry.Description);
        Assert.Equal("Body line.", entry.Body);
    }

    /// <summary>A repo rooted at home must not read ~/.claude twice.</summary>
    [Fact]
    public void AFolderReachedTwice_IsReadOnce()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".git"));
        Command(Path.Combine(_home, ".claude", "commands"), "dot");

        var result = InvocableCatalog.Find(new CatalogRoots(_home, _config, _home));

        Assert.Single(result.Entries);
        Assert.Empty(result.Shadowed);
    }

    /// <summary>A broken file in a LOSING folder is still reported.</summary>
    [Fact]
    public void ProblemsInLosingFolders_AreStillReported()
    {
        Command(Path.Combine(_repo, ".claude", "commands"), "x");
        Command(Path.Combine(_home, ".claude", "commands"), "y", "---\nnever closed");

        Assert.Single(Find().Problems);
    }

    [Fact]
    public void EachEntry_KnowsItsScope()
    {
        Command(Path.Combine(_repo, ".claude", "commands"), "p");
        Command(Path.Combine(_config, "commands"), "c");
        Command(Path.Combine(_home, ".claude", "commands"), "h");

        var result = Find();

        Assert.Equal(InvocableScope.Project, result.Entries.Single(e => e.Name == "p").Scope);
        Assert.Equal(InvocableScope.Config, result.Entries.Single(e => e.Name == "c").Scope);
        Assert.Equal(InvocableScope.Home, result.Entries.Single(e => e.Name == "h").Scope);
    }

    /// <summary>One unreadable subfolder costs that subfolder, not the folder around it.</summary>
    [Fact]
    public void AnUnreadableSubfolder_IsReported_AndTheRestStillLoads()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        var commands = Path.Combine(_repo, ".claude", "commands");
        Command(commands, "kept");
        var locked = Path.Combine(commands, "locked");
        Command(locked, "hidden");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var result = Find();

            Assert.Contains(result.Entries, e => e.Name == "kept");
            Assert.Contains(result.Problems, p => p.Path == locked);
        }
        finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    /// <summary>A symlink pointing a commands subfolder back at its parent does not walk for ever.</summary>
    [Fact]
    public void ASymlinkCycle_EndsTheWalk_WithAProblem()
    {
        if (OperatingSystem.IsWindows()) return;
        var commands = Path.Combine(_repo, ".claude", "commands");
        Command(commands, "ok");
        Directory.CreateSymbolicLink(Path.Combine(commands, "loop"), commands);

        // ENDING, AND SAYING WHY, is the whole point: each lap of the loop finds ok.md again, so the
        // copies clash with each other and the walk is reported rather than followed for ever.
        var result = Find();

        Assert.Contains(result.Problems, p => p.Reason.Contains("deeper than"));
    }
}
