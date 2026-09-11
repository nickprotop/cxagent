using System.Text;
using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Jobs;
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
public sealed class AgentReachTools(SubAgentStore store, SemaphoreSlim? slot = null)
{
    /// <summary>The store behind these tools, for a caller that must reserve a handle at dispatch.</summary>
    public SubAgentStore Store => store;

    /// <summary>Whether this handles a call by that name.</summary>
    public bool Claims(string name) => name == Tool.AgentSend || name == Tool.AgentList;

    /// <summary>Runs one call and answers what the parent's model should read.</summary>
    public async Task<string> InvokeAsync(string toolName, string agentName, string prompt,
        CancellationToken ct) =>
        toolName switch
        {
            Tool.AgentList => List(),
            Tool.AgentSend => await Send(agentName, prompt, ct),
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

        // A RUNNING CHILD IS SENT TO, NOT REFUSED — and the state decides the shape of the answer, so
        // the model never has to be told which mode it is in. It observes: an idle child answers, a
        // busy one confirms delivery, and the two returns do not look alike.
        //
        // THE 54 SECONDS ARE THE ARGUMENT. A live drive measured that for one file read, and for all
        // of it the parent could only wait — including when it had just learned something the child
        // needed. Spawning a replacement to carry one correction throws away everything the first
        // one already knows, which is the opposite of what keeping its context is for.
        //
        // THE CLAIM IS ALSO HOW BUSY-NESS IS DISCOVERED, and it is the only atomic way to ask: a
        // separate IsBusy read would answer about a moment that has passed.
        //
        // SILENT, AND HELD ACROSS THE CAP. TryBeginSend would announce a start here, and its release
        // is the application's one announcement that a stretch has STOPPED — a listener archives a
        // run on it. So a send that claimed loudly and handed the claim back to wait for a permit
        // would fabricate a finished run of zero turns and zero cost, and that phantom is counted by
        // /stats exactly as a real one is. Claiming silently keeps the exclusion for the whole wait
        // and announces nothing until work actually begins.
        if (!store.TryClaim(agentName))
            return Deliver(stored, agentName, prompt);

        var announced = false;
        try
        {
            // WAITED WITHOUT SAYING THE CHILD IS WORKING, because it is not: SendBegan starts the
            // row's timer and rebases its clock, so announcing before the permit is in hand paints a
            // ticking row for a child sitting in a queue and folds the queue wait into the duration
            // the archive records as the run's cost.
            //
            // A MESSAGE FOR A BUSY CHILD NEVER REACHES HERE, and must not: the mailbox is delivery,
            // not work, so it costs no concurrency and queueing it behind a full cap would make
            // telling a running child something wait on unrelated children finishing. The refusals
            // above are answered without waiting for the same reason — a wrong handle must not burn
            // a permit to be told it is wrong.
            //
            // THE ASKING TURN'S TOKEN, so Escape leaves the queue rather than stranding the caller.
            if (slot is not null)
                await slot.WaitAsync(ct);

            try
            {
                // ANNOUNCED ONLY NOW, WITH THE PERMIT IN HAND. From here the claim is released
                // through EndSend rather than Release, which is what keeps one begin paired with
                // exactly one end — the pairing a listener turns into one archive row.
                // SET BEFORE THE CALL, not after: a listener that throws from SendBegan has already
                // started the row, and an end must still be announced to settle it.
                announced = true;
                store.AnnounceBegin(agentName);
                // ANYTHING WAITING GOES FIRST. A message queued while this child was running, on a
                // lap it never took, would otherwise arrive after a prompt that was sent later — and
                // two corrections read in the wrong order are worse than one arriving late.
                //
                // THIS IS ALSO WHY NOTHING IS EVER STRANDED. A mailbox can only be filled while a
                // loop is running to drain it; a child that finished without draining is IDLE, and
                // idle is this path, which empties it before appending. The state that would lose a
                // message is the state that delivers it.
                foreach (var waiting in stored.Agent.Agent.Mailbox.Drain())
                    stored.Agent.Agent.Context.Messages.Add(
                        new ChatMessage { Role = "user", Content = waiting });

                // THE WAKING TURN'S TOKEN, NOT THE SPAWNING ONE'S. The token that created this child
                // died with the turn that called `agent`; a wake is governed by the turn that ASKED,
                // so Escape cancels it like any other tool call.
                var result = await stored.Agent.Agent.SendAsync(prompt, ct);
                return result.Text;
            }
            finally
            {
                // THE CAP, AND IT BINDS A RESUME EXACTLY AS IT BINDS A SPAWN. A woken child is a
                // child running; a cap that counted only spawns would be a cap the model can step
                // around by reaching for the cheaper tool, which is the one it is told to prefer.
                slot?.Release();
            }
        }
        finally
        {
            // RELEASED WHATEVER HAPPENED, AND THROUGH WHICHEVER CALL MATCHES THE ANNOUNCEMENT. A
            // throw that left the claim set would make this agent permanently unreachable, with a
            // delivery confirmation as the only symptom; and a cancellation while queued must not
            // announce a stop it never announced a start for, or the archive gains a run of zero
            // turns that /stats averages in.
            if (announced) store.EndSend(agentName);
            else store.Release(agentName);
        }
    }

    /// <summary>
    /// Leaves a message for a child that is mid-task, and says so.
    ///
    /// <para>NOT WORK, SO IT COSTS NO CONCURRENCY. A delivery appends to a mailbox a running loop
    /// will drain on its own next turn; nothing new starts, so it waits no cap — which is what lets
    /// a parent correct a running child while every permit is held by other children.</para>
    /// </summary>
    private static string Deliver(StoredAgent stored, string agentName, string prompt)
    {
        if (!stored.Agent.Agent.Mailbox.TryEnqueue(prompt, out var full))
            return $"error: could not reach '{agentName}' — {full}";

        // NAMES WHERE THE ANSWER IS NOT. Without that clause the obvious next move is to call
        // this again expecting a reply, which is the loop this exists to prevent.
        return $"delivered to '{agentName}' — it is mid-task and will see this on its next turn. "
             + "It will not answer here; ask agent_send again later, or read its final report.";
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
        new ToolDefinition(Tool.AgentSend,
            "Ask a sub-agent you already spawned for more. It REMEMBERS its own work — everything it "
            + "read, ran and concluded — so ask for what is still needed rather than restating what "
            + "it was originally told. Far cheaper than spawning a second agent over the same ground. "
            + "Works while it is still running: a message sent to a busy agent reaches it on its next "
            + "turn, so tell it as soon as you know rather than waiting for it to finish and "
            + "re-spawning. That call confirms delivery instead of answering.",
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

        new ToolDefinition(Tool.AgentList,
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
