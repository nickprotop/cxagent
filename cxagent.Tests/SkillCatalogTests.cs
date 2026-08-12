using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Discovery and parsing. The catalog rides in the prompt prefix and the body is loaded on demand, so
/// what matters here is: which directory wins, what is refused and WHY the user is told, and that the
/// output is stable enough to keep the prompt cache.
/// </summary>
public class SkillCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cxa-sk-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Dir(params string[] parts)
    {
        var p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>A skill folder with valid frontmatter and a body.</summary>
    private string Skill(string dir, string name, string description = "Use when testing.",
        string body = "# Heading\n\nDo the thing.")
    {
        var d = Directory.CreateDirectory(Path.Combine(dir, name)).FullName;
        File.WriteAllText(Path.Combine(d, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n\n{body}\n");
        return d;
    }

    private static void Raw(string dir, string name, string text)
    {
        var d = Directory.CreateDirectory(Path.Combine(dir, name)).FullName;
        File.WriteAllText(Path.Combine(d, "SKILL.md"), text);
    }

    [Fact]
    public void Find_ReadsAValidSkill_FromTheProjectDirectory()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        Skill(Dir("repo", ".cxagent", "skills"), "rtl-aware-development",
            "Use when implementing RTL/LTR behaviour.");

        var found = SkillCatalog.Find(repo);

        var only = Assert.Single(found.Skills);
        Assert.Equal("rtl-aware-development", only.Name);
        Assert.Contains("RTL/LTR", only.Description, StringComparison.Ordinal);
        Assert.Contains("Do the thing", only.Body, StringComparison.Ordinal);
        Assert.Empty(found.Problems);
        Assert.NotNull(found.SourceDirectory);
    }

    /// <summary>
    /// The description is the entire interface — the only thing the model sees before deciding. It is
    /// prose and contains colons; splitting on every colon truncates exactly the field that decides
    /// whether a skill is ever loaded.
    /// </summary>
    [Fact]
    public void Find_KeepsADescriptionContainingColons_Whole()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        Skill(Dir("repo", ".cxagent", "skills"), "add-projection",
            "Use when adding a projection: favour model-bound, fall back to IProjectionFor<T> only when needed.");

        var found = SkillCatalog.Find(repo);

        Assert.Contains("IProjectionFor<T> only when needed.",
            Assert.Single(found.Skills).Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// Published skills carry argument-hint and allowed-tools. Refusing unknown keys would make the
    /// recommended `ln -s .claude/skills .cxagent/skills` import nothing at all.
    /// </summary>
    [Fact]
    public void Find_IgnoresUnknownFrontmatterKeys_RatherThanRefusingTheSkill()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        Raw(Dir("repo", ".cxagent", "skills"), "doc-control",
            "---\nname: doc-control\ndescription: Generate docs.\nargument-hint: <ControlName>\n"
            + "allowed-tools: Read, Write, Edit\n---\n\nBody here.\n");

        var found = SkillCatalog.Find(repo);

        Assert.Equal("doc-control", Assert.Single(found.Skills).Name);
        Assert.Empty(found.Problems);
    }

    /// <summary>
    /// YAML QUOTES ARE STRIPPED. A description containing a colon must be quoted to be valid YAML,
    /// and "USE FOR: … DO NOT USE FOR: …" is exactly the shape a good description takes — so real
    /// published skills arrive quoted. Left in, the quotes reach the prompt and /skills verbatim.
    /// </summary>
    [Fact]
    public void Find_StripsSurroundingQuotesFromADescription()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        Raw(Dir("repo", ".cxagent", "skills"), "xunit",
            "---\nname: xunit\ndescription: \"Write tests. USE FOR: xUnit projects.\"\n---\n\nBody.\n");

        var found = SkillCatalog.Find(repo);

        Assert.Equal("Write tests. USE FOR: xUnit projects.", Assert.Single(found.Skills).Description);
    }

    /// <summary>But an apostrophe or a quoted phrase INSIDE a description is left exactly as written.</summary>
    [Fact]
    public void Find_LeavesQuotesThatAreNotWrapping_Alone()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        Raw(Dir("repo", ".cxagent", "skills"), "quoting",
            "---\nname: quoting\ndescription: Use when the repo's \"house style\" applies.\n---\n\nBody.\n");

        var found = SkillCatalog.Find(repo);

        Assert.Equal("Use when the repo's \"house style\" applies.",
            Assert.Single(found.Skills).Description);
    }

    [Fact]
    public void Find_RefusesAFileWithNoFrontmatter_AndSaysWhy()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        Raw(Dir("repo", ".cxagent", "skills"), "broken", "# Just a heading\n\nNo frontmatter here.\n");

        var found = SkillCatalog.Find(repo);

        Assert.Empty(found.Skills);
        Assert.Contains("frontmatter", Assert.Single(found.Problems).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Find_RefusesASkillWithNoDescription_BecauseNothingCouldEverMatchIt()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        Raw(Dir("repo", ".cxagent", "skills"), "nameless", "---\nname: nameless\n---\n\nA body.\n");

        var found = SkillCatalog.Find(repo);

        Assert.Empty(found.Skills);
        Assert.Contains("description", Assert.Single(found.Problems).Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Identity comes from the FOLDER. A frontmatter name that can disagree lets two directories
    /// declare one skill; the mismatch is reported rather than obeyed.
    /// </summary>
    [Fact]
    public void Find_TakesTheNameFromTheFolder_AndReportsAMismatch()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        Raw(Dir("repo", ".cxagent", "skills"), "actual-folder",
            "---\nname: something-else\ndescription: Use when testing.\n---\n\nBody.\n");

        var found = SkillCatalog.Find(repo);

        Assert.Equal("actual-folder", Assert.Single(found.Skills).Name);
        Assert.Contains("does not match", Assert.Single(found.Problems).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Find_PrefersDotCxagent_OverDotAgents()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        Skill(Dir("repo", ".cxagent", "skills"), "from-cxagent");
        Skill(Dir("repo", ".agents", "skills"), "from-agents");

        var found = SkillCatalog.Find(repo);

        Assert.Equal("from-cxagent", Assert.Single(found.Skills).Name);
    }

    /// <summary>
    /// "Exists" means HOLDS A SKILL, not Directory.Exists. An abandoned empty folder that silently
    /// switched off a populated one below it is the worst failure this could ship — and this repo
    /// already contains an empty .agents/skills, so the case is not hypothetical.
    /// </summary>
    [Fact]
    public void Find_AnEmptyWinner_DoesNotShadowAPopulatedFallback()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        Dir("repo", ".cxagent", "skills");                  // exists, holds nothing
        Skill(Dir("repo", ".agents", "skills"), "from-agents");

        var found = SkillCatalog.Find(repo);

        Assert.Equal("from-agents", Assert.Single(found.Skills).Name);
    }

    /// <summary>
    /// Shadowing decides which directory SUPPLIES skills, not which may REPORT problems. A malformed
    /// file in a losing directory is still a file the user wrote and expected to work.
    /// </summary>
    [Fact]
    public void Find_ReportsAMalformedFile_EvenInADirectoryThatLost()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        Skill(Dir("repo", ".cxagent", "skills"), "the-winner");
        Raw(Dir("repo", ".agents", "skills"), "the-loser", "no frontmatter at all\n");

        var found = SkillCatalog.Find(repo);

        Assert.Equal("the-winner", Assert.Single(found.Skills).Name);
        Assert.Contains(found.Problems, p => p.Path.Contains("the-loser", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every candidate malformed means NO winner. /skills must be able to say "no skills loaded"
    /// rather than claim a directory is in use — this is what a first attempt at writing one looks
    /// like, so it is the case that must read well.
    /// </summary>
    [Fact]
    public void Find_WhenEveryCandidateIsMalformed_HasNoSourceDirectory_ButReportsBoth()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        Raw(Dir("repo", ".cxagent", "skills"), "one", "broken\n");
        Raw(Dir("repo", ".agents", "skills"), "two", "also broken\n");

        var found = SkillCatalog.Find(repo);

        Assert.Empty(found.Skills);
        Assert.Null(found.SourceDirectory);
        Assert.Equal(2, found.Problems.Count);
    }

    [Fact]
    public void Find_FromASubdirectory_FindsTheRepoRootsSkills()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        Skill(Dir("repo", ".cxagent", "skills"), "at-the-root");
        var deep = Dir("repo", "src", "nested");

        var found = SkillCatalog.Find(deep);

        Assert.Equal("at-the-root", Assert.Single(found.Skills).Name);
    }

    /// <summary>
    /// NO REPO, NO WALK. Outside a worktree "the project" has no boundary, so climbing would let a
    /// scratch directory under the home folder load the home folder's skills — and a skill is text
    /// the model reads AND ACTS ON.
    /// </summary>
    [Fact]
    public void Find_WithNoGitAnywhere_ReadsTheWorkingDirectoryOnly()
    {
        Skill(Dir("loose", ".cxagent", "skills"), "from-the-parent");
        var here = Dir("loose", "work");

        var found = SkillCatalog.Find(here);

        Assert.Empty(found.Skills);
        Assert.Null(found.SourceDirectory);
    }

    /// <summary>
    /// A submodule and a linked worktree mark their root with a .git FILE holding a gitdir: pointer.
    /// Testing only for the directory walks straight past them and back out of the repo.
    /// </summary>
    [Fact]
    public void Find_TreatsAGitFileAsARepoRoot_NotOnlyAGitDirectory()
    {
        Skill(Dir("above", ".cxagent", "skills"), "outside-the-worktree");
        var root = Dir("above", "worktree");
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: /elsewhere/.git/worktrees/wt");
        Skill(Dir("above", "worktree", ".cxagent", "skills"), "the-worktrees-own");
        var deep = Dir("above", "worktree", "src");

        var found = SkillCatalog.Find(deep);

        Assert.Equal("the-worktrees-own", Assert.Single(found.Skills).Name);
    }

    /// <summary>Nearest wins: a package's own skills outrank the repo root's.</summary>
    [Fact]
    public void Find_PrefersTheNearestDirectory_OverTheRepoRoot()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        Skill(Dir("repo", ".cxagent", "skills"), "root-level");
        var package = Dir("repo", "packages", "web");
        Skill(Dir("repo", "packages", "web", ".cxagent", "skills"), "package-level");

        var found = SkillCatalog.Find(package);

        Assert.Equal("package-level", Assert.Single(found.Skills).Name);
    }

    /// <summary>Read when no project directory supplies any — cxagent's own config folder.</summary>
    [Fact]
    public void Find_FallsBackToTheGlobalDirectory_WhenTheProjectHasNone()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        var global = Dir("global");
        Skill(Dir("global", "skills"), "a-global-skill");

        var found = SkillCatalog.Find(repo, global);

        Assert.Equal("a-global-skill", Assert.Single(found.Skills).Name);
    }

    /// <summary>
    /// The catalog rides in the cached prompt prefix. Directory.EnumerateDirectories returns
    /// filesystem order — not sorted, and not stable across machines — so the sort is load-bearing
    /// rather than cosmetic.
    /// </summary>
    [Fact]
    public void Find_ReturnsSkillsSortedByName_SoThePromptPrefixIsStable()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        var skills = Dir("repo", ".cxagent", "skills");
        Skill(skills, "zebra");
        Skill(skills, "alpha");
        Skill(skills, "middle");

        var found = SkillCatalog.Find(repo);

        Assert.Equal(["alpha", "middle", "zebra"], found.Skills.Select(s => s.Name));
    }

    /// <summary>A folder without a SKILL.md is simply not a skill — not a problem to report.</summary>
    [Fact]
    public void Find_IgnoresASubdirectoryWithNoSkillFile_Silently()
    {
        var repo = Dir("repo");
        Dir("repo", ".git");
        var skills = Dir("repo", ".cxagent", "skills");
        Skill(skills, "real-one");
        Directory.CreateDirectory(Path.Combine(skills, "just-a-folder"));

        var found = SkillCatalog.Find(repo);

        Assert.Equal("real-one", Assert.Single(found.Skills).Name);
        Assert.Empty(found.Problems);
    }

    [Fact]
    public void Find_OnADirectoryThatDoesNotExist_ReturnsNothingAndDoesNotThrow()
    {
        var found = SkillCatalog.Find(Path.Combine(_root, "nope", "not-here"));

        Assert.Empty(found.Skills);
        Assert.Empty(found.Problems);
        Assert.Null(found.SourceDirectory);
    }
}
