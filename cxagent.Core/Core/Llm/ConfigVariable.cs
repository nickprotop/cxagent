using System.Text.RegularExpressions;

namespace CxAgent.Core.Llm;

/// <summary>
/// <c>{env:NAME}</c> and <c>{file:path}</c> placeholders, so a credential does not have to be typed
/// into config.json.
///
/// <para>The file is 0600 and a literal value still works, so this is not about the file being
/// readable — it is about what a config gets used FOR. People paste config.json into issues, commit
/// it to dotfiles, and screen-share it; a placeholder is the version that survives all three.</para>
///
/// <para>WHERE IT APPLIES IS DELIBERATELY NARROW: header values and environment values, and nothing
/// else. Not <c>command</c> — interpolating into an argv that spawns a process turns a config file
/// into a code-execution seam, and "my API key ended up as an argument" is a much worse failure than
/// "I had to type the command out".</para>
///
/// <para>A MISSING VALUE IS EMPTY, NOT AN ERROR. This runs inside the loader, whose MCP block is
/// non-fatal by design: an unset variable must not take a config down and with it every provider.
/// The caller collects a warning instead, so the user is told without being stopped.</para>
/// </summary>
public static class ConfigVariable
{
    private static readonly Regex EnvPattern = new(@"\{env:([^}]+)\}", RegexOptions.Compiled);
    private static readonly Regex FilePattern = new(@"\{file:([^}]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Expands every placeholder in <paramref name="text"/>, appending a line to
    /// <paramref name="warnings"/> for each one that could not be resolved.
    /// </summary>
    public static string Substitute(string text, IList<string> warnings,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (string.IsNullOrEmpty(text)) return text;

        text = EnvPattern.Replace(text, m =>
        {
            var name = m.Groups[1].Value.Trim();
            var value = environment is not null
                ? environment.GetValueOrDefault(name)
                : Environment.GetEnvironmentVariable(name);

            if (string.IsNullOrEmpty(value))
            {
                warnings.Add($"{m.Value} is not set; substituted an empty value.");
                return "";
            }
            return value;
        });

        text = FilePattern.Replace(text, m =>
        {
            var path = Expand(m.Groups[1].Value.Trim());
            try
            {
                // TRIMMED. A key file written by an editor ends with a newline, and a trailing \n in
                // an Authorization header is the classic silent 401 — the value looks right in every
                // log and is rejected by every server.
                return File.ReadAllText(path).Trim();
            }
            catch (Exception ex)
            {
                warnings.Add($"{m.Value} could not be read ({ex.GetType().Name}); substituted an empty value.");
                return "";
            }
        });

        return text;
    }

    /// <summary>Expands a leading <c>~</c>, which is how people write paths and not something
    /// File.ReadAllText understands.
    ///
    /// <para>PUBLIC, not private: <c>pluginPaths</c> entries are written the same way and need the
    /// same expansion both in Core's own config-time collision check and in an embedding
    /// application's own plugin discovery (PLUGINS.md, "Discovery is the application's") — a second
    /// copy of this in every consumer would drift the moment someone changes how <c>~</c> is written.</para>
    /// </summary>
    public static string Expand(string path)
    {
        if (path.Length > 0 && path[0] == '~')
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path.TrimStart('~').TrimStart('/', '\\'));
        }
        return path;
    }

    /// <summary>Expands every value of a map, leaving the keys alone — a header NAME is not a place
    /// anyone puts a secret, and substituting there would only create ways to build a malformed
    /// header.</summary>
    /// <param name="comparer">
    /// The key comparer to rebuild with. Passed in rather than guessed: HTTP header names are
    /// case-insensitive but Unix environment names are NOT, and collapsing the two here would quietly
    /// merge <c>PATH</c> and <c>Path</c> into one variable.
    /// </param>
    /// <param name="map">Where ${VARS} resolve from — normally the environment.</param>
    /// <param name="warnings">Unresolved names are appended here rather than throwing.</param>
    /// <param name="context">Which config key is being expanded, for the warning text.</param>
    public static IReadOnlyDictionary<string, string>? SubstituteValues(
        IReadOnlyDictionary<string, string>? map, IList<string> warnings, string context,
        StringComparer? comparer = null)
    {
        if (map is null || map.Count == 0) return map;

        var result = new Dictionary<string, string>(comparer ?? StringComparer.Ordinal);
        foreach (var (key, value) in map)
        {
            var before = warnings.Count;
            result[key] = Substitute(value, warnings);

            // Name the SETTING each complaint came from. "{env:FOO} is not set" alone leaves the user
            // hunting through a config for which server wanted it.
            for (var i = before; i < warnings.Count; i++)
                warnings[i] = $"{context}.{key}: {warnings[i]}";
        }
        return result;
    }
}
