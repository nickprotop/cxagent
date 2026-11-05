using CxAgent.Core.Agents;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;

namespace CxAgent.Core.Hooks;

/// <summary>
/// Runs an <c>agent</c> hook's verifier: a short-lived agent that may read the project before it answers.
///
/// <para>READ-ONLY, AND ASKS NOBODY. It is offered <c>read_file</c>, <c>glob</c> and <c>grep</c> and nothing
/// else, so it can look at test output or a file the event names but cannot change anything — which is why it
/// needs no permission gate, and why it never interrupts the user with a prompt while it decides.</para>
///
/// <para>NO HOOKS OF ITS OWN. It is not wired to the session's hooks: a PreToolUse agent hook that fired on its
/// own verifier's reads would never finish.</para>
/// </summary>
public static class HookAgentRunner
{
    /// <summary>The verifier's turn cap, the contract's.</summary>
    public const int MaxTurns = 50;

    private static readonly ToolSelection ReadOnly = new([Tool.ReadFile, Tool.Glob, Tool.Grep]);

    /// <summary>Runs the verifier and returns its final text, where its <c>{ok, reason}</c> answer is.</summary>
    public static async Task<string> RunAsync(HookAgentRequest request, CancellationToken ct)
    {
        var agent = new Agent(new AgentSetup
        {
            Model = new AgentModel(request.Model, new TokenLedger()),
            Outputs = new AgentOutputs(new BufferedChatSink(), new BufferedJobPanel()),
            Identity = new AgentIdentity { WorkingDir = request.ProjectDir, IsSubAgent = true, Label = "hook verifier" },
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()) { Selection = ReadOnly },
            Guards = new AgentGuards(),
            Brief = new AgentBrief(),
            Limits = new AgentLimits(MaxTurns),
            Conversation = null,
        });
        var result = await agent.SendAsync(request.Prompt, ct);
        return result.Text;
    }
}
