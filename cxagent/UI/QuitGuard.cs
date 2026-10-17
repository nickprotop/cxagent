using CxAgent.UI.Config;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// Whether to stop background agents in order to quit or close a tab.
///
/// <para>ASKED, BECAUSE THIS IS THE ONE STOP THAT CANNOT BE UNDONE: a background agent's work exists only
/// in memory until it reports, and the user may not have noticed one is still going. Escape never stops
/// one; quitting or closing its tab would, silently, without this.</para>
///
/// <para>PUBLIC, because this assembly grants no InternalsVisibleTo and the wording is what the tests
/// pin.</para>
/// </summary>
public static class QuitGuard
{
    /// <summary>The lines to ask with, or null when nothing is running and there is nothing to ask.</summary>
    public static string[]? Question(int running) => running <= 0 ? null :
    [
        running == 1 ? "1 background agent is still running." : $"{running} background agents are still running.",
        "Stopping now loses the work they have not reported.",
    ];

    /// <summary>
    /// Whether Ctrl+Q asks "Quit cxagent?" (<see cref="QuitDialog"/>) before going on to <see cref="ConfirmAsync"/>.
    ///
    /// <para>ONLY WITH NOTHING RUNNING. With agents running, <see cref="ConfirmAsync"/> asks the question that
    /// matters, and asking both would put a "yes, quit" in front of the "yes, lose their work". Turning the
    /// plain question off is therefore never a way past the agents one: that one is not this one.</para>
    /// </summary>
    public static bool AsksQuitCxagent(int running, bool confirmQuit) => running == 0 && confirmQuit;

    /// <summary>
    /// True to go ahead — nothing was running, or the user chose to stop them.
    ///
    /// <para>CANCEL FIRST, because the dialog opens on its first answer and Enter is a reflex: a quit
    /// pressed by habit must not end work that exists nowhere else. Stopping takes a deliberate move
    /// down to it.</para>
    /// </summary>
    public static async Task<bool> ConfirmAsync(ConsoleWindowSystem system, int running) =>
        Question(running) is not { } lines
        || await ConfirmDialog.AskAsync(system, "Background agents are running", lines, ["Cancel", "Stop them"])
            == "Stop them";

    /// <summary>
    /// What to ask before closing a tab, or null when it has nothing running and closes without a question.
    ///
    /// <para>COMMANDS ARE ASKED ABOUT AS WELL AS AGENTS, unlike quitting: a tab closes as a whole, so its
    /// commands would die with it, and a dev server or a watcher is often meant to outlive the tab. Keeping
    /// them is offered only when there are some — agents cannot be kept, since an agent's work exists only in
    /// the conversation that is closing.</para>
    ///
    /// <para>CANCEL FIRST, for the reason <see cref="ConfirmAsync"/> gives.</para>
    /// </summary>
    public static TabCloseQuestion? TabQuestion(TabWork work)
    {
        if (work is { Agents: <= 0, Commands: <= 0 }) return null;

        var lines = new List<string>
        {
            work.Agents > 0 && work.Commands > 0
                ? $"{Count(work.Agents, "agent")} and {Count(work.Commands, "command")} are still running."
                : work.Agents > 0
                    ? $"{Count(work.Agents, "background agent")} {(work.Agents == 1 ? "is" : "are")} still running."
                    : $"{Count(work.Commands, "background command")} {(work.Commands == 1 ? "is" : "are")} still running.",
        };
        if (work.Agents > 0) lines.Add("Stopping loses the work the agents have not reported.");

        var answers = new List<string> { CancelAnswer, StopAnswer };
        if (work.Commands > 0) answers.Add(KeepAnswer(work.Commands));
        return new TabCloseQuestion([.. lines], [.. answers]);
    }

    /// <summary>
    /// Asks <see cref="TabQuestion"/> and answers what the user chose. A tab with nothing running closes
    /// without a question, which is the same as stopping everything: there is nothing to stop.
    /// </summary>
    public static async Task<TabClose> ConfirmCloseTabAsync(ConsoleWindowSystem system, TabWork work)
    {
        if (TabQuestion(work) is not { } question) return TabClose.StopEverything;
        var answer = await ConfirmDialog.AskAsync(system, "This tab has background work", question.Lines, question.Answers);
        return answer == StopAnswer ? TabClose.StopEverything
            : answer == KeepAnswer(work.Commands) ? TabClose.KeepCommands
            : TabClose.Cancel;
    }

    /// <summary>
    /// The question before deleting a finished agent whose commands still run.
    ///
    /// <para>ASKED AS CLOSING A TAB IS, because it is the same moment: work would end as a side effect of
    /// something else, and a dev server or a watcher an agent started may be meant to outlive it. Cancel first,
    /// and keeping them offered by count, as there.</para>
    ///
    /// <para>THE HANDLE IN THE BODY, NOT THE TITLE: a dialog's title is cut to its width, and a handle the model
    /// wrote from a description is long enough to lose its end there.</para>
    /// </summary>
    public static TabCloseQuestion DeleteQuestion(string name, int commands) => new(
        [$"'{name}' started {Count(commands, "background command")} that {(commands == 1 ? "is" : "are")} still running."],
        [CancelAnswer, DeleteStopAnswer, DeleteKeepAnswer(commands)]);

    /// <summary>Asks <see cref="DeleteQuestion"/> and answers what the user chose.</summary>
    public static async Task<AgentDelete> ConfirmDeleteAsync(ConsoleWindowSystem system, string name, int commands)
    {
        var question = DeleteQuestion(name, commands);
        var answer = await ConfirmDialog.AskAsync(system, "This agent has background work", question.Lines, question.Answers);
        return answer == DeleteStopAnswer ? AgentDelete.StopCommands
            : answer == DeleteKeepAnswer(commands) ? AgentDelete.KeepCommands
            : AgentDelete.Cancel;
    }

    private const string DeleteStopAnswer = "Delete and stop them";
    private static string DeleteKeepAnswer(int commands) => $"Delete, keep {Count(commands, "command")} running";

    private const string CancelAnswer = "Cancel";
    private const string StopAnswer = "Close and stop everything";
    private static string KeepAnswer(int commands) => $"Close, keep {Count(commands, "command")} running";
    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}

/// <summary>What a tab still has going in the background when it is asked to close.</summary>
/// <param name="Agents">Its background agents, queued or running.</param>
/// <param name="Commands">Its background shells still running — its own agent's and its sub-agents'.</param>
public readonly record struct TabWork(int Agents, int Commands);

/// <summary>The lines and the answers of the tab-close question, Cancel first.</summary>
public sealed record TabCloseQuestion(string[] Lines, string[] Answers);

/// <summary>What the user chose when closing a tab with background work.</summary>
public enum TabClose
{
    /// <summary>Keep the tab open, and everything it is running.</summary>
    Cancel,
    /// <summary>Close it, stopping its agents and its commands.</summary>
    StopEverything,
    /// <summary>Close it, stopping its agents but leaving its commands running.</summary>
    KeepCommands,
}

/// <summary>What the user chose when deleting an agent whose commands still run.</summary>
public enum AgentDelete
{
    /// <summary>Keep the agent, and its commands.</summary>
    Cancel,
    /// <summary>Delete it, stopping its commands.</summary>
    StopCommands,
    /// <summary>Delete it, leaving its commands running as the session's.</summary>
    KeepCommands,
}
