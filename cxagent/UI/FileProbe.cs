namespace CxAgent.UI;

/// <summary>
/// The two questions asked of a file before it is put in a tab: what language highlights it, and
/// whether it is text at all.
/// </summary>
public static class FileProbe
{
    /// <summary>
    /// The language id for a path's extension, or null when there is no extension.
    ///
    /// <para>THE DOT IS TRIMMED because <c>Path.GetExtension</c> returns it and
    /// <c>SyntaxHighlighters.For</c> does not want it: <c>For(".cs")</c> is null where
    /// <c>For("cs")</c> resolves. Passing the extension through unchanged would turn highlighting off
    /// for every file, and read as a missing grammar rather than a bug here.</para>
    ///
    /// <para>A LANGUAGE THAT DOES NOT RESOLVE IS NOT AN ERROR. <c>For</c> returns null for "txt" and
    /// anything else TextMate has no grammar for, which is its documented no-highlighter path — the
    /// file still opens, as plain text.</para>
    /// </summary>
    public static string? LanguageFor(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;

        return ext.TrimStart('.').ToLowerInvariant() is { Length: > 0 } id ? id : null;
    }

    /// <summary>
    /// True when a leading chunk of a file contains a null byte.
    ///
    /// <para>THE TEST IS A NULL BYTE, not a character-set heuristic: it is what actually separates
    /// the files that render as a screenful of replacement characters, and it never rejects valid
    /// UTF-8. An empty file has none, so it opens as an empty buffer.</para>
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head) => head.IndexOf((byte)0) >= 0;
}
