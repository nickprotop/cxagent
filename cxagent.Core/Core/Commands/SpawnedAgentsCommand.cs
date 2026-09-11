using CxAgent.Core.Agents;

namespace CxAgent.Core.Commands;

/// <summary>
/// What this session has spawned, and a way to reach one without asking the model to.
///
/// <para>SEPARATE FROM <see cref="AgentsCommand"/>, which lists agent TYPES from config — the kinds
/// of agent that can be made. This lists the ones that WERE made, in this session, with their own
/// contexts. Two different subjects that happen to share a word.</para>
///
/// <para>WHY A USER NEEDS THIS AT ALL: the model decides whether to reach an existing agent or spawn
/// a fresh one, and it gets that wrong — driven live, "re use the agent and make it use lsp tools"
/// produced a second spawn that knew nothing of the first one's work. A user who can see the agents
/// and send to one directly does not depend on the model reading the situation correctly, which is
/// the same reason /triggers-cancel exists beside trigger_cancel.</para>
/// </summary>
public sealed class SpawnedAgentsCommand(SubAgentStore store)
{
    /// <summary>The listing, or a line saying there is nothing to list.</summary>
    public string Render()
    {
        var all = store.All();
        if (all.Count == 0)
            return "no sub-agents have been spawned in this session yet.";

        return string.Join("\n", all.Select(a =>
            $"#{a.Name}{(store.IsBusy(a.Name) ? "  ·  busy" : "")}"
            + $"  ·  {a.TypeName}"
            + (a.Description is { Length: > 0 } d ? $"  ·  {d}" : "")));
    }

    /// <summary>What a send could not do, or null when it is about to be attempted.</summary>
    /// <remarks>
    /// REFUSALS ARE SEPARATED FROM THE SEND ITSELF because the caller runs the send asynchronously
    /// and reports its answer later — a refusal has to come back now, in the same breath as the
    /// command, or the user is left watching nothing happen.
    /// </remarks>
    public string? RefuseSend(string arguments, out string name, out string prompt)
    {
        name = "";
        prompt = "";

        // THE VERB IS STILL IN THERE. RegisterVerb matches on the first word and hands the handler
        // everything typed, verb included — the same shape NewSessionCommand.FolderFrom strips for
        // `/sessions new`. Reading it as the agent's name asks for a sub-agent called "send".
        var text = (arguments.Split(' ', 2) is [_, var rest] ? rest : "").Trim();
        if (text.Length == 0)
            return "say which agent and what to ask: `/agents send <name> <prompt>`.";

        var split = text.IndexOf(' ');
        if (split < 0)
            return $"'{text}' names an agent but asks it nothing. Add the prompt after it.";

        name = text[..split];
        prompt = text[(split + 1)..].Trim();

        if (prompt.Length == 0)
            return $"'{name}' names an agent but asks it nothing. Add the prompt after it.";

        if (store.Find(name) is null)
            return $"no sub-agent named '{name}' in this session. `/agents list` to see them.";

        // REFUSED RATHER THAN QUEUED, matching agent_send: one turn at a time on an agent's context,
        // and a user told "busy" can wait, where a user told nothing cannot.
        if (store.IsBusy(name))
            return $"'{name}' is busy with another request. Try again once it answers.";

        return null;
    }
}
