namespace CxAgent.Core.Agent;

/// <summary>
/// How this session is set up to work — every switch that changes what the agent may do, in one
/// place.
///
/// <para>WHY A RECORD RATHER THAN A WIDER ENUM. The axes are INDEPENDENT: whether an agent may
/// delegate says nothing about whether it may write files, and neither says whether it is planning
/// or building. Folding them into one enum would make "fan-out, read-only, planning" unrepresentable
/// without a value per combination — the classic mistake this shape exists to avoid.</para>
///
/// <para>IMMUTABLE, AND CHANGED WITH <c>with</c>. A mutable object shared between the host and a
/// running turn would let a mid-turn switch take effect halfway through a request, so the model
/// would be offered one tool set and judged against another. Replacing the whole value means a turn
/// reads whatever was true when it started.</para>
///
/// <para>TODAY IT HOLDS ONE AXIS. That is the point: <see cref="AgentMode"/> was a bare enum
/// threaded through ten files, and every future axis would have threaded itself the same way. The
/// next one is a property here and a default — no signature anywhere else changes.</para>
/// </summary>
/// <param name="Agent">Whether this agent works alone or may delegate to sub-agents.</param>
public readonly record struct WorkingMode(AgentMode Agent = AgentMode.Single)
{
    /// <summary>How a session starts when nobody has said otherwise.</summary>
    public static WorkingMode Default => new(AgentMode.Single);

    /// <summary>
    /// An agent mode IS a working mode with nothing else set.
    ///
    /// <para>Exists so the switch from a bare enum stays mechanical: <c>Mode = AgentMode.FanOut</c>
    /// keeps compiling at two dozen call sites that have no opinion about the other axes. It is a
    /// widening — no information is lost — so there is no case where the implicit form means
    /// something different from the explicit one.</para>
    /// </summary>
    public static implicit operator WorkingMode(AgentMode agent) => new(agent);

    /// <summary>True when this session may delegate. Reads at the call site the way the question is
    /// actually asked, rather than making every caller compare against an enum member.</summary>
    public bool CanDelegate => Agent == AgentMode.FanOut;

    /// <summary>
    /// What the user reads. Delegates to <see cref="AgentModes"/> so the CLI, the <c>/mode</c>
    /// command and the status bar can never disagree about what a mode is called — and so that when
    /// a second axis arrives, there is one place that decides how a combination is spelled.
    /// </summary>
    public override string ToString() => AgentModes.Name(Agent);
}
