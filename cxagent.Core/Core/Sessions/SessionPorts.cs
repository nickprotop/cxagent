using CxAgent.Core.Agents;

namespace CxAgent.Core.Sessions;

/// <summary>
/// One session's own connections to whatever is driving it.
///
/// <para>NOT "PRESENTATION". Every type here is a Core type over Core's own models — the UI
/// implements them, and so does <see cref="BufferedChatSink"/>, and so would a server, a log file
/// or a test recorder. Naming them for the implementer is the mistake this codebase has already
/// corrected once on these very interfaces.</para>
///
/// <para>ONE SET PER SESSION, which is the whole reason they are separate from
/// <see cref="SharedServices"/>. Two sessions may share a history database; they must not share a
/// transcript.</para>
/// </summary>
public sealed record SessionPorts
{
    /// <summary>What the session reports — text, reasoning, turn boundaries, failures.</summary>
    public required ISessionObserver Observer { get; init; }

    /// <summary>
    /// What it reports about the tools it runs.
    ///
    /// <para>NAMED FOR THE ROLE, NOT THE SUBJECT. This was <c>Tools</c> until consumer-injected
    /// tools needed that name, and they have the better claim on it: they ARE the tools, while this
    /// only watches them. Renaming the observer was the cheaper half — two call sites against a
    /// port that every embedder would otherwise have to think about twice.</para>
    /// </summary>
    public required IToolObserver ToolObserver { get; init; }

    /// <summary>How it asks the user something, or null when there is nobody to ask.</summary>
    public AskUser? Ask { get; init; }

    /// <summary>
    /// Tools this embedder supplies, offered to the model alongside the built-ins. Empty by default.
    ///
    /// <para>PER SESSION, like everything else here, and for the sharpest version of the reason:
    /// a tool that renders into ONE session's transcript must never be handed to another. An
    /// injected tool routinely captures the surface it draws on — a chat control, a panel, a window
    /// handle — in its closure, so sharing one instance across sessions would write one session's
    /// output into another's screen.</para>
    ///
    /// <para>Wrap each in <see cref="Jobs.GatedAgentTool"/> before it reaches an agent. Passing a
    /// bare tool here is not a compile error and produces a tool that runs UNGATED, which is why
    /// SessionFactory does the wrapping rather than trusting every embedder to remember.</para>
    /// </summary>
    public IReadOnlyList<Jobs.IAgentTool> Tools { get; init; } = [];

    /// <summary>
    /// Which tools THIS conversation is offered. Null means no opinion.
    ///
    /// <para>S2, applied after the manager's S1 (code and config alike) and before any per-request
    /// selection. A later level may narrow further OR reopen with a <c>+</c> term — levels apply in
    /// order rather than intersecting — and none of them reaches a tool this agent structurally
    /// lacks.</para>
    /// </summary>
    public Jobs.ToolSelection? ToolSelection { get; init; }

    /// <summary>
    /// How THIS session's permission questions are judged — its working directory and edit mode.
    ///
    /// <para>PER SESSION, unlike the gate that asks them. One gate serves the process because stored
    /// rules and the prompt queue must be shared; the policy behind it must not be, because it holds
    /// a root and a mode that belong to one conversation. Captured in the gate, a second session
    /// would be judged against the first's folder and the first's <c>/mode edits</c> — allowing a
    /// write to a checkout the user never approved, with every layer behaving correctly.</para>
    ///
    /// <para>Null on the paths with no gating at all (headless, tests), where there is nothing to
    /// judge.</para>
    /// </summary>
    public Permissions.PermissionPolicy? Policy { get; init; }
}
