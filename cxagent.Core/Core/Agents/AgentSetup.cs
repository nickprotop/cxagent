using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Agents;

/// <summary>
/// Everything an <see cref="Agent"/> is built from, as seven named groups and its conversation.
///
/// <para>GROUPS, NOT A LIST. The constructor's arguments are the same values every caller passes, and as one flat
/// list they compiled cleanly in each other's places — six strings, four of them a folder or a name. A group
/// names what its values are, and passing one in another's place is a build error.</para>
///
/// <para>EVERY GROUP AND THE CONVERSATION ARE REQUIRED, so a setup cannot be half-built: a group left out is a
/// build error, not an agent that silently has no spawn tool. Within a group what is optional is an
/// <c>init</c> member with no initializer — an initializer runs once per record and a <c>with</c> copies its
/// result, so a computed default would be computed when the setup is made, not when the agent is, and two agents
/// built from one setup would share it. Every default is derived in the constructor instead.</para>
/// </summary>
public sealed record AgentSetup
{
    public required AgentModel Model { get; init; }
    public required AgentOutputs Outputs { get; init; }
    public required AgentIdentity Identity { get; init; }
    public required AgentTools Tools { get; init; }
    public required AgentGuards Guards { get; init; }
    public required AgentBrief Brief { get; init; }
    public required AgentLimits Limits { get; init; }

    /// <summary>
    /// The agent's conversation, or null for a fresh one of its own — the right default: an agent that is not
    /// handed a conversation still HAS one, rather than borrowing the caller's. Not instructions: what it is told
    /// is <see cref="AgentBrief"/>.
    /// </summary>
    public required AgentContext? Conversation { get; init; }
}

/// <summary>What an agent talks to, and where its spend is counted.</summary>
/// <param name="Provider">The model this agent talks to. Sub-agents may be given a different one.</param>
/// <param name="Ledger">Where this agent's token spend is recorded; shared with its children.</param>
public sealed record AgentModel(ILlmProvider Provider, TokenLedger Ledger)
{
    /// <summary>
    /// Which configured provider entry this is, for spend attribution. Two entries can serve the same model against
    /// different endpoints, and keying spend by model alone would merge them.
    /// </summary>
    public string? InstanceName { get; init; }
}

/// <summary>Where an agent reports.</summary>
/// <param name="Sink">Where the agent's words go — the observer an embedder supplies.</param>
/// <param name="Jobs">Where tool activity is reported.</param>
public sealed record AgentOutputs(ISessionObserver Sink, IToolObserver Jobs)
{
    /// <summary>Per-turn transcripts on disk, or null to keep none.</summary>
    public LogFileManager? Logs { get; init; }

    /// <summary>
    /// Where a tool this agent calls can send text back to THIS agent, put on every
    /// <see cref="Execution.JobContext"/> it builds. Null wherever no session wired one, and an executor must treat
    /// delivery as best-effort rather than assume a port.
    /// </summary>
    public IAgentDelivery? Delivery { get; init; }
}

/// <summary>Who an agent is, and where it works.</summary>
public sealed record AgentIdentity
{
    /// <summary>The folder this agent works in; also the permission boundary.</summary>
    public string? WorkingDir { get; init; }

    /// <summary>Whether this agent is itself a child — it reports and renders differently.</summary>
    public bool IsSubAgent { get; init; }

    /// <summary>A short name for this agent in the UI, for children whose work needs a row.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// The session agent's own id, for <see cref="Jobs.TaskTools"/>'s kill rule — "the session may kill any job; a
    /// sub-agent only its own". Null means this agent IS the session: a top-level agent has no parent to name, and
    /// its own id is the value a child would be passed anyway. A child is always given the session's id, never its
    /// immediate parent's.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>For a sub-agent, its type and where it runs (`planner on deepseek:…`), shown beside its label in its
    /// permission prompts; null for a session's own agent.</summary>
    public string? RunsOn { get; init; }

    /// <summary>For a sub-agent in a folder of its own, that folder relative to its parent's, shown beside where it
    /// runs; null otherwise.</summary>
    public string? Root { get; init; }
}

/// <summary>What an agent may use.</summary>
/// <param name="Executors">The tool implementations behind the built-in tool names.</param>
public sealed record AgentTools(JobRegistry Executors)
{
    /// <summary>Connected MCP servers, whose tools are offered beside the built-ins.</summary>
    public Mcp.McpToolset? Mcp { get; init; }

    /// <summary>
    /// How this agent delegates, or null if it cannot. A child is constructed without one, which is what makes "no
    /// sub-agents of sub-agents" structural rather than a rule it is asked to obey.
    /// </summary>
    public ISubAgentSpawner? Spawner { get; init; }

    /// <summary>Tools the embedder injected, offered beside the built-ins.</summary>
    public IReadOnlyList<IAgentTool>? Injected { get; init; }

    /// <summary>
    /// A second, LIVE source of tools — read fresh each turn rather than captured, so a tool that starts existing
    /// after this agent was constructed (a plugin loaded at a later turn boundary) is still offered. Null for every
    /// session with no such source.
    /// </summary>
    public Func<IReadOnlyList<IAgentTool>>? Dynamic { get; init; }

    /// <summary>
    /// The guidance those plugins declare, read from the same live source and on the same turn — so a plugin's
    /// instructions arrive with its tools and leave with them. Null when nothing supplies plugins.
    /// </summary>
    public Func<IReadOnlyList<Plugins.PluginInstructions>>? PluginInstructions { get; init; }

    /// <summary>
    /// The slash commands this agent may name to the user, as name-and-summary pairs — the ones that declared
    /// <c>TellTheModel</c>. Null when the host offers none, which is the usual case: a command the USER drives is not
    /// the model's business, and naming it costs tokens in every request to enable a suggestion nobody asked for.
    /// </summary>
    public Func<IReadOnlyList<(string Name, string Summary)>>? ModelFacingCommands { get; init; }

    /// <summary>Which tools this agent is offered, or null for all of them. See <see cref="ToolSelection"/>.</summary>
    public ToolSelection? Selection { get; init; }
}

/// <summary>What checks an agent's actions.</summary>
public sealed record AgentGuards
{
    /// <summary>The session's permission policy, passed to MCP calls. Null refuses every MCP call.</summary>
    public Permissions.PermissionPolicy? Policy { get; init; }

    /// <summary>
    /// The same classifier <see cref="Permissions.PermissionDecider"/> consults when a gated call needs a verdict —
    /// here so the agent can call <see cref="Permissions.ActionClassifier.Speculate"/> the moment a tool call is
    /// PARSED, before <see cref="Permissions.PermissionGatedExecutor"/> asks for one synchronously. Null wherever no
    /// classifier is reachable (headless runs, most tests): the agent then never speculates, and every gated call
    /// pays its own synchronous cost.
    /// </summary>
    public Permissions.ActionClassifier? Classifier { get; init; }

    /// <summary>
    /// How the agent asks its user a question, or null when nobody is listening — a sub-agent, or a headless host.
    /// Null removes the ask_user tool rather than making it fail.
    /// </summary>
    public Func<IReadOnlyList<UserQuestion>, CancellationToken, Task<QuestionAnswers>>? AskUser { get; init; }
}

/// <summary>What an agent is told.</summary>
public sealed record AgentBrief
{
    /// <summary>
    /// The highest-authority text in this agent's prompt — what an agent TYPE is told it is for. Null for an agent
    /// with no type.
    /// </summary>
    public string? Briefing { get; init; }

    /// <summary>What the parent already knew and passed down. Ranks below the briefing.</summary>
    public string? CallerContext { get; init; }

    /// <summary>Where user-level AGENTS.md and friends are read from.</summary>
    public string? GlobalInstructionsDir { get; init; }

    /// <summary>The day the session began, for the system prompt; null is the day the agent is built.</summary>
    public DateOnly? StartedOn { get; init; }
}

/// <summary>How far an agent may go.</summary>
/// <param name="MaxTurns">How many turns one goal may take before the agent stops and summarises; 0 or less is
/// unbounded.</param>
public sealed record AgentLimits(int MaxTurns)
{
    /// <summary>
    /// Input tokens past which the loop compresses its own context, or null to never compress — the bound that
    /// ends a session that cannot continue, where a turn cap would end one by a number unrelated to the task.
    /// </summary>
    public int? CompressAbove { get; init; }
}
