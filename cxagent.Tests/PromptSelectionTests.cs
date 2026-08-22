using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The prompt is a SURFACE like any other. Withholding a tool and still spending four paragraphs
/// teaching its discipline costs tokens on every turn and describes a capability the model does not
/// have — the same class of defect as the todo header naming a withheld todowrite.
///
/// <para>THE PROMPT IS BUILT BEFORE THE TOOL LIST (Agent.cs `:790` against `:906`), so the gates
/// cannot read _offeredNames. SelectionAllows is the single expression both consult; these tests
/// pin that the prompt actually follows it, because a second implementation that drifts is the
/// failure mode this feature keeps finding.</para>
///
/// <para>CACHE: S1 and S2 are fixed for the agent's life, so the gated text is identical every turn
/// and the cached prefix is never invalidated. Only an S3 selection that VARIES between requests
/// rewrites it — measured at 67,367 tokens for a 134-character change — which the documentation
/// tells callers to avoid rather than the code preventing.</para>
/// </summary>
public class PromptSelectionTests
{
    /// <summary>A spawner that exists to make CanSpawn true. Never invoked — these tests read the
    /// PROMPT, and the offer path only needs its Definition.</summary>
    private sealed class StubSpawner : ISubAgentSpawner
    {
        public string ToolName => Tool.Agent;

        public ToolDefinition Definition => new(
            Tool.Agent, "spawns", JsonSerializer.SerializeToElement(new { type = "object" }));

        public void SwapDefaultProvider(ILlmProvider provider, int? contextWindow, string? instanceName) { }

        public Task<string?> TryInvokeAsync(ToolCall call, Action<SubAgent>? onChild,
            CancellationToken ct, string? label = null, ToolSelection? turnTools = null)
            => throw new NotSupportedException();
    }

    private static async Task<string> PromptUnder(ToolSelection? selection)
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "done",
            StopReason = "end_turn",
            Usage = new LlmUsage { InputTokens = 1, OutputTokens = 1 },
        });

        var agent = new Agent(provider, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            spawner: new StubSpawner(),
            askUser: (_, _) => Task.FromResult(new QuestionAnswers()),
            toolSelection: selection);

        await agent.SendAsync("go", CancellationToken.None);

        return (provider.LastMessages ?? [])
            .Where(m => m.Role == "system")
            .Select(m => m.Content ?? "")
            .FirstOrDefault() ?? "";
    }

    // --- The spawn blocks ---------------------------------------------------------------

    [Fact]
    public async Task TheDelegationCoachingIsPresentWhenTheAgentToolIsOffered()
    {
        var prompt = await PromptUnder(null);

        Assert.Contains("sub-agent's report is a claim", prompt);
        Assert.Contains("cannot see a sub-agent's work", prompt);
    }

    [Fact]
    public async Task TheDelegationCoachingIsGoneWhenTheAgentToolIsWithheld()
    {
        // CanSpawn ALONE IS NOT ENOUGH. It answers "is this agent structurally able to spawn", which
        // stays true for a parent whose `agent` tool the selection removed — so before the && the
        // prompt taught a delegation discipline to an agent with nothing to delegate with.
        var prompt = await PromptUnder(new ToolSelection([Tool.Inherited, Tool.Not.Agent]));

        Assert.DoesNotContain("sub-agent's report is a claim", prompt);
        Assert.DoesNotContain("cannot see a sub-agent's work", prompt);
    }

    // --- The ask_user block -------------------------------------------------------------

    [Fact]
    public async Task TheAskUserCoachingIsPresentWhenTheToolIsOffered()
        => Assert.Contains("call ask_user and wait", await PromptUnder(null));

    [Fact]
    public async Task TheAskUserCoachingIsGoneWhenTheToolIsWithheld()
    {
        // The worst of the four to leave in: it names the tool and orders the model to call it, so a
        // model that obeys spends a turn learning it is not available.
        var prompt = await PromptUnder(new ToolSelection([Tool.Inherited, Tool.Not.AskUser]));

        Assert.DoesNotContain("call ask_user and wait", prompt);
    }

    // --- Narrowing something else leaves both alone -------------------------------------

    [Fact]
    public async Task NarrowingAnUnrelatedToolTouchesNeitherBlock()
    {
        // No collateral damage: the gates read their OWN tool, not "was anything narrowed".
        var prompt = await PromptUnder(new ToolSelection([Tool.Inherited, Tool.Not.RunShell]));

        Assert.Contains("sub-agent's report is a claim", prompt);
        Assert.Contains("call ask_user and wait", prompt);
    }

    // --- The turn level ------------------------------------------------------------------

    [Fact]
    public async Task ATurnSelectionAlsoGatesThePrompt()
    {
        // S3 COMPOSES INTO THE SAME PREDICATE. This is the case that varies the prefix if a caller
        // changes it per request — correct either way, but the reason the docs say to set it once.
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "done",
            StopReason = "end_turn",
            Usage = new LlmUsage { InputTokens = 1, OutputTokens = 1 },
        });

        var agent = new Agent(provider, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            spawner: new StubSpawner(),
            askUser: (_, _) => Task.FromResult(new QuestionAnswers()));

        await agent.SendAsync("go", CancellationToken.None,
            turnTools: new ToolSelection([Tool.Inherited, Tool.Not.Agent]));

        var prompt = (provider.LastMessages ?? [])
            .Where(m => m.Role == "system").Select(m => m.Content ?? "").FirstOrDefault() ?? "";

        Assert.DoesNotContain("sub-agent's report is a claim", prompt);
        Assert.Contains("call ask_user and wait", prompt);
    }
}
