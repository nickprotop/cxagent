using CxAgent.Core.Jobs.Builtin;

namespace CxAgent.UI;

/// <summary>A file read once: its text, the conventions to write it back with, and its language.</summary>
public sealed record LoadedFile(string Path, string Text, FileSnapshot Snapshot, string? Language);

/// <summary>
/// Reads a file for a tab, or explains why it cannot.
/// </summary>
public static class FileLoad
{
    /// <summary>How much of the file the binary check looks at.</summary>
    private const int ProbeBytes = 8 * 1024;

    /// <summary>
    /// Loads a file, or returns null with a sentence for the tab to show.
    ///
    /// <para>ONE READ, NOT TWO. The bytes are read once and both questions — is this binary, what
    /// does it say — are answered in memory. Opening the file a second time to decode it would
    /// re-read a file that may have changed between the two opens, and answer the second question
    /// about different content than the first.</para>
    ///
    /// <para>AN UNREADABLE FILE IS REFUSED LIKE A BINARY ONE. Permission denied, a directory, a
    /// deleted path: the tab wants a sentence, not an exception, and the difference between "cannot
    /// read" and "should not show" does not matter to the person reading it.</para>
    /// </summary>
    public static LoadedFile? TryLoad(string path, out string? refusal)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            refusal = $"cannot read {Path.GetFileName(path)}: {ex.Message}";
            return null;
        }

        var head = bytes.AsSpan(0, Math.Min(bytes.Length, ProbeBytes));
        if (FileProbe.LooksBinary(head))
        {
            refusal = $"{Path.GetFileName(path)} looks binary, so it is not shown.";
            return null;
        }

        // THE BOM IS STRIPPED FROM THE TEXT AND REMEMBERED SEPARATELY. Left in, it would show as a
        // stray glyph on line 1 and travel into every edit made near it.
        var hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = System.Text.Encoding.UTF8.GetString(hadBom ? bytes.AsSpan(3) : bytes.AsSpan());

        // THE FILE'S OWN CONVENTION, not the first line's: a file counts as CRLF if it uses CRLF
        // anywhere, which is what FileMutation.WriteAsync restores on save.
        var usesCrlf = text.Contains("\r\n", StringComparison.Ordinal);

        refusal = null;
        return new LoadedFile(path, text,
            new FileSnapshot(text, Existed: true, HadBom: hadBom, UsesCrlf: usesCrlf),
            FileProbe.LanguageFor(path));
    }
}
