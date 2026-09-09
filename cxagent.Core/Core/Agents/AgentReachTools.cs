using System.Text;
using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Agents;

/// <summary>
/// Reaching a sub-agent this session already spawned.
///
/// <para>TWO TOOLS, NOT FOUR. There is no <c>agent_stop</c>: a stored agent runs only while it is
/// being asked something, and that turn is already cancellable by the token of the turn that asked,
/// so a stop tool would name a state that does not exist. And no <c>agent_forget</c>: a context costs
/// nothing per turn, so a tool that only deletes is a tool whose only use is a mistake.</para>
///
/// <para>OFFERED TO THE SESSION'S AGENT, NEVER TO A CHILD. The spawn prompt states "It cannot spawn
/// sub-agents of its own", and these inherit that limit rather than quietly widening it — a child
/// waking a sibling is recursion by another name, and none of the reasoning that forbids spawning
/// would have been consulted.</para>
/// </summary>
public sealed class AgentReachTools(SubAgentStore store)
{
    /// <summary>Whether this handles a call by that name.</summary>
    public bool Claims(string name) => name is "agent_send" or "agent_list";

    /// <summary>Runs one call and answers what the parent's model should read.</summary>
    public async Task<string> InvokeAsync(string toolName, string agentName, string prompt,
        CancellationToken ct) =>
        toolName switch
        {
            "agent_list" => List(),
            "agent_send" => await Send(agentName, prompt, ct),
            _ => $"error: '{toolName}' is not a tool this handles.",
        };

    /// <summary>
    /// What this session has spawned.
    ///
    /// <para>SAYS SO WHEN THERE IS NOTHING, rather than answering with an empty string: a blank reply
    /// reads as a tool that failed, and the model's next move is to call it again.</para>
    /// </summary>
    private string List()
    {
        var all = store.All();
        if (all.Count == 0)
            return "no sub-agents have been spawned in this session yet.";

        var sb = new StringBuilder("sub-agents you can reach with agent_send:\n");
        foreach (var a in all)
        {
            sb.Append("- ").Append(a.Name).Append(" (").Append(a.TypeName).Append(')');
            if (a.Description is { Length: > 0 } d) sb.Append(" — ").Append(d);
            if (store.IsBusy(a.Name)) sb.Append(" [busy]");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> Send(string agentName, string prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agentName))
            return "error: 'name' is required — which sub-agent should answer? Call agent_list to "
                 + "see the names.";

        // NAMES THE REMEDY, NOT ONLY THE FAULT. A model that misremembers a handle needs to be told
        // where the real ones are, or its next move is another guess.
        if (store.Find(agentName) is not { } stored)
            return $"error: no sub-agent named '{agentName}' in this session. "
                 + "Call agent_list to see which ones there are.";

        // REFUSED RATHER THAN QUEUED — see SubAgentStore.TryBeginSend. A blocked tool call cannot say
        // why it is waiting; a refusal can, and the model can act on it now.
        if (!store.TryBeginSend(agentName))
            return $"error: '{agentName}' is busy with another request. Try again once it answers.";

        try
        {
            // THE WAKING TURN'S TOKEN, NOT THE SPAWNING ONE'S. The token that created this child died
            // with the turn that called `agent`; a wake is governed by the turn that ASKED, so Escape
            // cancels it like any other tool call.
            var result = await stored.Agent.Agent.SendAsync(prompt, ct);
            return result.Text;
        }
        finally
        {
            // RELEASED WHATEVER HAPPENED. A throw that left the claim set would make this agent
            // permanently unreachable, with the refusal above as the only symptom.
            store.EndSend(agentName);
        }
    }

    /// <summary>
    /// The two definitions, hand-built as the spawn tool's is.
    ///
    /// <para>THE DESCRIPTIONS CARRY WHAT THE MODEL GETS WRONG, which is the only thing worth paying
    /// schema bytes for. For <c>agent_send</c> that is restating the original brief — the agent
    /// already has it, and repeating it wastes the very turn the tool exists to save. For
    /// <c>agent_list</c> it is that NOTHING ELSE reports what was spawned, so a model that does not
    /// call it does not know.</para>
    /// </summary>
    public static IReadOnlyList<ToolDefinition> Definitions =>
    [
        new ToolDefinition("agent_send",
            "Ask a sub-agent you already spawned for more. It REMEMBERS its own work — everything it "
            + "read, ran and concluded — so ask for what is still needed rather than restating what "
            + "it was originally told. Far cheaper than spawning a second agent over the same ground.",
            JsonDocument.Parse(
                """
                {
                  "type": "object",
                  "properties": {
                    "name": {
                      "type": "string",
                      "description": "The agent's handle, from the name= on its spawn result or from agent_list."
                    },
                    "prompt": {
                      "type": "string",
                      "description": "What you need from it now. It still has its own context, so do not repeat the original task."
                    }
                  },
                  "required": ["name", "prompt"]
                }
                """).RootElement),

        new ToolDefinition("agent_list",
            "The sub-agents spawned in this session, by name. Nothing else tells you what you "
            + "spawned once the call falls out of context.",
            JsonDocument.Parse(
                """
                {
                  "type": "object",
                  "properties": {}
                }
                """).RootElement),
    ];
}
