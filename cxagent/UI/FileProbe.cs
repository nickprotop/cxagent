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
    /// True when a leading chunk of a file is not text.
    ///
    /// <para>A NULL BYTE SETTLES IT. Executables, images and archives carry thousands, and no valid
    /// UTF-8 text contains one.</para>
    ///
    /// <para>AND SO DOES A DECODE THAT MOSTLY FAILS, which the null-byte test alone misses: 400 bytes
    /// of random data have no zero byte roughly a fifth of the time, and a compressed or encrypted
    /// fragment often has none at all. Such a file passes the first test and then renders as a
    /// screenful of replacement characters — the exact outcome both tests exist to prevent. Measuring
    /// what decoding actually produced is the direct question, where the null byte is a proxy.</para>
    ///
    /// <para>THE THRESHOLD IS DELIBERATELY HIGH. Text that is merely in an encoding we did not guess
    /// still has structure worth showing, and a file the user asked for by name deserves the benefit
    /// of the doubt; a third of a file arriving as U+FFFD is past any doubt. An empty file decodes
    /// cleanly to nothing and opens as an empty buffer.</para>
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head)
    {
        if (head.IndexOf((byte)0) >= 0) return true;
        if (head.IsEmpty) return false;

        var text = System.Text.Encoding.UTF8.GetString(head);
        if (text.Length == 0) return false;

        var replacements = 0;
        foreach (var c in text)
            if (c == '\uFFFD') replacements++;

        return replacements * 3 > text.Length;
    }
}
