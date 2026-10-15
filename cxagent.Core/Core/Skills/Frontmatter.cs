namespace CxAgent.Core.Skills;

/// <summary>A file's <c>---</c> block, read as flat <c>key: value</c> lines, and what follows it.</summary>
/// <param name="Present">Whether the file opened with <c>---</c> at all.</param>
/// <param name="Fields">Keys as written, matched case-insensitively.</param>
/// <param name="Body">Everything after the block, trimmed; the whole text when there was no block.</param>
/// <param name="Error">Why the block could not be read, or null.</param>
public sealed record FrontmatterResult(bool Present, IReadOnlyDictionary<string, string> Fields,
    string Body, string? Error);

/// <summary>
/// The frontmatter reader skills and command files share.
///
/// <para>HAND-ROLLED, NO YAML DEPENDENCY. Real skills and Claude Code commands use flat
/// <c>key: value</c> lines, and a package for a handful of string fields is a dependency every
/// consumer of Core pays for.</para>
///
/// <para>SPLIT ON THE FIRST COLON ONLY. Descriptions are prose and contain colons, and splitting on
/// every colon truncates exactly the field the model and the palette show.</para>
/// </summary>
public static class Frontmatter
{
    public static FrontmatterResult Parse(string text)
    {
        // A BOM AND CRLF ARE HOW A FILE WRITTEN ON WINDOWS ARRIVES. Neither is content, and leaving
        // either in place makes the opening "---" fail to match on a file that is perfectly valid.
        var lines = text.TrimStart('﻿').Replace("\r\n", "\n").Split('\n');
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (lines.Length == 0 || lines[0].Trim() != "---")
            return new FrontmatterResult(false, fields, string.Join("\n", lines).Trim(), null);

        var end = -1;
        for (var i = 1; i < lines.Length; i++)
            if (lines[i].Trim() == "---") { end = i; break; }

        if (end < 0)
            return new FrontmatterResult(true, fields, "", "frontmatter is never closed with ---");

        for (var i = 1; i < end; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;                       // blank, comment, or continuation
            fields[lines[i][..colon].Trim()] = Unquote(lines[i][(colon + 1)..].Trim());
        }

        return new FrontmatterResult(true, fields, string.Join("\n", lines.Skip(end + 1)).Trim(), null);
    }

    /// <summary>
    /// Strips the quotes YAML allows around a scalar — a description containing a colon has to be
    /// quoted to be valid YAML. ONLY WHEN BOTH ENDS MATCH, so an apostrophe inside is left alone.
    /// </summary>
    public static string Unquote(string value) =>
        value.Length >= 2
        && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    /// <summary>A boolean field: "true"/"false", case-insensitive; anything else is the default.</summary>
    public static bool Flag(IReadOnlyDictionary<string, string> fields, string key, bool otherwise) =>
        fields.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : otherwise;
}
