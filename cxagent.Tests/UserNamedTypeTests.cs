using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That a user naming a type outranks the model's own sizing judgement — stated in all three places
/// a model reads, because the evidence in this repo says one place is not enough.
///
/// <para>THE FAILURE THIS PINS, measured on a live drive: "spawn a planner, then spawn a builder to
/// carry out that plan" produced ONE agent call. The planner reported the work was "just one switch
/// case and one help-text line" and the parent did the building itself over twenty turns — obeying
/// the spawn tool's "a task you could finish yourself costs a full briefing", which described that
/// situation exactly. No refusal, the tool still offered.</para>
///
/// <para>THREE PLACES because both prior interventions in this area needed that: the under-delegation
/// fix took a tool description AND a prompt rule AND worked examples, and ask_user went unused while
/// restraint sat in two of them. A carve-out in one location would lose to the restraint in the
/// others.</para>
/// </summary>
public class UserNamedTypeTests
{
    // --- 1. The tool description's restraint paragraph carries the exception ------------

    [Fact]
    public void TheSpawnDescriptionSaysAUserRequestSettlesIt()
    {
        var description = SpawnDescription();

        Assert.Contains("UNLESS THE USER ASKED FOR ONE", description);
        Assert.Contains("however small the task looks", description);
    }

    [Fact]
    public void TheRestraintItQualifiesIsStillThere()
    {
        // THE CARVE-OUT MUST NOT EAT THE RULE. This paragraph was tuned across three live drives and
        // stops the opposite failure — spawning an agent to read one known file. The exception is an
        // exception, not a replacement.
        var description = SpawnDescription();

        Assert.Contains("For a single-fact lookup where you already know the file", description);
        Assert.Contains("a full briefing", description);
    }

    [Fact]
    public void TheTypeLineSaysAUserNamedTypeSettlesIt()
    {
        // "name one when it fits what you need done" was the exact judgement that overrode the user:
        // the model assessed fit, decided the task was too small, and omitted the type.
        Assert.Contains("which settles it", SpawnDescription());
    }

    // --- 2. The system prompt states it where judgement forms ---------------------------

    [Fact]
    public void ThePromptSaysANamedTypeIsADecisionNotASuggestion()
    {
        var prompt = PromptWithSpawn();

        Assert.Contains("that is a decision", prompt);
        Assert.Contains("not a suggestion", prompt);
    }

    [Fact]
    public void ThePromptStillRequiresSayingSoRatherThanSilentlyComplying()
    {
        // THE HALF THAT MAKES IT SAFE. The model may still judge a spawn wasteful — what it may not
        // do is act on that silently, leaving the user believing an agent ran when none did.
        Assert.Contains("do not", PromptWithSpawn(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("silently do the work instead", PromptWithSpawn());
    }

    [Fact]
    public void ThePromptDoesNotSayItWhenTheAgentCannotSpawn()
    {
        // A rule about naming a type is noise for an agent with no spawn tool — and this is the
        // gate Task 9 added, so a regression there shows up here too.
        var prompt = SystemPrompt.Build(new SystemPromptContext(
            WorkingDirectory: "/tmp/x", IsGitRepo: false, Platform: "linux",
            Today: new DateOnly(2026, 8, 19), ModelId: "m")
        {
            CanSpawn = false,
        });

        Assert.DoesNotContain("that is a decision", prompt);
    }

    // --- Helpers -------------------------------------------------------------------------

    private static string PromptWithSpawn() => SystemPrompt.Build(new SystemPromptContext(
        WorkingDirectory: "/tmp/x", IsGitRepo: false, Platform: "linux",
        Today: new DateOnly(2026, 8, 19), ModelId: "m")
    {
        CanSpawn = true,
    });

    /// <summary>The description the model actually reads, catalog and all.</summary>
    private static string SpawnDescription() =>
        new SubAgentSpawner(
            new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
            {
                Provider = new MockLlmProvider(),
                Plugins = CxAgent.Core.Plugins.PluginRegistry.CreateWithBuiltins(),
                Ledger = new TokenLedger(),
                MaxTurns = 5,
            }),
            new AgentTypeCatalog(new Dictionary<string, AgentTypeConfig>(), null))
            .Definition.Description;
}
