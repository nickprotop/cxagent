namespace CxAgent.Core.Agent;

/// <summary>
/// Whether this session's agent works alone or may delegate.
///
/// <para>TWO THINGS CHANGE, and nothing else: the TOOLS the model is offered, and the SYSTEM PROMPT
/// it is given. Both are rebuilt on every prompt — the tool list at <c>Agent.SendAsync</c>'s request
/// build, the system message reconciled at index 0 — so a mode change takes effect on the next turn
/// with no restart and no rebuilt agent.</para>
///
/// <para>THE CONVERSATION IS UNAFFECTED. It belongs to <see cref="AgentHost"/>, which hands it to the
/// agent rather than the other way round, so messages 1..N are untouched by a switch and only index 0
/// is replaced. History is NOT rewritten either: a <c>task</c> call made in fan-out mode stays
/// in the conversation after switching to single. That is deliberate — erasing history to match
/// current capability would misrepresent what actually happened.</para>
/// </summary>
public enum AgentMode
{
    /// <summary>
    /// One agent, working alone. No spawn tool, and no spawn-related guidance in its prompt.
    ///
    /// <para>THE DEFAULT, because delegation is a choice a user makes rather than one they discover:
    /// a session that can silently spawn is one where a stray tool call costs an unexpected model run.
    /// This mode's prompt is byte-identical to what shipped before sub-agents existed.</para>
    /// </summary>
    Single,

    /// <summary>
    /// The agent may spawn sub-agents. It is offered <c>task</c>, and its prompt carries the
    /// three obligations a parent has towards work it did not do itself (D26).
    /// </summary>
    FanOut,
}

/// <summary>Parsing and display for <see cref="AgentMode"/>, shared by the CLI and the /mode command
/// so the two can never disagree about what a mode is called.</summary>
public static class AgentModes
{
    /// <summary>What the user types and reads. Hyphenated because "fanout" is not a word and
    /// "fan_out" is not something anyone types at a prompt.</summary>
    public static string Name(AgentMode mode) => mode == AgentMode.FanOut ? "fan-out" : "single";

    /// <summary>Every valid value, for an error message that tells the user what to type instead of
    /// only telling them they were wrong.</summary>
    public static string Valid => "single, fan-out";

    /// <summary>
    /// Parses a mode name, or returns null.
    ///
    /// <para>Tolerant of the near-misses people actually type — "fanout", "fan_out" — because
    /// rejecting those teaches nothing and costs a restart when it comes from the command line. Not
    /// tolerant of anything else: a value that silently defaults is how someone concludes sub-agents
    /// are broken when they merely misspelled the flag.</para>
    /// </summary>
    public static AgentMode? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "single" or "solo" => AgentMode.Single,
        "fan-out" or "fanout" or "fan_out" => AgentMode.FanOut,
        _ => null,
    };
}
