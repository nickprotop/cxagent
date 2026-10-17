using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What the model reads about sub-agents and background work, checked against what the app does.
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
        foreach (var d in AgentReachTools.DefinitionsFor(true).Concat(AgentReachTools.DefinitionsFor(false)).Concat(TaskTools.Definitions))
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

    /// <summary>
    /// THE USER'S ASK FOR A BACKGROUND AGENT SETTLES THE FLAG, in every place the model reads about it: the
    /// prompt, the spawn tool's description, and the flag's own description. Measured live, "explore … with
    /// an background agent" drew an explicit background: false, and the user's next message waited behind
    /// the parent's blocked turn.
    /// </summary>
    [Fact]
    public void TheModelIsTold_AnAskedForBackgroundAgentNeverPassesFalse_WhereBackgroundRuns()
    {
        const string rule = "never pass background: false";
        Assert.Contains(rule, Prompt(canSpawn: true, canBackground: true));
        var spawner = Spawner(true).Definition;
        Assert.Contains(rule, spawner.Description);
        var flag = spawner.InputSchema.GetProperty("properties").GetProperty("background").GetProperty("description").GetString();
        Assert.Contains(rule, flag);

        Assert.DoesNotContain(rule, Prompt(canSpawn: true, canBackground: false));
        Assert.DoesNotContain(rule, Spawner(false).Definition.Description);
    }

    /// <summary>
    /// THE WAITING EXAMPLE WAITS BECAUSE NOTHING CAN START WITHOUT THE ANSWER, not because it is a search: an
    /// example that reads "finding means foreground" pulls an explore request into a blocked turn.
    /// </summary>
    [Fact]
    public void TheDependentExample_GivesTheDependencyAsTheReasonToWait()
    {
        var prompt = Prompt(canSpawn: true, canBackground: true);
        var example = prompt[prompt.IndexOf("Find every place that hand-rolls this", StringComparison.Ordinal)..];
        example = example[..example.IndexOf("</example>", StringComparison.Ordinal)];
        Assert.Contains("nothing here can start until", example);
        Assert.DoesNotContain("the finding is a search", example);
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

    /// <summary>
    /// agent_send, WHERE A RESUME CANNOT RUN IN THE BACKGROUND, offers no flag and promises no message:
    /// the call waits, and a model told otherwise ends its turn before the answer it is about to be handed.
    /// </summary>
    [Fact]
    public void AgentSend_WhereBackgroundCannotRun_OffersNoFlag_AndPromisesNoMessage()
    {
        var send = Assert.Single(new AgentReachTools(new SubAgentStore()).Definitions);
        Assert.DoesNotContain("\"background\"", send.InputSchema.GetRawText());
        Assert.DoesNotContain("comes to you as a message", send.Description);
        Assert.DoesNotContain("background: false", send.Description);
        Assert.Contains("waits", send.Description);

        var backgrounding = Assert.Single(new AgentReachTools(new SubAgentStore(), delivery: new RecordingDelivery()).Definitions);
        Assert.Contains("\"background\"", backgrounding.InputSchema.GetRawText());
        Assert.Contains("comes to you as a message", backgrounding.Description);
    }
}
