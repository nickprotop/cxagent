using CxAgent.Core.Agents;

namespace CxAgent.Headless;

public enum HeadlessEnd { Answered, Failed, TurnLimit, Interrupted, Terminated }

/// <summary>How a run ended, as an exit code and as Claude Code's result subtype.</summary>
public static class HeadlessEnds
{
    /// <summary>
    /// STUCK AND SILENT ARE FAILURES. Stuck carries text, but the run was looping without progress;
    /// Silent carries none. Neither is the answer a script asked for.
    /// </summary>
    public static HeadlessEnd From(SendOutcome outcome, bool terminated) => outcome switch
    {
        SendOutcome.Completed => HeadlessEnd.Answered,
        SendOutcome.Capped => HeadlessEnd.TurnLimit,
        SendOutcome.Cancelled => terminated ? HeadlessEnd.Terminated : HeadlessEnd.Interrupted,
        _ => HeadlessEnd.Failed,
    };

    // 130 AND 143 ARE THE SHELL'S OWN CONVENTION (128 + the signal), and Claude Code's for SIGTERM.
    public static int ExitCode(HeadlessEnd end) => end switch
    {
        HeadlessEnd.Answered => 0,
        HeadlessEnd.TurnLimit => 3,
        HeadlessEnd.Interrupted => 130,
        HeadlessEnd.Terminated => 143,
        _ => 1,
    };

    public static string Subtype(HeadlessEnd end) => end switch
    {
        HeadlessEnd.Answered => "success",
        HeadlessEnd.TurnLimit => "error_max_turns",
        _ => "error_during_execution",
    };
}
