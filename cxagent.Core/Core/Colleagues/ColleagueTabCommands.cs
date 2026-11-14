namespace CxAgent.Core.Colleagues;

/// <summary>One command a colleague's tab runs, and what the completion list says of it.</summary>
public sealed record ColleagueTabCommand(string Name, string Summary);

/// <summary>
/// The commands typed in a colleague's Ask or Talk tab — the one table the tab's send runs and both composers offer.
///
/// <para>ONE LIST, THREE READERS. The send that runs a line, the terminal's slash menu and the browser's list all read
/// this, so a composer cannot offer a command the tab would refuse, nor leave out one it runs. Every other slash line
/// is refused in a worker's tab: these act on the colleague in front, which the tab already names.</para>
/// </summary>
public static class ColleagueTabCommands
{
    public const string Clear = "/clear";
    public const string Freeze = "/freeze";
    public const string Unfreeze = "/unfreeze";

    /// <summary>In the order the list shows them.</summary>
    public static IReadOnlyList<ColleagueTabCommand> All { get; } =
    [
        new(Clear, "Start a new conversation from what it has learned"),
        new(Freeze, "Stop it learning — it keeps working and its rounds are kept"),
        new(Unfreeze, "Let it learn again, from now on"),
    ];

    /// <summary>
    /// The commands starting with what is typed. Nothing once there is a space or a second line: these take no
    /// arguments, and a line past the name is words, not one of them.
    /// </summary>
    public static IReadOnlyList<ColleagueTabCommand> Matching(string text) =>
        !text.StartsWith('/') || text.Contains(' ') || text.Contains('\n') ? []
        : [.. All.Where(c => c.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The command a whole line is, or null. Whole lines only: the tab names the colleague, so "/freeze
    /// Charting" in another colleague's tab would act on the one in front, not on Charting.</summary>
    public static string? Of(string line) =>
        All.FirstOrDefault(c => c.Name.Equals(line.Trim(), StringComparison.OrdinalIgnoreCase))?.Name;
}
