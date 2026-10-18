using System.Globalization;
using CxAgent.Core.Agents;
using CxAgent.Core.Helpers;

namespace CxAgent.Core.Export;

/// <summary>Where an export is written, and how its size reads.</summary>
public static class ExportTarget
{
    /// <summary>
    /// Above this, the clipboard is warned about: many apps a paste lands in — a chat box, an issue form — choke on a
    /// paste this size, and a file is the better place for it.
    /// </summary>
    public const int ClipboardWarnBytes = 1024 * 1024;

    /// <summary>
    /// <c>yyyy-MM-dd-&lt;title&gt;.md</c>: dated so a folder of exports sorts by when, titled so it reads as what.
    /// </summary>
    private const int MaxSlug = 30;

    public static string DefaultFileName(string? title, DateTimeOffset at)
    {
        var slug = string.IsNullOrWhiteSpace(title) ? "session" : SubAgentStore.Slug(title, []);

        // SHORT ENOUGH TO READ WHOLE in the dialog's path box; a title is often a paragraph, and its first few words
        // name it as well as all of them.
        if (slug.Length > MaxSlug) slug = slug[..(slug.LastIndexOf('-', MaxSlug) is > 10 and var cut ? cut : MaxSlug)].TrimEnd('-');
        return $"{at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}-{slug}.md";
    }

    /// <summary>
    /// Writes <paramref name="markdown"/> to <paramref name="path"/>, whole or not at all.
    ///
    /// <para>THROUGH A TEMPORARY FILE AND A MOVE: a full disk or a killed process mid-write must not leave a truncated
    /// export where an older one was. Throws on failure — the dialog shows why and stays open.</para>
    /// </summary>
    public static void Write(string path, string markdown)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, markdown);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>"812 B", "14 KB", "2.1 MB".</summary>
    public static string Size(int bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{(int)Math.Round(bytes / 1024.0)} KB",
        _ => $"{DisplayNumber.Fixed(bytes / (1024.0 * 1024.0), 1)} MB",
    };
}
