using CxAgent.Core.Skills;

namespace CxAgent.Core.Commands;

/// <summary>
/// <c>/skills</c> — what was found, where it came from, and what was REFUSED.
///
/// <para>THE REFUSALS ARE WHY THIS EXISTS. A SKILL.md with broken frontmatter is invisible to the
/// model and nothing else in the app would ever mention it: the user wrote a file, nothing happened,
/// and there is no error anywhere. That is the <c>/mcp</c> lesson — a server that silently never
/// appears is indistinguishable from one that was never configured.</para>
///
/// <para>IT LISTS, IT DOES NOT LOAD. Typing <c>/name</c> runs a skill on purpose; this command only
/// shows what is there, and loading something from a listing would spend context on a document nobody
/// asked for.</para>
///
/// <para>PER NAME, FROM EVERY FOLDER — see <see cref="InvocableCatalog"/> — so each row says where it
/// came from, and a skill a nearer file took is listed as shadowed rather than vanishing.</para>
///
/// <para>AND IT IS NOT A REFRESH. Discovery runs per turn, so an edited skill is already live on the
/// next one. A command that looked like the way to apply changes would teach a ritual nobody
/// needs.</para>
/// </summary>
public sealed class SkillsCommand(Func<InvocableCatalogResult> catalog, bool skillToolOffered = true)
{
    public string Render()
    {
        var found = catalog();
        var loadable = found.Entries.Where(e => e.Kind == InvocableKind.Skill && e.ModelInvocable).ToList();
        var youOnly = found.Entries.Where(e => e.Kind == InvocableKind.Skill && !e.ModelInvocable).ToList();
        var shadowed = found.Shadowed.Where(s => s.Loser.Kind == InvocableKind.Skill).ToList();
        var lines = new List<string>();

        if (loadable.Count == 0 && youOnly.Count == 0)
        {
            // NO SKILL IS NOT AN ERROR, and must not be reported as one. It is what a first attempt at
            // writing a skill looks like — and naming a folder as the source when nothing in it parsed
            // would be a lie about the one thing the user is debugging.
            lines.Add(found.Problems.Count > 0 ? "## No skills loaded" : "## Skills");
            lines.Add(found.Problems.Count > 0
                ? "every candidate file was skipped — see below"
                : "none found. Add one at `.cxagent/skills/<name>/SKILL.md` "
                  + "with a name and a description.");
        }

        if (loadable.Count > 0)
        {
            // A HEADING AND INDENTED ROWS, NOT A TABLE. A skill's description runs to a sentence or
            // more — the same reason /agents keeps its listing this shape rather than a table's
            // cramped cell.
            lines.Add($"## Skills · {loadable.Count}");
            lines.Add("");
            foreach (var skill in loadable) Row(lines, skill);
        }

        // THE SKILLS THE MODEL NEVER SEES. disable-model-invocation keeps a skill out of the model's
        // list on purpose — /deploy is something a person decides to run — so they are listed apart,
        // where nobody reads them as a menu the model chooses from.
        if (youOnly.Count > 0)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add($"## You only · {youOnly.Count}");
            lines.Add("");
            foreach (var skill in youOnly) Row(lines, skill);
        }

        // THE LISTING IS TRUE AND UNREACHABLE. Discovery does not consult the selection — these
        // files are on disk and parsed — but with the skill tool withheld the model has no way to
        // load any of them, and a bare list reads as a menu. Said once, after the rows, rather than
        // marking each: the restriction is the agent's, not the skill's.
        //
        // AFTER EVERY BRANCH, not inside the populated one. The empty branch ends by telling the user
        // to go WRITE a skill at .cxagent/skills/<name> — the worst advice available to someone whose
        // agent could not load it either way.
        if (!skillToolOffered)
        {
            lines.Add("");
            lines.Add("The skill tool is not offered to this agent · nothing here can be loaded.");
        }

        if (shadowed.Count > 0)
        {
            lines.Add("");
            lines.Add($"## Shadowed · {shadowed.Count}");
            lines.Add("");
            foreach (var entry in shadowed)
            {
                lines.Add($"- `{entry.Loser.Path}`");
                lines.Add($"  shadowed by {Md.Escape(entry.Winner.Path)}");
            }
        }

        if (found.Problems.Count > 0)
        {
            lines.Add("");
            lines.Add($"## Skipped · {found.Problems.Count}");
            lines.Add("");

            // EVERY CANDIDATE FOLDER, INCLUDING THE ONES THAT LOST. A broken file in a folder whose
            // other names were shadowed is exactly the one nothing else explains.
            foreach (var problem in found.Problems)
            {
                lines.Add($"- `{problem.Path}`");
                lines.Add($"  {Md.Escape(problem.Reason)}");
            }
        }

        return string.Join("\n", lines);
    }

    private static void Row(List<string> lines, Invocable skill)
    {
        // A NAME THE COMPOSER CANNOT TAKE AS ONE WORD still loads for the model; it just cannot be
        // typed, and saying so here is how the user learns why /dotnet.testing answers "unknown".
        var typeable = skill.UserInvocable && !InvocableCatalog.IsTypeableName(skill.Name) ? " · not typeable" : "";
        lines.Add($"- **{Md.Escape(skill.Name)}** · `{skill.Folder}`{typeable}");
        // THE DESCRIPTION VERBATIM. It is what the model matches on, so a user debugging "why was
        // this never loaded?" needs to read exactly what the model read.
        lines.Add($"  {Md.Escape(skill.Description)}");
        if (skill.IgnoredFields.Count > 0) lines.Add($"  ignored: {string.Join(", ", skill.IgnoredFields)}");
    }
}
