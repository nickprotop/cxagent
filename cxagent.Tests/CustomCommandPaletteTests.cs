using CxAgent.Core.Commands;
using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

public class CustomCommandPaletteTests
{
    private static Invocable Entry(string name, string? hint = null, string? label = null) =>
        new(name, InvocableKind.Command, $"/r/.cxagent/commands/{name}.md", "/r/.cxagent/commands", label,
            $"does {name}", hint, true, false, [], "b", null);

    [Fact]
    public void FromCatalog_MakesRows_WithSourceAndHint()
    {
        var rows = SessionCommands.FromCatalog(
            new InvocableCatalogResult([Entry("review", "[pr]", "frontend")], [], []), _ => false);

        var row = Assert.Single(rows);
        Assert.Equal("/review", row.Name);
        Assert.Contains("does review", row.Summary);
        Assert.Contains("frontend", row.Summary);
        Assert.Equal("[[pr]]", row.Hint);
    }

    [Fact]
    public void FromCatalog_LeavesOut_NamesTheRegistryHas() =>
        Assert.Empty(SessionCommands.FromCatalog(
            new InvocableCatalogResult([Entry("init")], [], []), n => n == "/init"));

    [Fact]
    public void Matching_IncludesExtraRows_AfterTheBuiltIns()
    {
        var extra = SessionCommands.FromCatalog(new InvocableCatalogResult([Entry("review")], [], []), _ => false);

        var hits = SessionCommands.Matching("/re", extra: extra);

        Assert.Contains(hits, c => c.Name == "/review");
    }

    [Fact]
    public void ArgumentsFor_ACustomCommand_ShowsItsHint()
    {
        var extra = SessionCommands.FromCatalog(new InvocableCatalogResult([Entry("review", "[pr]")], [], []), _ => false);

        var args = SessionCommands.ArgumentsFor("/review ", extra: extra);

        Assert.Equal("[pr]", Assert.Single(args).Name);
    }

    /// <summary>
    /// The row says where the file came from, because nearest-first means a cloned repo's review.md
    /// silently replaces yours — and the palette is where that is seen before it runs.
    /// </summary>
    [Fact]
    public void FromCatalog_TagsEachRow_WithWhereItCameFrom()
    {
        var config = Entry("cfg") with { Scope = InvocableScope.Config };
        var projectSkill = Entry("tidy") with { Kind = InvocableKind.Skill, ModelInvocable = true };

        var rows = SessionCommands.FromCatalog(new InvocableCatalogResult([config, projectSkill], [], []), _ => false);

        // FIRST, because the palette cuts a summary at its column width and a tag at the end is the
        // part that goes.
        Assert.StartsWith("config · ", rows.Single(r => r.Name == "/cfg").Summary);
        Assert.StartsWith("project · skill · ", rows.Single(r => r.Name == "/tidy").Summary);
    }

    /// <summary>
    /// The name column fits the longest name shown — a custom command is often longer than the
    /// built-ins the fixed width was chosen for — within a cap that keeps room for the summaries.
    /// </summary>
    [Theory]
    [InlineData(new[] { "/clear", "/mode" }, 12)]
    [InlineData(new[] { "/clear", "/review-branch" }, 14)]
    [InlineData(new[] { "/a-very-long-command-name-indeed" }, 24)]
    public void TheNameColumn_FitsTheLongestName(string[] labels, int expected) =>
        Assert.Equal(expected, CxAgent.UI.CommandMenu.NameColumnFor(labels));
}
