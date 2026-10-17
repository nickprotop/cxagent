using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Agents;

/// <summary>
/// Runs a sub-agent on behalf of a tool call, or declines a name it does not own.
///
/// <para>CONSULTED BEFORE <see cref="Jobs.ToolBindings"/>, exactly as the MCP branch already is,
/// and returning null for a name it does not own — the same contract
/// <c>McpToolset.TryInvokeAsync</c> holds, so the dispatch site gains one more <c>??</c> rather than a
/// new shape.</para>
///
/// <para>NOT A <see cref="Llm.BuiltinTool"/> ENUM MEMBER, which is the obvious alternative and is
/// wrong. <c>Agent.AllBuiltins</c> is <c>Enum.GetValues&lt;BuiltinTool&gt;()</c>, so an enum member is
/// offered to EVERY agent — including children, which would make "no sub-agents of sub-agents" a rule
/// the child is asked to obey rather than a capability it lacks. A child is constructed without a
/// spawner and therefore cannot nest, whatever it is told.</para>
/// </summary>
public interface ISubAgentSpawner
{
    /// <summary>The tool name this spawner answers to, so the dispatch site can advertise it.</summary>
    string ToolName { get; }

    /// <summary>
    /// Points future children at a different default model, after the session switched.
    ///
    /// <para>ON THE INTERFACE because the host holds a spawner, not a factory, and a swap that
    /// stopped at the host left every child on the model the session started with — while the switch
    /// notice promised the opposite.</para>
    /// </summary>
    void SwapDefaultProvider(ILlmProvider provider, int? contextWindow, string? instanceName);

    /// <summary>
    /// A saved config, in place: the limit on children running at once and the catalog a type
    /// resolves against. Children already running keep what they started with. A spawner with nothing
    /// to refresh ignores it.
    /// </summary>
    void Refresh(int? maxConcurrentAgents, AgentTypeCatalog types) { }

    /// <summary>The definition the model sees. Hand-built: a spawn tool has no executor and no
    /// <c>JobSchema</c>, so <c>ToolBindings.BuildDefinition</c>'s drift guard has nothing to guard
    /// against — <c>McpToolset.Definitions()</c> constructs its own the same way.</summary>
    ToolDefinition Definition { get; }

    /// <summary>
    /// Where this spawner keeps the children it made, or null when it keeps none.
    ///
    /// <para>ON THE INTERFACE RATHER THAN AS ANOTHER <c>Agent</c> CONSTRUCTOR PARAMETER. That list is
    /// already long, and the store is not an independent thing an agent is given — it belongs to the
    /// spawner, and an agent that cannot spawn has nothing to reach. Reading it from here keeps the
    /// two facts in one place instead of letting a caller pair a spawner with somebody else's store.</para>
    ///
    /// <para>A DEFAULT MEMBER, so an existing implementation — the test fakes among them — keeps
    /// compiling and simply keeps nothing.</para>
    /// </summary>
    SubAgentStore? Store => null;

    /// <summary>
    /// The cap on children running at once, or null when uncapped.
    ///
    /// <para>ON THE INTERFACE FOR THE REASON <see cref="Store"/> GIVES: an agent that cannot spawn
    /// has nothing to cap, and the slot belongs to the spawner rather than being an independent
    /// thing an agent is handed — reading it from here is what stops a caller pairing a spawner with
    /// somebody else's cap.</para>
    ///
    /// <para>EXPOSED AT ALL BECAUSE A RESUME IS ALSO A CHILD RUNNING. agent_send does not pass
    /// through the spawn method that waits this, so without a way to reach the same semaphore a
    /// configured cap silently bounds only half the work it was set to bound.</para>
    ///
    /// <para>A DEFAULT MEMBER, so existing implementations — the test fakes among them — keep
    /// compiling and simply cap nothing.</para>
    /// </summary>
    SemaphoreSlim? ConcurrencySlot => null;

    /// <summary>
    /// Whether a spawn may run in the background here — a store, a delivery port, and a runtime that
    /// allows it.
    ///
    /// <para>ON THE INTERFACE FOR THE PROMPT: the agent tells its model that background reports arrive as
    /// messages only when they can, and the spawner is the one that knows. A DEFAULT MEMBER, false, so an
    /// existing implementation keeps compiling and keeps its foreground meaning.</para>
    /// </summary>
    bool CanBackground => false;

    /// <summary>
    /// Runs the child and returns the envelope, or null if <paramref name="call"/> is not this
    /// spawner's tool.
    /// </summary>
    /// <param name="onChild">
    /// Called with the child the moment it is built, BEFORE it runs — the seam telemetry uses to
    /// attach to a child's events and to associate its id with the row already on screen.
    /// </param>
    /// <param name="parentAgentId">
    /// The spawning agent's id, so the child's logs nest under it rather than beside it. Optional
    /// because a spawner that keeps no logs does not need it.
    /// </param>
    /// <param name="turnTools">
    /// The selection the PARENT's current request is running under, so a child inherits it.
    ///
    /// <para>RIDES THE CALL rather than living on SubAgentRuntime, which is built once when the
    /// session is wired and therefore cannot carry a per-request value. A turn narrowed to
    /// read-only that then spawns a writing child has not narrowed anything.</para>
    /// </param>
    /// <param name="call">The spawn call the model issued.</param>
    /// <param name="ct">Cancels the child mid-run.</param>

    Task<string?> TryInvokeAsync(ToolCall call, Action<SubAgent>? onChild, CancellationToken ct,
        string? parentAgentId = null, Jobs.ToolSelection? turnTools = null);
}

/// <summary>
/// What the parent's model receives back from a spawn.
///
/// <para>AN ENVELOPE FROM STEP 1, NOT A BARE STRING (D13). An id retrofitted later would have to be
/// threaded through every surface that had already been built against a string; adding it now costs a
/// field. And <c>state</c> is the part that cannot be deferred at all: without it a capped run — a
/// salvage summary of unfinished work — is indistinguishable from a finished answer, and the parent
/// acts on it as though the work were done.</para>
/// </summary>
public static class SubAgentEnvelope
{
    /// <summary>
    /// Renders the envelope the parent's model reads.
    ///
    /// <para>XML-ISH RATHER THAN JSON. The text inside is the child's prose, which routinely
    /// contains braces, quotes and code — embedding that in JSON means escaping it, and a model
    /// reading escaped source is reading something other than what the child wrote.</para>
    /// </summary>
    /// <summary>
    /// Reads the <c>state</c> back out of a rendered envelope, or null if this is not one.
    ///
    /// <para>THE INVERSE OF <see cref="Render(string, string?, SendResult, string?)"/>, and deliberately parsed rather than threaded
    /// alongside: the envelope is the one artefact that always carries the outcome, so a caller that
    /// has the string has the truth. Threading a second copy of the same fact through the spawn
    /// dispatch would create two things that can disagree — and the failure would be silent, since
    /// both are plausible strings.</para>
    ///
    /// <para>Null for an ordinary tool result, which is how a non-spawn call is told apart.</para>
    /// </summary>
    public static string? StateOf(string? envelope) => Attribute(envelope, "state=\"");

    /// <summary>
    /// The envelope's state word for an outcome — the word <see cref="StateOf"/> reads back.
    ///
    /// <para>ONE MAPPING, for a caller that must announce how a run ended when the envelope it rendered
    /// describes something else: a stop that overtook a whole answer renders the answer as completed, but the
    /// run was cancelled.</para>
    /// </summary>
    internal static string WordFor(SendOutcome outcome) => outcome switch
    {
        SendOutcome.Completed => "completed",
        SendOutcome.Capped => "capped",
        SendOutcome.Stuck => "stuck",
        SendOutcome.Cancelled => "cancelled",
        SendOutcome.Silent => "no-answer",
        _ => "error",
    };

    /// <summary>
    /// Reads the child's <c>id</c> back out of a rendered envelope, or null if this is not one.
    ///
    /// <para>PARSED RATHER THAN THREADED, for the reason <see cref="StateOf"/> gives at length: the
    /// envelope is the one artefact that always carries the id, so a caller holding the string holds
    /// the truth. A second copy carried alongside it would be a second thing that can disagree, and
    /// both would be plausible strings.</para>
    ///
    /// <para>This is what lets a UI join a finished spawn ROW to the CHILD's own tool calls, which
    /// are reported under the child's agent id and under no other key.</para>
    /// </summary>
    public static string? IdOf(string? envelope) => Attribute(envelope, "id=\"");

    /// <summary>
    /// Reads the child's <c>name</c> — the handle it is kept under — back out of a rendered envelope or
    /// receipt, or null when there is none.
    ///
    /// <para>PARSED for the reason <see cref="StateOf"/> gives: a background spawn's receipt is the one
    /// artefact that carries the handle the parent's later <c>task_output</c> and <c>task_stop</c> take.</para>
    /// </summary>
    public static string? NameOf(string? envelope) => Attribute(envelope, "name=\"");

    /// <summary>
    /// The state a background spawn's receipt carries: the child has started, and its report comes later.
    /// </summary>
    public const string Running = "running";

    /// <summary>
    /// One attribute of the opening tag, or null.
    ///
    /// <para>ONLY IN THE OPENING TAG: the same attribute appearing later is the CHILD's own text — it
    /// routinely writes HTML and XML — and is not ours to read.</para>
    /// </summary>
    private static string? Attribute(string? envelope, string marker)
    {
        if (string.IsNullOrEmpty(envelope)) return null;

        var i = envelope.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;

        var close = envelope.IndexOf('>', 0);
        if (close >= 0 && i > close) return null;

        var start = i + marker.Length;
        var end = envelope.IndexOf('"', start);
        return end > start ? envelope[start..end] : null;
    }

    /// <summary>
    /// What a background spawn returns at once, in place of an answer.
    ///
    /// <para>IT SAYS WHAT HAPPENS NEXT: the report arrives as a message without the parent doing anything,
    /// and a model not told so polls for it — or ends its turn early and stops before the answer comes.</para>
    ///
    /// <para>THE NEXT CALLS ARE SPELLED OUT, id and all: a model that copies a call does not have to recall a
    /// tool name or a handle, and recalling them is where a model goes wrong.</para>
    ///
    /// <para>THE WRITE WARNING ONLY FOR A CHILD THAT CAN WRITE: two agents editing one file each believe the
    /// other did not, and a background spawn returns at once, so nothing holds the parent off the files the
    /// child is working on.</para>
    /// </summary>
    /// <summary>
    /// The receipt for a foreground child the user moved to the background: the background receipt, prefaced by who
    /// moved it — so the model does not wonder why a call it made in the foreground answered like a background one.
    /// </summary>
    public static string Moved(string childId, string name, bool canWrite) =>
        Receipt(childId, name, canWrite).Replace("\nRunning in the background.",
            $"\n{MovedByTheUser} Running in the background.", StringComparison.Ordinal);

    private const string MovedByTheUser = "The user moved it to the background.";

    /// <summary>Whether a receipt is <see cref="Moved"/>'s — a child that has been working since its spawn, not since now.</summary>
    public static bool WasMoved(string? envelope) =>
        StateOf(envelope) == Running && envelope!.Contains(MovedByTheUser, StringComparison.Ordinal);

    public static string Receipt(string childId, string name, bool canWrite) =>
        $"<sub_agent id=\"{childId}\" name=\"{name}\" state=\"{Running}\">\n"
        + "Running in the background. Its report will come to you as a message when it finishes — in this "
        + "turn if you are still working, as a new turn if you are not. Do not poll task_list for it and do "
        + "not do this work yourself; carry on with other work, or end your turn.\n"
        + $"If your next step cannot go on without its answer: task_output {{\"id\": \"{name}\", \"wait\": true}}\n"
        + $"To stop it: task_stop {{\"id\": \"{name}\"}}"
        + (canWrite ? "\nIt may edit files. Do not edit what it is working on until it reports." : "")
        + "\n</sub_agent>";

    /// <summary>Renders the envelope the parent's model reads as this spawn's tool result.</summary>
    /// <param name="childId">The child's own agent id, for a reader tracing it back to its logs.</param>
    /// <param name="name">
    /// The handle this child is reachable by, or null when nothing kept it.
    ///
    /// <para>IN THE ENVELOPE RATHER THAN LEFT TO A LISTING CALL, because it is what a later
    /// <c>agent_send</c> takes. Without it the model must call <c>task_list</c> after every spawn
    /// just to learn what it created a moment ago.</para>
    /// </param>
    /// <param name="outcome">How the run ended — completed, capped, stuck, cancelled or failed.</param>
    /// <param name="text">The child's answer, or its account of why there is not one.</param>
    public static string Render(string childId, string? name, SendOutcome outcome, string text) =>
        Render(childId, name, new SendResult(text, outcome), work: null);

    /// <summary>
    /// The envelope for a finished run, with — for one that answered nothing — why, what the child did first, and how
    /// to get its answer.
    /// </summary>
    /// <param name="childId">The child's agent id.</param>
    /// <param name="name">The handle it is kept under; null when nothing kept it.</param>
    /// <param name="result">How the run ended, its text, and why an empty one was empty.</param>
    /// <param name="work">What the child did, from <see cref="WorkOf"/>; null when it is not known.</param>
    public static string Render(string childId, string? name, SendResult result, string? work)
    {
        var outcome = result.Outcome;
        var text = result.Text;
        var state = WordFor(outcome);

        // A NOTE ON THE UNHAPPY STATES, because "capped" alone is a word the model has to interpret.
        // The parent must know that what follows is an ACCOUNT OF UNFINISHED WORK rather than an
        // answer — that is the entire reason the field exists.
        var note = outcome switch
        {
            SendOutcome.Capped =>
                "\nThis agent hit its turn limit before finishing. What follows is its own summary of "
                + "how far it got, NOT a completed answer — check what remains before relying on it.",
            SendOutcome.Stuck =>
                "\nThis agent stopped making progress: it repeated the same call and got the same "
                + "result. What follows may be incomplete.",
            SendOutcome.Failed =>
                "\nThis agent failed. What follows is the error, not an answer.",
            SendOutcome.Cancelled =>
                "\nThis agent was cancelled before finishing.",
            SendOutcome.Silent => Silent(name, result.Why, work),
            _ => "",
        };

        // THE NAME IS AN ATTRIBUTE, AND ABSENT WHEN NOTHING KEPT THIS CHILD. An empty name= would
        // read as a handle that happens to be blank, which is a name the model would then try to
        // send to; omitting the attribute says plainly that there is nothing to reach.
        var handle = name is null ? "" : $" name=\"{name}\"";
        return $"<sub_agent id=\"{childId}\"{handle} state=\"{state}\">{note}\n{text}\n</sub_agent>";
    }

    /// <summary>
    /// What a run that answered nothing tells the parent: why, what the child did first, and the way on.
    ///
    /// <para>ASK IT, DON'T RE-RUN IT, WHEN IT IS KEPT. A kept child still holds everything it read and found, so a
    /// send asking for its answer recovers the work in one turn; a re-run starts from nothing and redoes every turn.
    /// A child nothing kept cannot be asked, so it is checked for and re-run.</para>
    /// </summary>
    private static string Silent(string? name, string? why, string? work)
    {
        var said = $"\nThis agent produced no answer — {why ?? "its reply was empty"}.";
        if (work is not null) said += $" Before that it made {work}.";
        if (name is null)
            return said + " It may have changed files first, so check the tree before assuming nothing changed, "
                 + "and re-run it if the work is still needed.";
        return said + " It still has what it read and found. To get its answer without redoing the work: "
             + $"agent_send {{\"to\": \"{name}\", \"prompt\": \"Answer from what you have found so far.\"}}"
             + $"\nTo see what it did first: task_output {{\"id\": \"{name}\"}}";
    }

    /// <summary>
    /// What a child did, for a parent deciding what to make of a run: how many tool calls, and the latest few.
    /// </summary>
    public static string WorkOf(SubAgent child)
    {
        var jobs = child.Jobs.Jobs;
        var latest = jobs.OrderBy(j => j.CreatedAt).TakeLast(3).Select(j => j.DisplayName).ToList();
        var count = jobs.Count == 1 ? "1 tool call" : $"{jobs.Count} tool calls";
        return latest.Count == 0 ? count : $"{count}; latest: {string.Join(", ", latest)}";
    }
}
