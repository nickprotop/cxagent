using CxAgent.Core.Agents;

namespace CxAgent.Core.Sessions;

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
/// <para>IT HELD ONE AXIS, AND THE SHAPE PAID OFF EXACTLY AS INTENDED. <see cref="AgentMode"/> was a
/// bare enum threaded through ten files, and every future axis would have threaded itself the same
/// way. <see cref="EditMode"/> arrived as a property here and a default — the implicit conversion
/// below kept two dozen call sites compiling, and no signature outside this file had to change to
/// admit it.</para>
///
/// <para>THE THIRD AXIS NAMED ABOVE — planning versus building — WAS CONSIDERED AND DECLINED. It
/// would have withdrawn the write tools and restricted shell to <c>ReadOnlyCommands</c>, but
/// withholding tools does not make a model plan well: the briefing does, and the <c>planner</c> agent
/// type already carries one. Two mechanisms for one intent would owe an answer to which governs. The
/// example in the paragraph above is left as written because it is still the right argument for the
/// SHAPE, whether or not that particular axis was ever built.</para>
/// </summary>
/// <param name="Agent">Whether this agent works alone or may delegate to sub-agents.</param>
/// <param name="Edits">When a file write happens without asking.</param>
public readonly record struct WorkingMode(
    AgentMode Agent = AgentMode.FanOut,
    EditMode Edits = EditMode.AlwaysAsk)
{
    /// <summary>
    /// How a session starts when nobody has said otherwise.
    ///
    /// <para><see cref="EditMode.AlwaysAsk"/> BECAUSE A DEFAULT IS NOT A CHOICE. Every other
    /// permissive state in cxagent is reached by an act — trust is answered, a rule is granted, a
    /// mode is picked with Shift+Tab and then remembered per folder. AcceptEdits as the default was
    /// the one silent widening left: a user who had never expressed an opinion got silent in-cwd
    /// writes because that happened to be the pre-<see cref="EditMode"/> behaviour. Preserving old
    /// behaviour was the right call while the axis was new and nothing could set it; it stopped
    /// being right once the axis had a CLI flag, a key binding and per-folder memory.</para>
    ///
    /// <para>WHAT THIS DOES NOT CHANGE, which is most of it. A folder with a remembered mode is
    /// restored exactly, so anyone who has ever picked a mode where they work sees no difference —
    /// this governs only a folder nobody has chosen for. And it never widens: AlwaysAsk is the
    /// narrowest edit mode, so no session that was asking starts writing silently because of
    /// this.</para>
    ///
    /// <para>THE COST IS REAL AND ACCEPTED: a first run in a new folder now prompts on its first
    /// write, one Shift+Tab from never prompting again there. That is the trade the rest of the
    /// system already makes — trust asks once too.</para>
    ///
    /// <para><see cref="AgentMode.FanOut"/> ON THE OTHER AXIS, and the asymmetry is deliberate. The
    /// edits axis defaults to the NARROW value because a permissive default is a silent widening
    /// nobody chose; delegation is not a widening at all — a child runs under the same permissions,
    /// in the same folder, against the same gate. What it changes is capability, and an agent that
    /// cannot delegate is not safer, only less able.</para>
    ///
    /// <para>SO SINGLE IS THE OPT-OUT. A front end with nowhere to show a child's progress asks for
    /// it explicitly, which is the right way round: the capable default, narrowed by a caller who
    /// knows their surface cannot render it.</para>
    /// </summary>
    public static WorkingMode Default => new(AgentMode.FanOut, EditMode.AlwaysAsk);

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
    /// What the user reads. Delegates to <see cref="AgentModes"/> and <see cref="EditModes"/> so the
    /// CLI, the <c>/mode</c> command and the status bar can never disagree about what a mode is
    /// called — and this is the one place that decides how a COMBINATION is spelled.
    ///
    /// <para>AGENT FIRST because it is the coarser fact: whether there is one agent or several frames
    /// everything else, including whose edits are being accepted.</para>
    /// </summary>
    public override string ToString() => $"{AgentModes.Name(Agent)} · {EditModes.Name(Edits)}";
}
