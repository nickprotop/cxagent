using System.Text;
using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;

namespace CxAgent.Core.Agents;

/// <summary>What an agent_send asks, of whom, and how its answer comes back.</summary>
/// <param name="To">A handle, or a child's agent id.</param>
/// <param name="Prompt">What it is asked.</param>
/// <param name="Background">Resume in the background and report later, rather than wait.</param>
/// <param name="ParentAgentId">Who a background resume reports to; null when nobody can be told.</param>
/// <param name="FromUser">Typed by the user rather than called by the model — the one sender a user's
/// stop does not refuse.</param>
public sealed record SendRequest(string To, string Prompt, bool Background, string? ParentAgentId,
    bool FromUser);

/// <summary>
/// Reaching a sub-agent this session already spawned.
///
/// <para>SENDING, NOT LISTING OR STOPPING. The task tools list sub-agents and stop background ones, with
/// background shells — a parent asks "what is still running" about both kinds at once. A child resumed
/// in the foreground stops with the turn that asked it, by that turn's token. No <c>agent_forget</c>
/// either: a context costs nothing per turn, so a tool that only deletes is a tool whose only use is a
/// mistake.</para>
///
/// <para>OFFERED TO THE SESSION'S AGENT, NEVER TO A CHILD. The spawn prompt states "It cannot spawn
/// sub-agents of its own", and these inherit that limit rather than quietly widening it — a child
/// waking a sibling is recursion by another name, and none of the reasoning that forbids spawning
/// would have been consulted.</para>
/// </summary>
public sealed class AgentReachTools(SubAgentStore store, SemaphoreSlim? slot = null,
    IAgentDelivery? delivery = null)
{
    /// <summary>The store behind these tools.</summary>
    public SubAgentStore Store => store;

    /// <summary>Whether this handles a call by that name.</summary>
    public bool Claims(string name) => name == Tool.AgentSend;

    /// <summary>
    /// Sends to a kept child: into its mailbox while it runs, otherwise resuming it — in the background
    /// and reported later, or waited for.
    /// </summary>
    public async Task<string> SendAsync(SendRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.To))
            return "error: 'to' is required — which sub-agent should answer? Call task_list to see the "
                 + "names.";

        // A HANDLE OR AN AGENT ID: the handle is what the receipt and task_list show, the id is what an
        // envelope carries, and a model holding either should not have to translate. NAMES THE REMEDY,
        // NOT ONLY THE FAULT: a model that misremembers a handle needs to be told where the real ones
        // are, or its next move is another guess.
        var stored = store.Find(request.To) ?? store.FindByAgentId(request.To);
        if (stored is null)
            return store.WasForgotten(request.To)
                ? request.FromUser ? SubAgentStore.Dismissed(request.To) : SubAgentStore.DismissedForModel(request.To)
                : $"error: no sub-agent named '{request.To}' in this session. "
                  + "Call task_list to see which ones there are.";
        var name = stored.Name;
        AfterLookupForTest?.Invoke();

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
        // SILENT, AND HELD ACROSS THE CAP. An announced start pairs with EndSend, the application's one
        // announcement that a stretch has STOPPED — a listener archives a run on it. So a send that
        // announced at the claim and handed the claim back to wait for a permit would fabricate a
        // finished run of zero turns and zero cost, and that phantom is counted by /stats exactly as a
        // real one is. Claiming silently keeps the exclusion for the whole wait and announces nothing
        // until work actually begins.
        // A LOST CLAIM MAY BE A FORGET'S, which holds it for all of its work — so the send waits for any forget to
        // finish before deciding: gone, it is answered "dismissed"; still kept (the forget backed off, or the claim
        // was a run's), the message is delivered as to any busy child.
        if (!store.TryClaim(name))
            return store.AfterAnyForget(() => store.WasForgotten(name) ? Dismissed(name, request) : Deliver(stored, name, request));

        // A FORGET THAT FINISHED BETWEEN THE LOOKUP AND THE CLAIM released its claim after writing the tombstone,
        // so this claim succeeded on an agent that is gone — caught here rather than resumed.
        if (store.WasForgotten(name))
        {
            store.Release(name);
            return Dismissed(name, request);
        }

        // THE USER'S STOP STANDS AGAINST THE MODEL: resuming a child the user stopped would quietly undo
        // their decision. The user's own send is the way back, and it hands the child back to the model.
        if (store.BackgroundOf(name) is { StoppedByUser: true } last)
        {
            if (!request.FromUser)
            {
                store.Release(name);
                return $"error: '{name}' was stopped by the user, so it will not be resumed. Say so in your "
                     + "reply rather than starting the same work elsewhere.";
            }
            last.ClearUserStop();
        }

        // IN THE BACKGROUND WHERE IT CAN BE: a port to report through and a parent to report to. The
        // claim taken above is the run's; the runner releases it when the stretch ends.
        if (request.Background && delivery is not null && request.ParentAgentId is { } parent)
        {
            // TAKEN BEFORE THE NEW RUN REPLACES THE ONE HOLDING IT — see UndeliveredReport.
            var earlier = Earlier(request, name);
            var run = store.BeginBackground(
                new BackgroundStart(Name: name, ParentAgentId: parent, Brief: request.Prompt,
                    Description: null, IsResume: true),
                new Jobs.ResultHandoff(BackgroundRunner.LeaveFor(delivery, parent, name)));
            _ = BackgroundRunner.Start(new BackgroundWork(store, stored, run, request.Prompt, slot, delivery,
                result => SubAgentEnvelope.Render(stored.Agent.Agent.Id, name, result.Outcome, result.Text)));
            return earlier + $"'{name}' is working on it in the background — its answer will come to you as a "
                 + $"message.\nIf your next step cannot go on without it: task_output {{\"id\": \"{name}\", "
                 + "\"wait\": true}";
        }

        // TAKEN ONLY ONCE THE ANSWER IS IN: a foreground send leaves the earlier run in place, and one that
        // throws — Escape on the asking turn — must not have taken a report it can no longer return.
        var answer = await Foreground(stored, request, ct);
        return Earlier(request, name) + answer;
    }

    /// <summary>
    /// The undelivered report this send hands back, or empty.
    ///
    /// <para>NOT FOR THE USER'S SEND: their answer is shown to them and never to the model, so taking the
    /// report there would lose it after all. Left on its run, it is still the model's to read.</para>
    /// </summary>
    private string Earlier(SendRequest request, string name) => request.FromUser ? "" : UndeliveredReport(name);

    /// <summary>
    /// The child's last background report, if it was never delivered, taken so nothing hands it over twice
    /// — with a line saying what it is; empty when there is none.
    ///
    /// <para>A REPORT THAT NEVER REACHED THE MODEL GOES BACK WITH A RESUME'S ANSWER. A background resume
    /// replaces the run that holds it, after which nothing can reach it; the model would have lost work it
    /// asked for with no sign that it existed.</para>
    /// </summary>
    private string UndeliveredReport(string name)
    {
        if (store.BackgroundOf(name) is not { Handoff.Undelivered: true } last) return "";
        return last.Handoff.Read() is { AlreadySent: false, Result: { } report }
            ? $"'{name}' had a report that never reached you; here it is:\n{report}\n\nAnd for this send:\n"
            : "";
    }

    /// <summary>
    /// Runs between the lookup and the claim — a test's way into the window where a forget can finish after this
    /// send found the agent and before it claimed it. The app never sets it.
    /// </summary>
    internal Action? AfterLookupForTest { get; set; }

    /// <summary>The refusal for a forgotten agent: the user's line for the user, the model's for the model.</summary>
    private static string Dismissed(string name, SendRequest request) =>
        request.FromUser ? SubAgentStore.Dismissed(name) : SubAgentStore.DismissedForModel(name);

    /// <summary>Resumes a claimed child and waits for its answer.</summary>
    private async Task<string> Foreground(StoredAgent stored, SendRequest request, CancellationToken ct)
    {
        var agentName = stored.Name;
        var prompt = request.Prompt;
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
                stored.Agent.Agent.EchoPrompt(prompt, request.FromUser);

                // THE WAKING TURN'S TOKEN, NOT THE SPAWNING ONE'S. The token that created this child
                // died with the turn that called `agent`; a wake is governed by the turn that ASKED,
                // so Escape cancels it like any other tool call.
                // NOTED, NOT ANNOUNCED: the release below names no outcome — its listeners settle a resume as they
                // always have — and the store's record of how this stretch ended takes the word from here.
                SendResult result;
                try
                {
                    result = await stored.Agent.Agent.SendAsync(prompt, ct);
                }
                catch (OperationCanceledException)
                {
                    store.NoteOutcome(agentName, SubAgentEnvelope.WordFor(SendOutcome.Cancelled));
                    throw;
                }
                catch (Exception)
                {
                    store.NoteOutcome(agentName, SubAgentEnvelope.WordFor(SendOutcome.Failed));
                    throw;
                }
                store.NoteOutcome(agentName, SubAgentEnvelope.WordFor(result.Outcome));
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
    private static string Deliver(StoredAgent stored, string agentName, SendRequest request)
    {
        if (!stored.Agent.Agent.Mailbox.TryEnqueue(request.Prompt, out var full))
            return $"error: could not reach '{agentName}' — {full}";

        // SHOWN WHEN DELIVERED, the moment the sender pressed Enter — the mailbox keeps only the text, so whose it
        // was is known here and nowhere later.
        stored.Agent.Agent.EchoPrompt(request.Prompt, request.FromUser);

        // NAMES WHERE THE ANSWER IS NOT. Without that clause the obvious next move is to call this again
        // expecting a reply, which is the loop this exists to prevent.
        //
        // TRUE WHICHEVER WAY THE CHILD IS BUSY: a background run reports as a message, a foreground one
        // answers the agent_send or spawn that is waiting on it — so the reply names the work already
        // under way rather than one of the two, and task_list, which shows both as working, rather than
        // task_output, which knows only the background run.
        //
        // THE USER IS TOLD WHERE IT GOES, NOT TOLD IT COMES TO THEM: whatever the child does with this
        // reaches the model that started the work, and "its report will come to you" over a typed
        // command would promise the user an answer that is addressed to somebody else.
        return request.FromUser
            ? $"delivered to '{agentName}' — it is mid-task and will see this on its next turn. Its answer "
              + "goes to the agent that set it working, not here; /tasks shows whether it is still going."
            : $"delivered to '{agentName}' — it is mid-task and will see this on its next turn. It will "
              + "not answer here: what it makes of this comes back with the work it is already doing. "
              + "task_list shows whether it is still working.";
    }

    /// <summary>The send tool as this agent is offered it — see <see cref="DefinitionsFor"/>.</summary>
    public IReadOnlyList<ToolDefinition> Definitions => DefinitionsFor(canBackground: delivery is not null);

    /// <summary>
    /// The send tool's definition, hand-built as the spawn tool's is.
    ///
    /// <para>THE DESCRIPTION CARRIES WHAT THE MODEL GETS WRONG, which is the only thing worth paying
    /// schema bytes for: restating the original brief — the agent already has it, and repeating it wastes
    /// the very turn the tool exists to save.</para>
    ///
    /// <para>HOW A RESUME WAITS IS CHOSEN BY WHAT THIS AGENT CAN DO, as the spawn tool's is: where a
    /// resume cannot run in the background — headless, no delivery port — there is no flag, and the text
    /// says the call waits. A flag the send ignores, or a message promised that never comes, would have
    /// the model end its turn before the answer it is about to be handed.</para>
    /// </summary>
    public static IReadOnlyList<ToolDefinition> DefinitionsFor(bool canBackground) =>
    [
        new ToolDefinition(Tool.AgentSend,
            "Ask a sub-agent you already spawned for more. It REMEMBERS its own work — everything it "
            + "read, ran and concluded — so ask for what is still needed rather than restating what "
            + "it was originally told. Far cheaper than spawning a second agent over the same ground. "
            + "Works while it is still running: a message sent to a busy agent reaches it on its next "
            + "turn, so tell it as soon as you know rather than waiting for it to finish and "
            + "re-spawning. That call confirms delivery instead of answering. "
            + (canBackground
                ? "A finished agent works on what you ask in the background by default and its answer "
                  + "comes to you as a message; pass background: false when your next step needs the answer now."
                : "A finished agent works on what you ask and the call waits for it: its answer is the result."),
            JsonDocument.Parse(SchemaTemplate.Replace("{{BACKGROUND}}", canBackground ? BackgroundProperty : "",
                StringComparison.Ordinal)).RootElement),
    ];

    /// <summary>The `background` argument, offered only where a resume can run in the background.</summary>
    private const string BackgroundProperty =
        """
        ,
                    "background": {
                      "type": "boolean",
                      "description": "Default true: it works while you carry on and its answer comes to you as a message. Pass false when your next step needs the answer now."
                    }
        """;

    /// <summary>The send tool's argument schema, with a slot for the background flag.</summary>
    private const string SchemaTemplate =
        """
        {
          "type": "object",
          "properties": {
            "to": {
              "type": "string",
              "description": "The agent's handle, from the name= on its spawn result or from task_list, or its agent id."
            },
            "prompt": {
              "type": "string",
              "description": "What you need from it now. It still has its own context, so do not repeat the original task."
            }{{BACKGROUND}}
          },
          "required": ["to", "prompt"]
        }
        """;
}
