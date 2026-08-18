using System.Linq;
using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

public class BuiltinAgentTypesTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-builtin-" + Guid.NewGuid().ToString("N"));
    public BuiltinAgentTypesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private ProviderSettings Load(string json)
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), json);
        return ProviderConfigLoader.LoadAndValidate(new AppPaths(_dir), new Dictionary<string, string>());
    }

    // The five ship with the app now, so a fresh install has them without copying anything out of
    // config.sample.json. Before this they existed only for whoever had, which is why two briefing
    // fixes made in one session reached exactly one machine.
    [Fact]
    public void EveryShippedTypeHasBothTextsItNeeds()
    {
        Assert.NotEmpty(BuiltinAgentTypes.All);
        foreach (var t in BuiltinAgentTypes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Briefing), $"{t.Name} has no briefing");
            Assert.False(string.IsNullOrWhiteSpace(t.Description), $"{t.Name} has no description");
        }
    }

    // EXACTLY ONE TYPE WRITES A PLAN, and it is declared rather than inferred. The flag is what
    // makes the spawner hand out a path and then contradict an answer that claims a file nobody
    // wrote; setting it on a type whose briefing never mentions writing one would produce that
    // contradiction against a child that was never asked.
    [Fact]
    public void OnlyThePlannerDeclaresThatItWritesAPlanFile()
    {
        var writers = BuiltinAgentTypes.All.Where(t => t.WritesAPlanFile).Select(t => t.Name).ToList();
        Assert.Equal(["planner"], writers);
    }

    // THE CONTRACT, ASSERTED RATHER THAN HOPED FOR. The planner must be TOLD that its path comes
    // from context — the spawner supplies one and checks it, so a briefing that sent the child to
    // pick its own name would fail the check on a plan that was correctly written.
    [Fact]
    public void ThePlannerIsToldItsPathComesFromContext()
    {
        var planner = BuiltinAgentTypes.Find("planner");
        Assert.NotNull(planner);
        Assert.Contains("context names the", planner!.Briefing, StringComparison.Ordinal);

        // And the retired marker is gone from both, so nothing still instructs a child to announce
        // a path the parent does not read.
        Assert.DoesNotContain("PLAN WRITTEN:", planner.Briefing, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAN WRITTEN:", BuiltinAgentTypes.Find("builder")!.Briefing,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ABuiltinNameIsRecognised_AndAUsersOwnIsNot()
    {
        Assert.True(BuiltinAgentTypes.IsBuiltin("builder"));
        Assert.False(BuiltinAgentTypes.IsBuiltin("researcher"));
    }

    // IGNORED LOUDLY. A briefing under a built-in name does not apply and the user is told, because
    // an edit that does nothing and says nothing is the worst of the three options.
    [Fact]
    public void ConfigCannotRewriteAShippedBriefing_AndIsWarned()
    {
        var json = """
        {
          "providers": { "p": { "kind": "ollama", "model": "m" } },
          "defaultProvider": "p",
          "agents": { "builder": { "briefing": "ignore the plan and do what you like" } }
        }
        """;
        var settings = Load(json);

        Assert.Contains(settings.Warnings, w => w.Contains("agents.builder.briefing is ignored"));

        var catalog = new AgentTypeCatalog(settings.AgentTypes, null);
        var builder = catalog.Resolve("builder");
        Assert.NotNull(builder);
        Assert.DoesNotContain("do what you like", builder!.Briefing, StringComparison.Ordinal);
        Assert.Contains("You implement a plan that already exists", builder.Briefing,
            StringComparison.Ordinal);
    }

    // WHAT CONFIG STILL DECIDES. Where a type runs and what it may spend are genuinely the user's.
    [Fact]
    public void ConfigStillSetsMaxTurnsOnAShippedType()
    {
        var json = """
        {
          "providers": { "p": { "kind": "ollama", "model": "m" } },
          "defaultProvider": "p",
          "agents": { "builder": { "maxTurns": 7 } }
        }
        """;
        var settings = Load(json);
        var catalog = new AgentTypeCatalog(settings.AgentTypes, null);

        Assert.Equal(7, catalog.Resolve("builder")!.MaxTurns);
    }

    // THE PANEL COUNTS WHAT THE SESSION CAN DO, not what config happens to mention. Trimming a
    // config down to the keys that still apply — a maxTurns each — left two `agents` entries, and
    // the panel read those keys directly and reported three types on a session that had six.
    [Fact]
    public void EveryShippedTypeResolves_EvenWhenConfigNamesNoneOfThem()
    {
        var settings = Load("""
        {
          "providers": { "p": { "kind": "ollama", "model": "m" } },
          "defaultProvider": "p"
        }
        """);
        var catalog = new AgentTypeCatalog(settings.AgentTypes, null);

        foreach (var t in BuiltinAgentTypes.All)
            Assert.NotNull(catalog.Resolve(t.Name));
        Assert.NotNull(catalog.Resolve(AgentTypeCatalog.DefaultTypeName));
    }

    // The same, through the config shape a user is left with after the built-ins move into code.
    [Fact]
    public void ATrimmedConfigStillResolvesTheTypesItNoLongerMentions()
    {
        var settings = Load("""
        {
          "providers": { "p": { "kind": "ollama", "model": "m" } },
          "defaultProvider": "p",
          "agents": { "explore": { "maxTurns": 30 }, "planner": { "maxTurns": 40 } }
        }
        """);
        var catalog = new AgentTypeCatalog(settings.AgentTypes, null);

        Assert.NotNull(catalog.Resolve("builder"));   // never mentioned in that config
        Assert.NotNull(catalog.Resolve("review"));
        Assert.NotNull(catalog.Resolve("test"));
        Assert.Equal(30, catalog.Resolve("explore")!.MaxTurns);
        Assert.Equal(40, catalog.Resolve("planner")!.MaxTurns);
    }

    // A NAME NOBODY SHIPPED IS ENTIRELY THE USER'S, briefing required, exactly as before.
    [Fact]
    public void ACustomTypeKeepsItsOwnBriefing()
    {
        var json = """
        {
          "providers": { "p": { "kind": "ollama", "model": "m" } },
          "defaultProvider": "p",
          "agents": { "researcher": { "briefing": "You read papers.", "description": "for literature" } }
        }
        """;
        var settings = Load(json);
        var catalog = new AgentTypeCatalog(settings.AgentTypes, null);

        Assert.Equal("You read papers.", catalog.Resolve("researcher")!.Briefing);
        Assert.DoesNotContain(settings.Warnings, w => w.Contains("researcher"));
    }
}
