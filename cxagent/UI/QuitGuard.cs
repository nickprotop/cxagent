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
}
