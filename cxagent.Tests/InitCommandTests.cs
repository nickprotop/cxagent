using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <c>/init</c> — which file it writes, and what it asks for.
///
/// <para>The rule it exists to enforce: it edits the file that ALREADY GOVERNS, so what <c>/init</c>
/// writes is what the agent then reads. A second near-identical document beside an existing one is
/// how instructions rot.</para>
/// </summary>
public class InitCommandTests
{
    private const string Here = "/projects/here";

    private static Func<string, bool> OnDisk(params string[] names) =>
        path => names.Any(n => path.EndsWith(n, StringComparison.Ordinal));

    [Fact]
    public void WithNothingOnDiskItCreatesCxagentMd()
    {
        var target = InitCommand.Resolve(Here, OnDisk());

        Assert.EndsWith("CXAGENT.md", target.Path, StringComparison.Ordinal);
        Assert.False(target.Exists);
        Assert.Null(target.Note);
    }

    [Fact]
    public void WithCxagentMdItWritesIntoIt()
    {
        var target = InitCommand.Resolve(Here, OnDisk("CXAGENT.md"));

        Assert.EndsWith("CXAGENT.md", target.Path, StringComparison.Ordinal);
        Assert.True(target.Exists);
    }

    /// <summary>
    /// NEVER A SECOND FILE BESIDE AN EXISTING ONE. Writing CXAGENT.md next to an AGENTS.md produces
    /// two near-identical documents, one of which will rot — and the repository has already
    /// committed to the vendor-neutral name. Improving AGENTS.md in place benefits every agent that
    /// reads the repo, not only this one.
    /// </summary>
    [Fact]
    public void WithOnlyAgentsMdItWritesIntoAgentsMd()
    {
        var target = InitCommand.Resolve(Here, OnDisk("AGENTS.md"));

        Assert.EndsWith("AGENTS.md", target.Path, StringComparison.Ordinal);
        Assert.True(target.Exists);
    }

    /// <summary>With both, the resolver's own winner — so /init edits what the agent reads.</summary>
    [Fact]
    public void WithBothItPrefersCxagentMd()
    {
        var target = InitCommand.Resolve(Here, OnDisk("CXAGENT.md", "AGENTS.md"));

        Assert.EndsWith("CXAGENT.md", target.Path, StringComparison.Ordinal);
        Assert.True(target.Exists);
    }

    /// <summary>
    /// CLAUDE.md IS READ, NEVER WRITTEN. It is third in the resolver so a repository carrying only
    /// that one still works; seeding from it would mean copying another product's instructions into
    /// a file we maintain. Honouring it is a courtesy — treating it as ours to edit is not.
    /// </summary>
    [Fact]
    public void WithOnlyClaudeMdItWritesAFreshCxagentMdAndSaysWhy()
    {
        var target = InitCommand.Resolve(Here, OnDisk("CLAUDE.md"));

        Assert.EndsWith("CXAGENT.md", target.Path, StringComparison.Ordinal);
        Assert.False(target.Exists);
        Assert.Contains("CLAUDE.md", target.Note!);
    }

    /// <summary>An existing AGENTS.md still wins over a CLAUDE.md — it is higher in the resolver.</summary>
    [Fact]
    public void AgentsMdBeatsClaudeMd()
    {
        var target = InitCommand.Resolve(Here, OnDisk("AGENTS.md", "CLAUDE.md"));

        Assert.EndsWith("AGENTS.md", target.Path, StringComparison.Ordinal);
        Assert.True(target.Exists);
    }

    /// <summary>
    /// A NOTE ONLY WHEN THE CHOICE NEEDS EXPLAINING. CXAGENT.md winning over AGENTS.md is the
    /// resolver's documented behaviour, and saying so on every /init is noise.
    /// </summary>
    [Fact]
    public void TheOrdinaryChoicesAreMadeSilently()
    {
        Assert.Null(InitCommand.Resolve(Here, OnDisk("AGENTS.md")).Note);
        Assert.Null(InitCommand.Resolve(Here, OnDisk("CXAGENT.md", "AGENTS.md")).Note);
    }

    // --- the prompt ---

    /// <summary>
    /// MERGED, NEVER APPENDED. An existing file is the user's work and their words: preserve what is
    /// there, add only what is missing, and never restate something already said in different words.
    /// </summary>
    [Fact]
    public void AnExistingFileIsMergedRatherThanRewritten()
    {
        var prompt = InitCommand.Prompt(new InitCommand.Target("/p/AGENTS.md", Exists: true, null));

        Assert.Contains("AGENTS.md", prompt);
        Assert.Contains("already exists", prompt);
        Assert.Contains("preserve", prompt);
        Assert.DoesNotContain("Create `", prompt);
    }

    [Fact]
    public void AMissingFileIsCreated()
    {
        var prompt = InitCommand.Prompt(new InitCommand.Target("/p/CXAGENT.md", Exists: false, null));

        Assert.Contains("Create `CXAGENT.md`", prompt);
        Assert.DoesNotContain("already exists", prompt);
    }

    /// <summary>
    /// WHAT IS WORTH WRITING IS WHAT IS NOT DISCOVERABLE. Said explicitly because a model asked to
    /// "document the project" otherwise produces a summary of the directory tree — the one thing the
    /// reader can already see.
    /// </summary>
    [Fact]
    public void ThePromptAsksForWhatALookAroundCannotTell()
    {
        var prompt = InitCommand.Prompt(new InitCommand.Target("/p/CXAGENT.md", false, null));

        Assert.Contains("not discoverable", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("single test", prompt);
        Assert.Contains("tried and abandoned", prompt);
    }

    /// <summary>No invented sections, and nothing that is not in the repository.</summary>
    [Fact]
    public void ThePromptForbidsPadding()
    {
        var prompt = InitCommand.Prompt(new InitCommand.Target("/p/CXAGENT.md", false, null));

        Assert.Contains("Contributing", prompt);   // named as a thing NOT to invent
        Assert.Contains("do not write it down", prompt);
    }

    /// <summary>
    /// THE TWO LISTS MUST AGREE. /init writes the file the resolver reads; if the resolver learned a
    /// new name and this did not, /init would write a file the agent never looks at.
    /// </summary>
    [Fact]
    public void EveryFileTheResolverReadsIsOneInitKnowsAbout()
    {
        foreach (var name in CxAgent.Core.Llm.ProjectInstructions.ProjectFileNames)
        {
            var target = InitCommand.Resolve(Here, OnDisk(name));

            // Either it writes that file, or — for CLAUDE.md — it says why it will not.
            Assert.True(target.Path.EndsWith(name, StringComparison.Ordinal) || target.Note is not null,
                $"{name} is read by the resolver but /init neither writes it nor explains why");
        }
    }
}
