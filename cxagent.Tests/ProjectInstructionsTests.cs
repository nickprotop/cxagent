using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Per-project instructions: AGENTS.md and friends, found by walking up from the working directory.
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

        Assert.NotNull(found);
        Assert.Contains("Comment heavily", found!.Text, StringComparison.Ordinal);
        Assert.EndsWith("AGENTS.md", found.Path, StringComparison.Ordinal);
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

        Assert.NotNull(found);
        Assert.Contains("root instructions", found!.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// FIRST MATCH WINS, and the nearest one is the match. opencode's comment: "so we don't stack
    /// AGENTS.md/CLAUDE.md from every ancestor" — stacking is how a context fills with advice from a
    /// parent directory that has nothing to do with the work.
    /// </summary>
    [Fact]
    public void Find_TakesTheNearestFile_NotEveryAncestor()
    {
        var root = Dir("outer");
        Write(root, "AGENTS.md", "OUTER");
        var inner = Dir("outer", "inner");
        Write(inner, "AGENTS.md", "INNER");

        var found = ProjectInstructions.Find(inner);

        Assert.NotNull(found);
        Assert.Contains("INNER", found!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTER", found.Text, StringComparison.Ordinal);
    }

    /// <summary>CLAUDE.md is read too — the same file under the name Claude Code uses, so a repo
    /// already carrying one does not have to duplicate it.</summary>
    [Fact]
    public void Find_AlsoReadsClaudeMd()
    {
        var d = Dir("proj");
        Write(d, "CLAUDE.md", "claude-style instructions");

        var found = ProjectInstructions.Find(d);

        Assert.NotNull(found);
        Assert.Contains("claude-style", found!.Text, StringComparison.Ordinal);
    }

    /// <summary>AGENTS.md wins when both exist: it is the vendor-neutral name, and a repo carrying
    /// both has chosen to keep them separate for a reason.</summary>
    [Fact]
    public void Find_PrefersAgentsMdOverClaudeMd()
    {
        var d = Dir("proj");
        Write(d, "AGENTS.md", "AGENTS wins");
        Write(d, "CLAUDE.md", "CLAUDE loses");

        var found = ProjectInstructions.Find(d);

        Assert.Contains("AGENTS wins", found!.Text, StringComparison.Ordinal);
    }

    /// <summary>No file, nothing to add. The common case, and it must cost nothing.</summary>
    [Fact]
    public void Find_ReturnsNull_WhenThereIsNoInstructionFile()
    {
        Assert.Null(ProjectInstructions.Find(Dir("bare")));
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

        Assert.Null(ProjectInstructions.Find(d));
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

        var found = ProjectInstructions.Find(d);

        Assert.NotNull(found);
        Assert.True(found!.Text.Length < 10_000, $"kept {found.Text.Length} chars");
        Assert.Contains("truncated", found.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unreadable path is not a crash — the agent runs without project instructions,
    /// exactly as it does today.</summary>
    [Fact]
    public void Find_OnAMissingDirectory_ReturnsNull()
    {
        Assert.Null(ProjectInstructions.Find(Path.Combine(_root, "does", "not", "exist")));
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
        Assert.Equal("", ProjectInstructions.Render(null));
    }
}
