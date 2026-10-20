using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;

namespace CxAgent.Tests;

/// <summary>
/// The setup most tests build an agent from, adjusted with <c>with { … }</c> where a test needs more.
///
/// <para>THE OUTPUTS ARE A PARAMETER, not a default: tests read their own sink and job panel back, and several
/// files declare a sink of their own under the shared one's name — a default here would hand those tests a
/// different type with no compile error. What every test means the same thing by is defaulted: the built-in
/// tools, a ledger of its own, no logs, and every optional group empty.</para>
///
/// <para>BUILT PER CALL, so no two agents share a registry or a ledger — an instance made once and handed out
/// would leak one test's spend and tools into the next.</para>
/// </summary>
internal static class TestAgents
{
    public static AgentSetup Setup(ILlmProvider provider, AgentOutputs outputs, int maxTurns) => new()
    {
        Model = new AgentModel(provider, new TokenLedger()),
        Outputs = outputs,
        Identity = new AgentIdentity(),
        Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        Guards = new AgentGuards(),
        Brief = new AgentBrief(),
        Limits = new AgentLimits(maxTurns),
        Conversation = null,
    };
}
