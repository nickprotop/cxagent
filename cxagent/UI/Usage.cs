using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using Spectre.Console;
using Spectre.Console.Rendering;

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

    /// <summary>One flag (or flag group) and what it does.</summary>
    private sealed record Entry(string Flags, string Description);

    /// <summary>A titled group of entries, with optional prose between the title and the table.</summary>
    private sealed record Section(string Title, string? Intro, IReadOnlyList<Entry> Entries, string? Footer = null);

    /// <summary>The flag column's width. A flag longer than this gets its own line, with the
    /// description underneath, so one long flag cannot widen the column for every other row.</summary>
    private const int FlagColumn = 22;

    private const string Tagline = "a terminal AI coding agent";

    private const string Synopsis =
        "Run it in the folder you want to work in, then type what you want in plain language. "
        + "Reading and writing inside that folder is free; anything outside it, and every shell command, asks first.";

    private static IReadOnlyList<Section> Sections() =>
    [
        new("OPTIONS", null,
        [
            new("--model NAME", "Start on a named provider instance from config.json instead of the default. The status line shows which one is live."),
            new("--theme NAME", $"Start in a named theme, case-insensitive. An unknown name falls back to '{Features.DefaultTheme}' rather than refusing to start. Available themes: {ThemeNames()}"),
            new("--mode MODE", $"How the agent works: {AgentModes.Valid}. Fan-out lets it hand work to sub-agents; single keeps everything in one agent. Default: fan-out."),
            new("--config-dir DIR", "Use DIR for config.json, history and session state instead of the usual location. Useful for a throwaway setup that leaves your real one alone."),
            new("-c, --continue", "Reopen the most recent session in this folder."),
            new("-r, --resume ID", "Reopen that session. Without an id, the TUI starts with this folder's sessions listed, to pick from."),
            new("--sessions [all]", "List this folder's sessions and exit. 'all' lists every folder's."),
            new("--mock", "Run against a scripted provider instead of a real model. Nothing is sent anywhere and nothing is spent."),
            new("-v, --version", "Print the version and exit."),
            new("-h, --help", "Print this and exit."),
        ]),
        new("HEADLESS", "One goal, no TUI — Claude Code's options.",
        [
            new("-p, --print \"GOAL\"", "Run GOAL and print the result. The goal may also come last, after the options. With no text, stdin is the goal; with text, piped stdin is attached to it."),
            new("--output-format F", "text (the answer), json (one result object) or stream-json (one event per line)."),
            new("--include-partial-messages", "With stream-json: text as it streams."),
            new("--verbose", "With text: progress on stderr."),
            new("--thinking LEVEL", $"Run at this thinking level: {string.Join(", ", ThinkingLevels.Known)}. Without it, the session's own level is kept."),
            new("--max-turns N", "Stop after N turns (exit 3)."),
            new("--permission-mode M", "default or dontAsk (refuse what would prompt), acceptEdits, auto, bypassPermissions."),
            new("--dangerously-skip-permissions", "Approve every prompt, except a file-tool write into a .git, .vscode, .claude or .idea folder, anywhere. Shell commands are approved like anything else, including ones that write into those folders."),
            new("--trust", "Trust this folder for this run only."),
            new("--plugin NAME", "Load that configured plugin for this run, even one with enabled: false (repeatable, or comma-separated); --plugin none loads none."),
        ], Footer: "Exit: 0 answered, 1 failed, 2 usage, 3 turn limit, 130/143 interrupted."),
        new("PLUGIN STORES",
            "A folder of built plugins, one per subfolder, becomes the catalog and zips a web server serves as a plugin store. "
            + "Users then run /plugin store add <name> <base-url>/catalog.json.",
            [
                new("store build FOLDER --url BASE --out DIR [--json]", "Write DIR/catalog.json and one zip per plugin, with downloads addressed under BASE (https, or http://localhost). Writes nothing on any problem. Exit: 0 built, 1 a plugin is at fault, 2 usage or nothing to build."),
            ]),
        new("CONFIGURATION",
            "Providers, agent types, MCP servers and permissions live in config.json under your config directory. "
            + "Run cxagent once with no configuration and it offers to write one.",
            []),
        new("MORE",
            "/help inside the app lists what you can type at the prompt.\nhttps://github.com/nickprotop/cxagent",
            []),
    ];

    /// <summary>
    /// Draws the help onto a console. Colour is Spectre's call: it drops styling on its own when the
    /// output is redirected or NO_COLOR is set, so the same drawing serves a terminal and a pipe.
    ///
    /// <para>EVERYTHING USER-FACING GOES THROUGH <see cref="Markup.Escape"/>. Descriptions contain
    /// square brackets (<c>--sessions [all]</c>) which Spectre would otherwise read as a style tag
    /// and either throw on or silently swallow.</para>
    /// </summary>
    private static void Render(IAnsiConsole console)
    {
        console.MarkupLine($"[bold cyan]cxagent[/] [dim]—[/] {Markup.Escape(Tagline)}");
        console.WriteLine();
        console.MarkupLine("[bold yellow]USAGE[/]");
        console.MarkupLine("  [cyan]cxagent[/] [dim][[options]][/]");
        console.WriteLine();
        console.Write(new Padder(new Markup(Markup.Escape(Synopsis)), new Padding(2, 0, 0, 0)));
        console.WriteLine();

        foreach (var section in Sections())
        {
            console.MarkupLine($"[bold yellow]{Markup.Escape(section.Title)}[/]");
            if (section.Intro is not null)
            {
                console.Write(new Padder(new Markup(Markup.Escape(section.Intro), new Style(decoration: Decoration.Dim)),
                    new Padding(2, 0, 0, 0)));
            }
            if (section.Entries.Count > 0) console.Write(new Padder(EntryTable(section.Entries), new Padding(2, 0, 0, 0)));
            if (section.Footer is not null)
            {
                console.Write(new Padder(new Markup(Markup.Escape(section.Footer), new Style(decoration: Decoration.Dim)),
                    new Padding(2, 0, 0, 0)));
            }
            console.WriteLine();
        }
    }

    /// <summary>
    /// The entries as a stack of per-entry layouts rather than one Table. A table column has a single
    /// width, and a fixed-width column folds an over-long flag mid-word; per entry, a long flag can
    /// instead sit on its own line with the description indented beneath it, while short flags keep
    /// the aligned two-column look. Spectre still wraps each description to the console width.
    /// </summary>
    private static IRenderable EntryTable(IReadOnlyList<Entry> entries)
    {
        var rows = new List<IRenderable>();
        foreach (var e in entries)
        {
            var flags = new Markup(Markup.Escape(e.Flags), new Style(Color.Cyan1));
            var desc = new Markup(Markup.Escape(e.Description));
            if (e.Flags.Length > FlagColumn - 2)
            {
                rows.Add(new Rows(flags, new Padder(desc, new Padding(FlagColumn + 2, 0, 0, 0))));
            }
            else
            {
                var grid = new Grid();
                grid.AddColumn(new GridColumn().Width(FlagColumn).NoWrap());
                grid.AddColumn(new GridColumn());
                grid.AddRow(flags, desc);
                rows.Add(grid);
            }
        }
        return new Rows(rows);
    }

    /// <summary>
    /// Prints the help to the terminal (or <paramref name="writer"/>), in colour when the target is
    /// a terminal that supports it. <paramref name="writer"/> is the stream the caller chose:
    /// stdout for --help, stderr after a bad argument.
    /// </summary>
    public static void Write(TextWriter writer)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer) });
        Render(console);
    }

    /// <summary>
    /// The help as plain text at 80 columns, with no escape codes. Tests and anything that wants a
    /// string use this; it is the same drawing as <see cref="Write"/>, with colour switched off and
    /// the width pinned so the result does not depend on the machine it runs on.
    /// </summary>
    public static string Text
    {
        get
        {
            var buffer = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.No,
                Out = new AnsiConsoleOutput(buffer),
            });
            console.Profile.Width = 80;
            Render(console);
            return string.Join('\n', buffer.ToString().Split('\n').Select(l => l.TrimEnd())).TrimEnd();
        }
    }
}
