using System.Text;

namespace CxAgent.Core.Colleagues;

/// <summary>One colleague as the lead's prompt lists it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Description">Its one line, distilled with its experience.</param>
/// <param name="Knows">The folders it has worked in, by their own names, most recent first.</param>
public sealed record ColleagueLine(string Name, string Description, IReadOnlyList<string> Knows)
{
    /// <summary>The line for a colleague as the library has it.</summary>
    public static ColleagueLine Of(ColleagueRecord record) => new(record.Name, record.Description,
        [.. record.Workplaces.OrderByDescending(w => w.Last).Select(w => Path.GetFileName(w.Folder.TrimEnd('/', '\\'))).Distinct()]);
}

/// <summary>
/// The colleagues as the lead's prompt lists them: name, its line, and the codebases it knows.
///
/// <para>CAPPED, for memory's reason: a library grown over a year must not crowd out the conversation it sits above.
/// Past the cap the rest are counted, and the colleague tool lists them all.</para>
/// </summary>
public static class ColleagueIndex
{
    /// <summary>How long the list may run — about a thousand tokens.</summary>
    public const int DefaultCap = 4_000;

    /// <summary>The list, in the order given (most recently worked first), or empty when there are none.</summary>
    public static string Render(IReadOnlyList<ColleagueLine> lines, int cap)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            var c = lines[i];
            var line = "- " + c.Name
                + (c.Description.Length > 0 ? " — " + c.Description : "")
                + (c.Knows.Count > 0 ? " · knows: " + string.Join(", ", c.Knows) : "") + "\n";
            if (sb.Length + line.Length > cap)
            {
                sb.Append($"- {lines.Count - i} more — the colleague tool lists them all.\n");
                break;
            }
            sb.Append(line);
        }
        return sb.ToString();
    }
}
