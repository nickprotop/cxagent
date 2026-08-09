using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Project and user instructions: CXAGENT.md / AGENTS.md / CLAUDE.md, found by walking up from the
/// working directory, plus CXAGENT.md in cxagent's own config directory.
///
/// <para>WHY THIS EXISTS. Some instructions are true of a REPO, not of agents in general, and cannot
/// live in the universal system prompt. The example that forced it: opencode's prompt says "DO NOT
/// ADD ***ANY*** COMMENTS unless asked", while this codebase wants the opposite — heavy explanatory
/// comments carrying the reasoning behind a decision. Both are correct, for their own tree. Putting
/// either in the universal prompt is wrong for whoever points the agent somewhere else.</para>
///
/// <para>Shape copied from opencode (<c>session/instruction.ts</c>): a global file, then findUp from
/// the working directory, FIRST MATCH WINS — their comment says "so we don't stack AGENTS.md/CLAUDE.md
/// from every ancestor", which is how a context fills with stale advice from three levels up.</para>
/// </summary>
public class ProjectInstructionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cxa-pi-" + Guid.NewGuid().ToString("N"));

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

    private static void Write(string dir, string name, string text) =>
        File.WriteAllText(Path.Combine(dir, name), text);

    [Fact]
    public void Find_ReadsAgentsMdFromTheWorkingDirectory()
    {
        var d = Dir("proj");
        Write(d, "AGENTS.md", "Comment heavily. Explain the why.");

        var found = ProjectInstructions.Find(d);

        var only = Assert.Single(found);
        Assert.Contains("Comment heavily", only.Text, StringComparison.Ordinal);
        Assert.EndsWith("AGENTS.md", only.Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// WALKS UP. An agent run from a subdirectory of a repo is still working in that repo, and its
    /// instructions sit at the root.
    /// </summary>
    [Fact]
    public void Find_WalksUpToTheRepoRoot()
    {
        var root = Dir("repo");
        Write(root, "AGENTS.md", "root instructions");
        var deep = Dir("repo", "src", "nested");

        var found = ProjectInstructions.Find(deep);

        Assert.Contains("root instructions", Assert.Single(found).Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ONE NAME, BUT EVERY LEVEL THAT HAS IT — matching opencode exactly.
    ///
    /// <para>Their comment "the first project-level match wins so we don't stack AGENTS.md/CLAUDE.md
    /// from every ancestor" is about not stacking the two NAMES; their <c>findUp</c> collects every
    /// directory from the start to the worktree root, and <c>matches.forEach</c> adds them all. The
    /// monorepo case is why: a root file carries the house style and a package file carries what is
    /// specific to that package, and both are true at once.</para>
    ///
    /// <para>ROOT FIRST, so the nearest file is rendered last and wins on a conflict — the more
    /// specific claim.</para>
    /// </summary>
    [Fact]
    public void Find_TakesEveryLevelThatHasTheSameName_RootFirst()
    {
        var root = Dir("outer");
        Write(root, "AGENTS.md", "OUTER");
        var inner = Dir("outer", "inner");
        Write(inner, "AGENTS.md", "INNER");

        var found = ProjectInstructions.Find(inner);

        Assert.Equal(2, found.Count);
        Assert.Contains("OUTER", found[0].Text, StringComparison.Ordinal);
        Assert.Contains("INNER", found[1].Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// But only ONE name. A repo with AGENTS.md at the root and CLAUDE.md in a subdirectory gets the
    /// AGENTS.md — the first name that matches anywhere wins, and the other is not mixed in.
    /// </summary>
    [Fact]
    public void Find_DoesNotMixNames_AcrossLevels()
    {
        var root = Dir("outer");
        Write(root, "AGENTS.md", "AGENTS at root");
        var inner = Dir("outer", "inner");
        Write(inner, "CLAUDE.md", "CLAUDE in subdir");

        var found = ProjectInstructions.Find(inner);

        var only = Assert.Single(found);
        Assert.Contains("AGENTS at root", only.Text, StringComparison.Ordinal);
    }

    /// <summary>CLAUDE.md is read too — the same file under the name Claude Code uses, so a repo
    /// already carrying one does not have to duplicate it.</summary>
    [Fact]
    public void Find_AlsoReadsClaudeMd()
    {
        var d = Dir("proj");
        Write(d, "CLAUDE.md", "claude-style instructions");

        Assert.Contains("claude-style", Assert.Single(ProjectInstructions.Find(d)).Text,
            StringComparison.Ordinal);
    }

    /// <summary>AGENTS.md wins over CLAUDE.md: the vendor-neutral name, and a repo carrying both has
    /// chosen to keep them separate for a reason.</summary>
    [Fact]
    public void Find_PrefersAgentsMdOverClaudeMd()
    {
        var d = Dir("proj");
        Write(d, "AGENTS.md", "AGENTS wins");
        Write(d, "CLAUDE.md", "CLAUDE loses");

        Assert.Contains("AGENTS wins", Assert.Single(ProjectInstructions.Find(d)).Text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// CXAGENT.md WINS OVER BOTH. A repo can address this agent specifically — some instructions only
    /// make sense for one tool ("never run pkill, it kills the harness" is about cxagent's process
    /// model, not about agents generally), and a shared AGENTS.md is the wrong place for them because
    /// every other agent reads it too.
    /// </summary>
    [Fact]
    public void Find_PrefersCxagentMdOverEverything()
    {
        var d = Dir("proj");
        Write(d, "CXAGENT.md", "CXAGENT wins");
        Write(d, "AGENTS.md", "AGENTS loses");
        Write(d, "CLAUDE.md", "CLAUDE loses");

        var only = Assert.Single(ProjectInstructions.Find(d));

        Assert.Contains("CXAGENT wins", only.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("AGENTS loses", only.Text, StringComparison.Ordinal);
    }

    /// <summary>No CXAGENT.md is the ordinary case: the shared convention still applies, unchanged.</summary>
    [Fact]
    public void Find_WithoutACxagentMd_FallsBackToAgentsMd()
    {
        var d = Dir("proj");
        Write(d, "AGENTS.md", "shared convention");

        Assert.Contains("shared convention", Assert.Single(ProjectInstructions.Find(d)).Text,
            StringComparison.Ordinal);
    }

    /// <summary>No file, nothing to add. The common case, and it must cost nothing.</summary>
    [Fact]
    public void Find_ReturnsNull_WhenThereIsNoInstructionFile()
    {
        Assert.Empty(ProjectInstructions.Find(Dir("bare")));
    }

    /// <summary>
    /// AN EMPTY FILE IS NOT INSTRUCTIONS. A repo with a placeholder AGENTS.md would otherwise get a
    /// header announcing project instructions followed by nothing.
    /// </summary>
    [Fact]
    public void Find_IgnoresAnEmptyFile()
    {
        var d = Dir("proj");
        Write(d, "AGENTS.md", "   \n\n  ");

        Assert.Empty(ProjectInstructions.Find(d));
    }

    /// <summary>
    /// CAPPED. This rides in the cache prefix on every single turn, so an enormous AGENTS.md would be
    /// a permanent tax on the window. Truncated with a visible marker rather than silently.
    /// </summary>
    [Fact]
    public void Find_TruncatesAnEnormousFile_Visibly()
    {
        var d = Dir("proj");
        Write(d, "AGENTS.md", new string('x', 40_000));

        var only = Assert.Single(ProjectInstructions.Find(d));

        Assert.True(only.Text.Length < 10_000, $"kept {only.Text.Length} chars");
        Assert.Contains("truncated", only.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unreadable path is not a crash — the agent runs without project instructions,
    /// exactly as it does today.</summary>
    [Fact]
    public void Find_OnAMissingDirectory_ReturnsNull()
    {
        Assert.Empty(ProjectInstructions.Find(Path.Combine(_root, "does", "not", "exist")));
    }

    /// <summary>
    /// The block NAMES ITS SOURCE and says it wins. An unattributed paragraph appended to a system
    /// prompt reads as though the app said it, leaving the model no way to weigh a project rule
    /// against a general one.
    /// </summary>
    [Fact]
    public void Render_NamesTheFileAndSaysItTakesPrecedence()
    {
        var d = Dir("proj");
        Write(d, "AGENTS.md", "Comment heavily.");

        var rendered = ProjectInstructions.Render(ProjectInstructions.Find(d));

        Assert.Contains("AGENTS.md", rendered, StringComparison.Ordinal);
        Assert.Contains("follow these", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Comment heavily.", rendered, StringComparison.Ordinal);
    }

    /// <summary>Nothing found, nothing rendered — not an empty header.</summary>
    [Fact]
    public void Render_OfNothing_IsEmpty()
    {
        Assert.Equal("", ProjectInstructions.Render([]));
    }

    /// <summary>
    /// A GLOBAL FILE TOO, matching opencode: they read <c>~/.config/opencode/AGENTS.md</c> alongside
    /// the project's. It carries what is true of the USER wherever they work — house style, a
    /// preferred test runner — which a per-repo file cannot express and which they should not have to
    /// copy into every checkout.
    /// </summary>
    [Fact]
    public void Find_ReadsTheGlobalFile_WhenThereIsNoProjectOne()
    {
        var global = Dir("globalcfg");
        Write(global, "CXAGENT.md", "I always want British spelling.");
        var project = Dir("bare-project");

        var found = ProjectInstructions.Find(project, globalDirectory: global);

        Assert.Single(found);
        Assert.Contains("British spelling", found[0].Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// BOTH, when both exist, and the PROJECT comes last. Later text wins on a conflict, and a repo
    /// saying "tabs here" must override a global "spaces everywhere" — the repo is the more specific
    /// claim.
    /// </summary>
    [Fact]
    public void Find_ReturnsGlobalThenProject_SoTheProjectWins()
    {
        var global = Dir("globalcfg");
        Write(global, "CXAGENT.md", "GLOBAL RULE");
        var project = Dir("proj");
        Write(project, "AGENTS.md", "PROJECT RULE");

        var found = ProjectInstructions.Find(project, globalDirectory: global);

        Assert.Equal(2, found.Count);
        Assert.Contains("GLOBAL RULE", found[0].Text, StringComparison.Ordinal);
        Assert.Contains("PROJECT RULE", found[1].Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE GLOBAL FILE IS CXAGENT.md ONLY.
    ///
    /// <para>Only this app reads cxagent's config directory, so the shared names buy nothing there —
    /// an AGENTS.md at that path would be a vendor-neutral name in a vendor-specific location. The
    /// project directory is the opposite case, where several agents read one repo, and the shared
    /// names are honoured.</para>
    ///
    /// <para>And no CLAUDE.md at this level: a USER-level one is another product's configuration,
    /// written for a different agent with different tools. opencode reads <c>~/.claude/CLAUDE.md</c>;
    /// this deliberately does not.</para>
    /// </summary>
    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("CLAUDE.md")]
    public void Find_IgnoresAnyGlobalFileOtherThanCxagentMd(string name)
    {
        var global = Dir("globalcfg");
        Write(global, name, "not addressed to this app");
        var project = Dir("bare-project");

        Assert.Empty(ProjectInstructions.Find(project, globalDirectory: global));
    }

    /// <summary>No global directory configured is the ordinary case and must cost nothing.</summary>
    [Fact]
    public void Find_WithoutAGlobalDirectory_StillReadsTheProject()
    {
        var project = Dir("proj");
        Write(project, "AGENTS.md", "project only");

        var found = ProjectInstructions.Find(project);

        Assert.Single(found);
        Assert.Contains("project only", found[0].Text, StringComparison.Ordinal);
    }

    /// <summary>Both rendered, in order, each naming its own source.</summary>
    [Fact]
    public void Render_EmitsEveryFile_InOrder()
    {
        var global = Dir("globalcfg");
        Write(global, "CXAGENT.md", "GLOBAL RULE");
        var project = Dir("proj");
        Write(project, "AGENTS.md", "PROJECT RULE");

        var rendered = ProjectInstructions.Render(
            ProjectInstructions.Find(project, globalDirectory: global));

        Assert.True(rendered.IndexOf("GLOBAL RULE", StringComparison.Ordinal)
                  < rendered.IndexOf("PROJECT RULE", StringComparison.Ordinal),
            "the project block must come after the global one so it wins");
    }
}
