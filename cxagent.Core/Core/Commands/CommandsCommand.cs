using CxAgent.Core.Skills;

namespace CxAgent.Core.Commands;

/// <summary>What <c>/commands</c> renders from.</summary>
/// <param name="Catalog">One fresh scan of every command file and skill.</param>
/// <param name="IsBuiltIn">Whether the registry already has this <c>/name</c> — a built-in or a
/// plugin's command, which wins over any file.</param>
/// <param name="FolderTrusted">Whether shell lines and file includes can run without asking.</param>
public sealed record CommandsView(InvocableCatalogResult Catalog, Func<string, bool> IsBuiltIn, bool FolderTrusted);

/// <summary>
/// <c>/commands</c>: what can be typed, and every file that did not make it, with why.
///
/// <para>THE FAILURES ARE THE POINT, as with <c>/skills</c>. A command file that lost its name to a
/// nearer one, to a built-in, or to broken frontmatter is otherwise a file that silently does
/// nothing — saved, typed, and answered with "Unknown command" by an app that knew why.</para>
/// </summary>
public static class CommandsCommand
{
    public static string Render(CommandsView view, string? argument)
    {
        if (!string.IsNullOrWhiteSpace(argument)) return Detail(view, argument.Trim().TrimStart('/'));

        var lines = new List<string>();
        var typeable = view.Catalog.Entries.Where(e => e.Typeable && !view.IsBuiltIn("/" + e.Name)).ToList();

        if (typeable.Count == 0)
        {
            lines.Add("## Custom commands");
            lines.Add("none found. Add one at `.cxagent/commands/<name>.md`.");
        }
        else
        {
            // A HEADING AND INDENTED ROWS, NOT A TABLE — the shape /skills and /agents use, for the
            // same reason: a description runs to a sentence, and a table cell cramps it.
            lines.Add($"## Custom commands · {typeable.Count}");
            lines.Add("");
            foreach (var entry in typeable)
            {
                lines.Add($"- **/{Md.Escape(entry.Name)}**"
                    + (entry.ArgumentHint is { Length: > 0 } hint ? $" {Md.Escape(hint)}" : "")
                    + $" · {Md.Escape(Source(entry))}");
                lines.Add($"  {Md.Escape(entry.Description)}");

                // NAMED, NOT HIDDEN. A file copied from Claude Code carries allowed-tools, a grant
                // this app does not honour; saying so here is how the user learns that a command
                // which ran silently there will ask here.
                if (entry.IgnoredFields.Count > 0)
                    lines.Add($"  ignored: {string.Join(", ", entry.IgnoredFields)}");
            }
        }

        var builtIn = view.Catalog.Entries.Where(e => e.Typeable && view.IsBuiltIn("/" + e.Name)).ToList();
        if (view.Catalog.Shadowed.Count + builtIn.Count > 0)
        {
            lines.Add("");
            lines.Add($"## Shadowed · {view.Catalog.Shadowed.Count + builtIn.Count}");
            lines.Add("");
            foreach (var entry in builtIn)
            {
                lines.Add($"- `{entry.Path}`");
                lines.Add($"  shadowed by built-in /{Md.Escape(entry.Name)}");
            }
            foreach (var shadowed in view.Catalog.Shadowed)
            {
                lines.Add($"- `{shadowed.Loser.Path}`");
                lines.Add($"  shadowed by {Md.Escape(shadowed.Winner.Path)}");
            }
        }

        if (view.Catalog.Problems.Count > 0)
        {
            lines.Add("");
            lines.Add($"## Skipped · {view.Catalog.Problems.Count}");
            lines.Add("");
            foreach (var problem in view.Catalog.Problems)
            {
                lines.Add($"- `{problem.Path}`");
                lines.Add($"  {Md.Escape(problem.Reason)}");
            }
        }

        // SAID BEFORE IT HAPPENS. In a folder nobody trusted, every shell line and every @ file in a
        // command asks first, as the model's own calls do — better read here than met as a prompt.
        if (!view.FolderTrusted)
        {
            lines.Add("");
            lines.Add("This folder is not trusted · shell lines and file includes in a command will ask first.");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// One entry as read — what <c>/name</c> will actually send before its arguments go in. The way to
    /// check a file from a cloned repo before running it.
    /// </summary>
    private static string Detail(CommandsView view, string name)
    {
        var entry = view.Catalog.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return $"No command named '{Md.Escape(name)}'. /commands lists them.";

        var lines = new List<string>
        {
            $"## /{Md.Escape(entry.Name)} · {Md.Escape(Source(entry))}",
            $"`{entry.Path}`",
            "",
            Md.Escape(entry.Description),
        };

        // SAID FIRST, because it changes what the rest means: this is the file, and typing its name
        // runs something else.
        if (view.IsBuiltIn("/" + entry.Name))
            lines.Insert(2, $"shadowed by built-in /{Md.Escape(entry.Name)} — typing /{Md.Escape(entry.Name)} runs that, not this file.");
        if (entry.IgnoredFields.Count > 0) lines.Add($"ignored: {string.Join(", ", entry.IgnoredFields)}");
        lines.Add("");
        lines.Add(Md.Fence(entry.Body, "markdown"));
        return string.Join("\n", lines);
    }

    /// <summary>Where it came from, short: the folder's last two parts, the subfolder, and the kind.</summary>
    private static string Source(Invocable entry)
    {
        var tail = string.Join('/', entry.Folder.Replace('\\', '/').TrimEnd('/').Split('/').TakeLast(2));
        var label = entry.Label is { } l ? $" · {l}" : "";
        var kind = entry.Kind == InvocableKind.Skill ? " · skill" : "";
        return tail + label + kind;
    }
}
