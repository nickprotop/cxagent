using CxAgent.Core.Commands;
using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class AgentsCommandTests
{
    private static AgentTypeCatalog Catalog(params (string Name, string Briefing)[] types)
    {
        var configured = types.ToDictionary(
            t => t.Name,
            t => new AgentTypeConfig(t.Briefing),
            StringComparer.Ordinal);
        return new AgentTypeCatalog(configured, null);
    }

    // RENDERS, RATHER THAN PRINTING THROUGH A DOUBLE. The command took a transcript writer and this
    // supplied a fake to read back what it wrote; a listing that is pure text needs neither.
    private static string Run(AgentTypeCatalog catalog, string? arg = null) =>
        new AgentsCommand(catalog).Render(arg);

    [Fact]
    public void Bare_ListsEveryShippedTypeWithoutConfiguringAnything()
    {
        var text = Run(Catalog());

        foreach (var t in BuiltinAgentTypes.All)
            Assert.Contains(t.Name, text, StringComparison.Ordinal);
        Assert.Contains("general", text, StringComparison.Ordinal);
    }

    // THE LIST STAYS A LIST. Five shipped briefings total about 5,700 characters; printing them all
    // by default would bury the names they belong to.
    [Fact]
    public void Bare_ShowsDescriptionsButNotBriefings()
    {
        var text = Run(Catalog());
        var builder = BuiltinAgentTypes.Find("builder")!;

        Assert.Contains(builder.Description[..40], text, StringComparison.Ordinal);
        Assert.DoesNotContain(builder.Briefing[..40], text, StringComparison.Ordinal);
    }

    [Fact]
    public void Named_PrintsThatTypesBriefingInFull()
    {
        var text = Run(Catalog(), "builder");
        var builder = BuiltinAgentTypes.Find("builder")!;

        Assert.Contains(builder.Briefing[..60], text, StringComparison.Ordinal);
    }

    // A BUILT-IN NAME SAYS SO, because this is where a user looks after their config edit did
    // nothing — the startup warning fires once and is gone.
    [Fact]
    public void Named_SaysWhenTheTextShipsWithTheApp()
    {
        Assert.Contains("Built in", Run(Catalog(), "planner"), StringComparison.Ordinal);
    }

    [Fact]
    public void Named_ACustomTypeIsNotClaimedAsBuiltIn()
    {
        var text = Run(Catalog(("researcher", "You read papers.")), "researcher");

        Assert.Contains("You read papers.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Built in", text, StringComparison.Ordinal);
    }

    // A BLANK OR UNKNOWN NAME RESOLVES TO `general` in the catalog, so a typo would otherwise print
    // the default type's entry and look like a real answer.
    [Fact]
    public void Named_AnUnknownTypeIsRefused_NotSilentlyTheDefault()
    {
        var text = Run(Catalog(), "explorer");   // note: the type is `explore`

        Assert.Contains("No agent type 'explorer'", text, StringComparison.Ordinal);
        Assert.Contains("available:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Named_General_SaysItHasNoBriefingRatherThanPrintingNothing()
    {
        var text = Run(Catalog(), "general");
        Assert.Contains("No briefing", text, StringComparison.Ordinal);
    }

    // WHICH TYPE WRITES A PLAN FILE is otherwise invisible: it decides that the spawner names a path
    // and contradicts an answer claiming a file nobody wrote.
    [Fact]
    public void Bare_MarksTheTypeThatWritesAPlanFile()
    {
        Assert.Contains("writes a plan file", Run(Catalog()), StringComparison.Ordinal);
    }
}
