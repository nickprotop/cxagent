using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Commands;
using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The two commands that ANSWER "what can this agent do" — and were answering about the catalog on
/// disk instead of the agent in front of the user.
///
/// <para>Both list things the model reaches through a tool: /skills through `skill`, /agents through
/// `agent`. Withhold the tool and the listing is still true and completely unreachable, which reads
/// as a menu. The same defect as the todo header naming a withheld todowrite, arriving on a surface
/// the user reads rather than the model.</para>
///
/// <para>WHAT IS NOT GATED, and why: /mcp, because MCP bypasses selection entirely by design — its
/// `enabled` flag is its control. /stats, because it reports what was CALLED in the past, and a
/// record of history does not become false when a tool is withheld today.</para>
/// </summary>
public class CommandSelectionTests
{
    // --- /skills -------------------------------------------------------------------------

    private static SkillCatalogResult OneSkill() => new(
        [new SkillInfo("writing-tests", "Use when writing tests.", "/tmp/skills/writing-tests", "body")],
        [], "/tmp/skills");

    [Fact]
    public void SkillsListsWithoutRemarkWhenTheSkillToolIsOffered()
    {
        var text = new SkillsCommand(OneSkill, skillToolOffered: true).Render();

        Assert.Contains("writing-tests", text);
        Assert.DoesNotContain("not offered", text);
    }

    [Fact]
    public void SkillsSaysTheToolIsNotOfferedWhenItIsWithheld()
    {
        var text = new SkillsCommand(OneSkill, skillToolOffered: false).Render();

        Assert.Contains("not offered", text);

        // THE LISTING STILL SHOWS. The files are on disk and the user may be debugging one; hiding
        // them would answer a different question than the one asked.
        Assert.Contains("writing-tests", text);
    }

    [Fact]
    public void TheDefaultIsOffered()
    {
        // Every existing caller constructs this with one argument, so the default decides what they
        // render. Withheld-by-default would put a warning on every unnarrowed session.
        Assert.DoesNotContain("not offered", new SkillsCommand(OneSkill).Render());
    }

    [Fact]
    public void SkillsSaysItWithNoSkillsOnDiskEither()
    {
        // THE BRANCH A FIRST VERSION MISSED. With nothing found, /skills ends by telling the user to
        // write one at .cxagent/skills/<name> — the worst advice available to someone whose agent
        // cannot load a skill however many they write. Found only because the wiring test ran
        // against an empty temp directory.
        var empty = new SkillCatalogResult([], [], null);

        Assert.Contains("not offered", new SkillsCommand(() => empty, skillToolOffered: false).Render());
    }

    // --- /agents -------------------------------------------------------------------------

    private static AgentTypeCatalog Catalog() =>
        new(new Dictionary<string, AgentTypeConfig>(), null);

    [Fact]
    public void AgentsListsWithoutRemarkWhenTheSpawnToolIsOffered()
        => Assert.DoesNotContain("cannot spawn",
            new AgentsCommand(Catalog(), spawnToolOffered: true).Render());

    [Fact]
    public void AgentsSaysTheAgentCannotSpawnWhenTheToolIsWithheld()
    {
        // "N available" IS ABOUT THE CATALOG. The types resolve fine; nothing can reach them.
        var text = new AgentsCommand(Catalog(), spawnToolOffered: false).Render();

        Assert.Contains("cannot spawn", text);
        Assert.Contains("Agent types", text);
    }

    [Fact]
    public void TheAgentsDefaultIsOffered()
        => Assert.DoesNotContain("cannot spawn", new AgentsCommand(Catalog()).Render());

    [Fact]
    public void ShowingOneTypeIsNotAnAvailabilityClaim()
    {
        // `/agents show <name>` asks what a type IS — its briefing and model. That answer does not
        // change with the selection, and a warning stapled to it would be noise on a question
        // nobody asked about availability.
        var text = new AgentsCommand(Catalog(), spawnToolOffered: false).Render("show explore");

        Assert.DoesNotContain("cannot spawn", text);
    }
}
