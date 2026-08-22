using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Jobs;
using Xunit;
namespace CxAgent.Tests;
/// <summary>
/// The wiring between an agent and the ledger's per-model tally.
///
/// <para>Covered separately from TokenLedger's own tests because those prove the LEDGER tallies; this
/// proves the AGENT tells it which model spent. Removing `_provider.ModelId` from the Record call
/// leaves every ledger test passing and the breakdown permanently empty.</para>
/// </summary>
public class AgentSpendAttributionTests
{
    [Fact]
    public async Task AnAgentsSpendIsAttributedToItsModel()
    {
        var ledger = new TokenLedger();
        var provider = new MockLlmProvider("the-model");
        provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" }
            with { Usage = new LlmUsage { InputTokens = 100, OutputTokens = 20 } });

        var agent = new Agent(provider, JobRegistry.CreateWithBuiltins(), ledger,
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 50);
        await agent.SendAsync("go", CancellationToken.None);

        Assert.Equal(120, ledger.ByModel["the-model"]);
    }
}
