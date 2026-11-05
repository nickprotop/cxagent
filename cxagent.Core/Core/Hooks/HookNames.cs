using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CxAgent.Core.Hooks;

/// <summary>
/// Tool names as hooks see them: cxagent's own, and the contract's other spelling of each.
///
/// <para>BOTH SPELLINGS MATCH, ONE IS DOCUMENTED. Hook scripts shared for the contract match on its tool
/// names (<c>Bash</c>, <c>Edit|Write</c>) and read its argument names (<c>file_path</c>). Accepting those
/// beside cxagent's lets such a script work unchanged; the docs and F8 speak only cxagent's names, so
/// nobody has to learn two vocabularies to write a new hook.</para>
/// </summary>
public static class HookNames
{
    /// <summary>cxagent's tool name → the contract's names for the same tool.</summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
    {
        ["run_shell"] = ["Bash"],
        ["read_file"] = ["Read"],
        ["write_file"] = ["Write"],
        ["replace_in_file"] = ["Edit", "MultiEdit"],
        ["glob"] = ["Glob"],
        ["grep"] = ["Grep"],
        ["web_fetch"] = ["WebFetch"],
        ["web_search"] = ["WebSearch"],
        ["todowrite"] = ["TodoWrite"],
    };

    private static readonly ConcurrentDictionary<string, Regex?> Patterns = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a hook's matcher selects this tool. Null, empty and <c>*</c> select every tool; anything
    /// else is a pattern that must match the WHOLE name — <c>Bash</c> must not match <c>bash_helper</c>.
    /// </summary>
    public static bool Matches(string? matcher, string toolName)
    {
        if (string.IsNullOrEmpty(matcher) || matcher == "*") return true;
        if (Patterns.GetOrAdd(matcher, Compile) is not { } regex) return false;
        if (regex.IsMatch(toolName)) return true;
        return Aliases.TryGetValue(toolName, out var other) && other.Any(regex.IsMatch);
    }

    private static Regex? Compile(string pattern)
    {
        try { return new Regex($"^(?:{pattern})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// A tool's arguments as a hook reads them: cxagent's, plus the contract's names beside them.
    /// </summary>
    public static JsonObject WithAliases(string toolName, JsonObject input)
    {
        var o = (JsonObject)input.DeepClone();
        if (o["path"] is { } path && !o.ContainsKey("file_path")) o["file_path"] = path.DeepClone();

        if (toolName == "replace_in_file")
        {
            if (o["edits"] is JsonArray edits)
            {
                // ONE EDIT READS AS THE CONTRACT'S SINGLE EDIT, several as its list — inside each item, so the
                // list keeps cxagent's own fields for anything that reads those.
                foreach (var e in edits.OfType<JsonObject>())
                {
                    if (e["pattern"] is { } p) e["old_string"] = p.DeepClone();
                    if (e["replacement"] is { } r) e["new_string"] = r.DeepClone();
                }
                if (edits.Count == 1 && edits[0] is JsonObject only)
                {
                    if (only["pattern"] is { } p) o["old_string"] = p.DeepClone();
                    if (only["replacement"] is { } r) o["new_string"] = r.DeepClone();
                }
            }
            else
            {
                if (o["pattern"] is { } p) o["old_string"] = p.DeepClone();
                if (o["replacement"] is { } r) o["new_string"] = r.DeepClone();
            }
        }
        return o;
    }

    /// <summary>
    /// A hook's rewritten arguments in cxagent's names, whichever spelling the hook used.
    ///
    /// <para>CXAGENT'S NAME WINS when both arrive. A hook that edits the input it was given sends both back,
    /// and only the one it meant to change can be told from the copy; the real name is the one cxagent's own
    /// hooks write, and the alias is the one most likely left untouched.</para>
    /// </summary>
    public static JsonObject ToCxagentInput(string toolName, JsonObject updated)
    {
        var o = (JsonObject)updated.DeepClone();
        Rename(o, "file_path", "path");

        if (toolName == "replace_in_file")
        {
            if (o["edits"] is JsonArray edits)
            {
                foreach (var e in edits.OfType<JsonObject>())
                {
                    Rename(e, "old_string", "pattern");
                    Rename(e, "new_string", "replacement");
                }
                o.Remove("old_string");
                o.Remove("new_string");
            }
            else if (o.ContainsKey("old_string") || o.ContainsKey("pattern"))
            {
                var edit = new JsonObject
                {
                    ["pattern"] = (o["pattern"] ?? o["old_string"])?.DeepClone(),
                    ["replacement"] = (o["replacement"] ?? o["new_string"])?.DeepClone(),
                };
                foreach (var k in new[] { "pattern", "replacement", "old_string", "new_string", "replace_all" }) o.Remove(k);
                o["edits"] = new JsonArray(edit);
            }
        }
        return o;
    }

    private static void Rename(JsonObject o, string alias, string name)
    {
        if (!o.ContainsKey(alias)) return;
        var value = o[alias];
        o.Remove(alias);
        if (!o.ContainsKey(name)) o[name] = value;
    }
}
