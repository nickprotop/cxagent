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

    public InvocableCatalogTests()
    {
        _repo = Dir("repo");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        _config = Dir("config");
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
        InvocableCatalog.Find(new CatalogRoots(cwd ?? _repo, _config));

    [Fact]
    public void ACommandFile_WithNoFrontmatter_IsDescribedByItsFirstLine()
    {
        Command(Path.Combine(_repo, ".cxagent", "commands"), "review", "\nReview the change.\nThen more.");

        var review = Assert.Single(Find().Entries);

        Assert.Equal("review", review.Name);
        Assert.Equal(InvocableKind.Command, review.Kind);
        Assert.Equal("Review the change.", review.Description);
        Assert.True(review.Typeable);
    }

    /// <summary>
    /// CLAUDE CODE'S FOLDERS ARE ITS OWN. A file there is written for another tool's commands and
    /// tools, and reading it here made cxagent run instructions that do not fit it — so neither a
    /// project's .claude nor the home one is a source, and nothing in them is reported either. Home
    /// is not reachable at all: the catalog's roots are the project and cxagent's config dir.
    /// </summary>
    [Fact]
    public void ClaudeCodeFolders_AreNotRead()
    {
        Command(Path.Combine(_repo, ".claude", "commands"), "review", "---\nnever closed");
        Skill(Path.Combine(_repo, ".claude", "skills"), "style");

        var result = Find();

        Assert.Empty(result.Entries);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void CxagentSkills_BeatAgentsSkills_AtOneLevel()
    {
        Skill(Path.Combine(_repo, ".agents", "skills"), "review");
        var mine = Skill(Path.Combine(_repo, ".cxagent", "skills"), "review");

        var result = Find();

        Assert.Equal(mine, Assert.Single(result.Entries).Path);
        Assert.Equal(mine, Assert.Single(result.Shadowed).Winner.Path);
    }

    [Fact]
    public void ACommand_BeatsASkill_AtOneLevel_AndHidesItFromTheModel()
    {
        Skill(Path.Combine(_repo, ".cxagent", "skills"), "review");
        Command(Path.Combine(_repo, ".cxagent", "commands"), "review");

        var result = Find();

        Assert.Equal(InvocableKind.Command, Assert.Single(result.Entries).Kind);
        Assert.Empty(result.ModelSkills);
    }

    [Fact]
    public void ANearerSkill_BeatsAFartherCommand()
    {
        var sub = Dir("repo", "src", "api");
        Command(Path.Combine(_repo, ".cxagent", "commands"), "review");
        Skill(Path.Combine(sub, ".agents", "skills"), "review");

        Assert.Equal(InvocableKind.Skill, Assert.Single(Find(sub).Entries).Kind);
    }

    [Fact]
    public void TheProject_BeatsConfig()
    {
        Command(Path.Combine(_config, "commands"), "a");
        Command(Path.Combine(_config, "commands"), "b");
        var project = Command(Path.Combine(_repo, ".cxagent", "commands"), "a");

        var result = Find();

        Assert.Equal(project, result.Entries.Single(e => e.Name == "a").Path);
        Assert.StartsWith(_config, result.Entries.Single(e => e.Name == "b").Path);
        Assert.Single(result.Shadowed);
    }

    /// <summary>Skills now merge per name: a project's skills no longer hide the config dir's.</summary>
    [Fact]
    public void Skills_MergePerName_AcrossFolders()
    {
        Skill(Path.Combine(_repo, ".cxagent", "skills"), "one");
        Skill(Path.Combine(_config, "skills"), "two");
        Skill(Path.Combine(_repo, ".agents", "skills"), "three");

        Assert.Equal(["one", "three", "two"], Find().ModelSkills.Select(s => s.Name).Order().ToArray());
    }

    [Fact]
    public void TheWalk_StopsAtTheRepoRoot()
    {
        Command(Path.Combine(_root, ".cxagent", "commands"), "outside");

        Assert.Empty(Find().Entries);
    }

    [Fact]
    public void WithNoRepo_OnlyTheWorkingFolderIsWalked()
    {
        var loose = Dir("loose", "deeper");
        Command(Path.Combine(_root, "loose", ".cxagent", "commands"), "parent");
        Command(Path.Combine(loose, ".cxagent", "commands"), "here");

        Assert.Equal(["here"], Find(loose).Entries.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void ASubfolder_IsALabel_NotPartOfTheName()
    {
        Command(Path.Combine(_repo, ".cxagent", "commands", "frontend"), "component");

        var entry = Assert.Single(Find().Entries);

        Assert.Equal("component", entry.Name);
        Assert.Equal("frontend", entry.Label);
    }

    [Fact]
    public void TwoSubfolders_DeclaringOneName_AtOneLevel_AreBothSkipped()
    {
        var commands = Path.Combine(_repo, ".cxagent", "commands");
        Command(Path.Combine(commands, "a"), "x");
        Command(Path.Combine(commands, "b"), "x");

        var result = Find();

        Assert.Empty(result.Entries);
        Assert.Equal(2, result.Problems.Count(p => p.Reason.Contains("more than one")));
    }

    [Fact]
    public void ACommandWithAnInvalidName_IsSkipped_AndReported()
    {
        Command(Path.Combine(_repo, ".cxagent", "commands"), "dotnet.test");

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
        Command(Path.Combine(_repo, ".cxagent", "commands"), "commit",
            "---\ndescription: commit it\nallowed-tools: Bash(git add:*)\nmodel: claude-3-5-haiku\nargument-hint: [message]\n---\nCommit.");

        var entry = Assert.Single(Find().Entries);

        Assert.Equal(["allowed-tools", "model"], entry.IgnoredFields);
        Assert.Equal("[message]", entry.ArgumentHint);
        Assert.Equal("Commit.", entry.Body);
    }

    [Fact]
    public void UnclosedFrontmatter_IsSkipped_AndReported()
    {
        Command(Path.Combine(_repo, ".cxagent", "commands"), "broken", "---\ndescription: never closed\nbody");

        var result = Find();

        Assert.Empty(result.Entries);
        Assert.Contains(result.Problems, p => p.Reason.Contains("never closed"));
    }

    [Fact]
    public void Frontmatter_ToleratesBomAndCrlf()
    {
        Command(Path.Combine(_repo, ".cxagent", "commands"), "win",
            "﻿---\r\ndescription: from windows\r\n---\r\nBody line.\r\n");

        var entry = Assert.Single(Find().Entries);

        Assert.Equal("from windows", entry.Description);
        Assert.Equal("Body line.", entry.Body);
    }

    /// <summary>.agents/skills linked to .cxagent/skills must not read that folder twice.</summary>
    [Fact]
    public void AFolderReachedTwice_IsReadOnce()
    {
        if (OperatingSystem.IsWindows()) return;
        Skill(Path.Combine(_repo, ".cxagent", "skills"), "dot");
        Directory.CreateDirectory(Path.Combine(_repo, ".agents"));
        Directory.CreateSymbolicLink(Path.Combine(_repo, ".agents", "skills"), Path.Combine(_repo, ".cxagent", "skills"));

        var result = Find();

        Assert.Single(result.Entries);
        Assert.Empty(result.Shadowed);
    }

    /// <summary>A broken file in a LOSING folder is still reported.</summary>
    [Fact]
    public void ProblemsInLosingFolders_AreStillReported()
    {
        Command(Path.Combine(_repo, ".cxagent", "commands"), "x");
        Command(Path.Combine(_config, "commands"), "y", "---\nnever closed");

        Assert.Single(Find().Problems);
    }

    [Fact]
    public void EachEntry_KnowsItsScope()
    {
        Command(Path.Combine(_repo, ".cxagent", "commands"), "p");
        Command(Path.Combine(_config, "commands"), "c");

        var result = Find();

        Assert.Equal(InvocableScope.Project, result.Entries.Single(e => e.Name == "p").Scope);
        Assert.Equal(InvocableScope.Config, result.Entries.Single(e => e.Name == "c").Scope);
    }

    /// <summary>One unreadable subfolder costs that subfolder, not the folder around it.</summary>
    [Fact]
    public void AnUnreadableSubfolder_IsReported_AndTheRestStillLoads()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        var commands = Path.Combine(_repo, ".cxagent", "commands");
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
        var commands = Path.Combine(_repo, ".cxagent", "commands");
        Command(commands, "ok");
        Directory.CreateSymbolicLink(Path.Combine(commands, "loop"), commands);

        // ENDING, AND SAYING WHY, is the whole point: the link back is reported and not entered, so ok.md is read
        // once rather than once per lap of the loop.
        var result = Find();

        Assert.Contains(result.Problems, p => p.Reason.Contains("link back"));
        Assert.Single(result.Entries, e => e.Name == "ok");
    }
}
