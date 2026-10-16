using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What the model reads about sub-agents and background work, checked against what the app now does.
///
/// <para>A STALE SENTENCE FAILS SILENTLY: a description naming a tool that no longer exists, or saying
/// a spawn waits where it returns at once, compiles and passes every behaviour test — and a model acting
/// on it polls, waits on nothing, or calls a tool it was never offered.</para>
/// </summary>
public class ModelFacingTextTests
{
    private static readonly string[] Removed = ["job_list", "job_kill", "agent_list"];

    private static string Prompt(bool canSpawn, bool canBackground, bool isSubAgent = false) =>
        SystemPrompt.Build(new SystemPromptContext(
            WorkingDirectory: "/tmp/x", IsGitRepo: false, Platform: "linux",
            Today: new DateOnly(2026, 10, 16), ModelId: "m")
        {
            CanSpawn = canSpawn,
            CanBackground = canBackground,
            IsSubAgent = isSubAgent,
        });

    private static SubAgentSpawner Spawner(bool background) =>
        new(new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Executors = JobRegistry.CreateWithBuiltins(), Ledger = new TokenLedger(),
            MaxTurns = 5, CompressAbove = 40_000, ContextWindow = 200_000,
            Delivery = background ? new RecordingDelivery() : null,
        }), store: new SubAgentStore());

    /// <summary>EVERY TEXT THE MODEL READS — prompts, tool definitions, receipts — names only tools that exist.</summary>
    [Fact]
    public void NoModelFacingText_NamesARemovedTool()
    {
        var texts = new List<string>
        {
            Prompt(canSpawn: true, canBackground: true),
            Prompt(canSpawn: true, canBackground: false),
            Prompt(canSpawn: false, canBackground: false),
            Prompt(canSpawn: false, canBackground: false, isSubAgent: true),
            SubAgentEnvelope.Receipt("id", "x", canWrite: true),
        };
        foreach (var spawner in new[] { Spawner(true), Spawner(false) })
            texts.Add(spawner.Definition.Description + spawner.Definition.InputSchema.GetRawText());
        foreach (var d in AgentReachTools.Definitions.Concat(TaskTools.Definitions))
            texts.Add(d.Description + d.InputSchema.GetRawText());

        foreach (var text in texts)
            foreach (var name in Removed)
                Assert.DoesNotContain(name, text);
    }

    /// <summary>WHERE A SPAWN RUNS IN THE BACKGROUND, the parent is told its report arrives as a message.</summary>
    [Fact]
    public void TheParentPrompt_WhereBackgroundRuns_SaysReportsArriveAsMessages() =>
        Assert.Contains("arrives as a message", Prompt(canSpawn: true, canBackground: true));

    /// <summary>WHERE IT CANNOT — headless, no delivery port — nothing promises a message that will not come.</summary>
    [Fact]
    public void TheParentPrompt_WhereBackgroundCannotRun_DoesNotSaySo()
    {
        var prompt = Prompt(canSpawn: true, canBackground: false);
        Assert.DoesNotContain("arrives as a message", prompt);
        Assert.DoesNotContain("background: false", prompt);
    }

    /// <summary>
    /// THE DEPENDENT EXAMPLE WAITS FOR ITS ANSWER. "Find them, then decide and edit" is a search whose
    /// answer the next step needs — backgrounded, the parent would decide before the search came back.
    /// </summary>
    [Fact]
    public void TheDependentExample_WaitsForItsAnswer_WhereBackgroundRuns()
    {
        var prompt = Prompt(canSpawn: true, canBackground: true);
        var example = prompt[prompt.IndexOf("Find every place that hand-rolls this", StringComparison.Ordinal)..];
        example = example[..example.IndexOf("</example>", StringComparison.Ordinal)];
        Assert.Contains("background: false", example);
    }

    /// <summary>A CHILD'S PROMPT TEACHES NO DELEGATION: it has no spawn tool and nothing to send to.</summary>
    [Fact]
    public void AChildPrompt_NamesNoSubAgentTool()
    {
        var prompt = Prompt(canSpawn: false, canBackground: false, isSubAgent: true);
        Assert.DoesNotContain("agent_send", prompt);
        Assert.DoesNotContain("background: false", prompt);
        Assert.DoesNotContain("arrives as a message", prompt);
    }

    /// <summary>THE PROMPT FLAG FOLLOWS THE SPAWNER: an agent is told about background reports only when its spawner can make them.</summary>
    [Fact]
    public void ASpawnerSaysWhetherItCanBackground()
    {
        Assert.True(((ISubAgentSpawner)Spawner(true)).CanBackground);
        Assert.False(((ISubAgentSpawner)Spawner(false)).CanBackground);
    }
}
