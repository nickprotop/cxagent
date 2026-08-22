namespace CxAgent.UI;

/// <summary>
/// What <c>--help</c> prints, and what a bad argument prints after the complaint.
///
/// <para>THE SAME TEXT BOTH TIMES, DELIBERATELY. A user who mistypes a flag has just proved they do
/// not know the flags; answering with only "unknown option" and an exit code makes them go and find
/// the help themselves, which is a step nobody enjoys and some never take. The error names what went
/// wrong, then this says what was available — the reading order a person actually wants.</para>
///
/// <para>ITS OWN FILE because it is prose, not logic. Kept beside the parser it describes so the two
/// are edited together: a flag added to <see cref="CommandLine"/> and not to this is a flag nobody
/// will find.</para>
/// </summary>
public static class Usage
{
    /// <summary>
    /// The registered themes, wrapped to fit the options column.
    ///
    /// <para>ASKED OF THE REGISTRY, NOT LISTED BY HAND. The set is whatever the window framework has
    /// registered plus cxagent's own, so a hardcoded list would go stale the first time either
    /// changed — and a help text that names a theme the app does not have is worse than one that
    /// names none.</para>
    ///
    /// <para>A THROWAWAY WINDOW SYSTEM, because --help must answer without building the app. It runs
    /// against a headless driver, touches no terminal state, and is discarded immediately.</para>
    /// </summary>
    private static string ThemeNames()
    {
        try
        {
            var system = new SharpConsoleUI.ConsoleWindowSystem(
                new SharpConsoleUI.Drivers.HeadlessConsoleDriver(80, 24));
            CxAgentTheme.Install(system, null);

            var names = system.ThemeRegistryService.GetAvailableThemes()
                .Select(t => t.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return string.Join(", ", names);
        }
        catch
        {
            // NEVER FAIL THE HELP. Whatever went wrong building a headless window system, the rest of
            // this text is still worth printing — a missing list is a smaller loss than no help.
            return "(run cxagent and press F9 to see the list)";
        }
    }

    /// <summary>The full help text, without a trailing newline.</summary>
    public static string Text =>
        $"""
        cxagent — a terminal AI coding agent

        USAGE
          cxagent [options]

        Run it in the folder you want to work in, then type what you want in plain
        language. Reading and writing inside that folder is free; anything outside it,
        and every shell command, asks first.

        OPTIONS
          --instance NAME     Start on a named provider from config.json instead of the
                              default. The status line shows which one is live.
          --model NAME        Alias for --instance.
          --theme NAME        Start in a named theme, case-insensitive. An unknown name
                              falls back to '{Features.DefaultTheme}' rather than refusing
                              to start. Available themes:
                                {ThemeNames()}
          --config-dir DIR    Use DIR for config.json, history and session state instead
                              of the usual location. Useful for a throwaway setup that
                              leaves your real one alone.
          --resume [ID]       Reopen the last session in this folder, or a specific one
                              by id. Without an id it takes the most recent.
          --sessions [all]    List this folder's sessions and exit. 'all' lists every
                              folder's.
          --mock              Run against a scripted provider instead of a real model.
                              Nothing is sent anywhere and nothing is spent.
          --version           Print the version and exit.
          --help              Print this and exit.

        CONFIGURATION
          Providers, agent types, MCP servers and permissions live in config.json under
          your config directory. Run cxagent once with no configuration and it offers to
          write one.

        MORE
          /help inside the app lists what you can type at the prompt.
          https://github.com/nickprotop/cxagent
        """;
}
