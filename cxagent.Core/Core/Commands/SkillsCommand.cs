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
/// <para>IT LISTS, IT DOES NOT LOAD. The model decides what a task needs; a command that loaded one
/// into the parent's context would be the user guessing on the model's behalf, and would spend
/// context on a document nothing had asked for.</para>
///
/// <para>AND IT IS NOT A REFRESH. Discovery runs per turn, so an edited skill is already live on the
/// next one. A command that looked like the way to apply changes would teach a ritual nobody
/// needs.</para>
/// </summary>
public sealed class SkillsCommand(Func<SkillCatalogResult> catalog, bool skillToolOffered = true)
{
    /// <summary>
    /// The listing, as text.
    ///
    /// <para>RENDERS RATHER THAN PRINTS. It took a transcript writer and wrote to it, which made a
    /// listing that is pure text depend on a front end having one. The caller says the result — the
    /// session does, through the observer everything else already speaks through.</para>
    /// </summary>
    public string Render()
    {
        var found = catalog();
        var lines = new List<string>();

        if (found.Skills.Count == 0)
        {
            // NO WINNER IS NOT AN ERROR, and must not be reported as one. It is what a first attempt
            // at writing a skill looks like — and saying "/repo/.cxagent/skills is in use" when
            // nothing in it parsed would be a lie about the one thing the user is debugging.
            lines.Add(found.Problems.Count > 0 ? "## No skills loaded" : "## Skills");
            lines.Add(found.Problems.Count > 0
                ? "every candidate file was skipped — see below"
                : "none found. Add one at `.cxagent/skills/<name>/SKILL.md` "
                  + "with a name and a description.");
        }
        else
        {
            // A HEADING AND INDENTED ROWS, NOT A TABLE. A skill's description runs to a sentence or
            // more — the same reason /agents keeps its listing this shape rather than a table's
            // cramped cell.
            lines.Add($"## Skills · {found.Skills.Count} from {Md.Escape(found.SourceDirectory ?? "")}");
            lines.Add("");

            foreach (var skill in found.Skills)
            {
                lines.Add($"- **{Md.Escape(skill.Name)}**");
                // THE DESCRIPTION VERBATIM. It is what the model matches on, so a user debugging
                // "why was this never loaded?" needs to read exactly what the model read.
                lines.Add($"  {Md.Escape(skill.Description)}");
            }
        }

        // THE LISTING IS TRUE AND UNREACHABLE. Discovery does not consult the selection — these
        // files are on disk and parsed — but with the skill tool withheld the model has no way to
        // load any of them, and a bare list reads as a menu. Said once, after the rows, rather than
        // marking each: the restriction is the agent's, not the skill's.
        //
        // AFTER BOTH BRANCHES, not inside the populated one. The empty branch ends by telling the
        // user to go WRITE a skill at .cxagent/skills/<name> — the worst advice available to someone
        // whose agent could not load it either way, and the case a first version missed because the
        // test directory happened to be empty.
        if (!skillToolOffered)
        {
            lines.Add("");
            lines.Add("The skill tool is not offered to this agent · nothing here can be loaded.");
        }

        if (found.Problems.Count > 0)
        {
            lines.Add("");
            lines.Add($"## Skipped · {found.Problems.Count}");
            lines.Add("");

            // EVERY CANDIDATE DIRECTORY, INCLUDING THE ONES THAT LOST. Shadowing decides which
            // directory SUPPLIES skills, not which may report a file the user wrote and expected to
            // work — a broken file in a shadowed directory is exactly the one nothing else explains.
            foreach (var problem in found.Problems)
            {
                lines.Add($"- `{problem.Path}`");
                lines.Add($"  {Md.Escape(problem.Reason)}");
            }
        }

        return string.Join("\n", lines);
    }
}
