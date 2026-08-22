using CxAgent.Core.Sessions;
using CxAgent.Core.Agents;

namespace CxAgent.Core.Commands;

/// <summary>
/// <c>/agents</c> — the sub-agent types this session can spawn, and what each one is told.
///
/// <para>IT EXISTS BECAUSE THE BRIEFINGS LEFT config.json. While they lived there, reading one meant
/// opening a file the user had written themselves; now the shipped five are code, and there was no
/// way to see them from inside the app at all. That matters when a drive goes wrong: diagnosing a
/// bad run means reading the exact text the child was given — the builder's refusal to start without
/// a plan, the planner's instruction about where to write — and "go and read the source" is not an
/// answer for someone whose agent just did something strange.</para>
///
/// <para>TWO DEPTHS, following <c>/mcp</c>. Bare lists every type with the one line the PARENT reads
/// while choosing; a name prints that type's full briefing, which is what the CHILD reads. The five
/// shipped briefings total about 5,700 characters, so printing them all by default would bury the
/// list they are attached to.</para>
///
/// <para>IT LISTS, IT DOES NOT SPAWN — the <c>/skills</c> rule. Which type fits a task is the
/// model's decision, made against the same catalog this prints; a command that launched one would be
/// the user guessing on its behalf.</para>
///
/// <para>AND IT SAYS WHEN CONFIG IS BEING IGNORED. A user who still has <c>agents.builder.briefing</c>
/// in their file gets a warning at startup, once, and then never again — this is where they look when
/// they wonder why their edit did nothing.</para>
/// </summary>
public sealed class AgentsCommand(AgentTypeCatalog catalog, bool spawnToolOffered = true)
{
    /// <summary>The listing, or one type's briefing, as text. Renders rather than printing: it took
    /// a transcript writer, which made a listing that is pure text depend on a front end having
    /// one. The session says the result.</summary>
    public string Render(string? argument = null)
    {
        var words = (argument ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        // `show <name>` IS THE SPELLED-OUT FORM, matching /mcp. Without a verb the palette could
        // offer the type names but never insert the word in front of them, because there was no word
        // — a row carrying a placeholder completes to the verb, and a bare placeholder has none.
        if (words is [var verb, ..] && verb.Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            return string.Join("\n", words.Length >= 2
                ? Detail(words[1])
                : ["Name a type: `/agents show <name>`."]);
        }

        // A BARE NAME STILL WORKS, for the reason /mcp keeps it: refusing the reading someone
        // naturally reaches for teaches nothing.
        var lines = words.Length == 0 ? List() : Detail(words[0]);
        return string.Join("\n", lines);
    }

    private List<string> List()
    {
        var lines = new List<string>
        {
            $"## Agent types · {catalog.All.Count} available",
            "",
        };

        // "AVAILABLE" IS ABOUT THE CATALOG, NOT THE AGENT. The types are configured and resolvable;
        // with the spawn tool withheld nothing can reach them, and a list under that heading reads
        // as a set of things the agent will do if asked. /mode already says this for the mode axis —
        // the same fact reaches a user here who never typed /mode.
        if (!spawnToolOffered)
        {
            lines.Add("This agent cannot spawn · the agent tool is not offered, so none of these "
                    + "can be reached.");
            lines.Add("");
        }

        foreach (var type in catalog.All)
        {
            // WHERE IT RUNS, because a type bound to another provider is the fact users most often
            // forget they configured — and it is the one that explains a surprising bill or a
            // surprising answer. Inherited is stated rather than blank: "the session's" is a real
            // answer, and an empty column reads as missing information.
            var runsOn = type.Routing.InstanceName is { Length: > 0 } instance
                ? instance
                : "session's model";

            var turns = type.MaxTurns switch
            {
                null => "inherits the turn cap",
                0 => "no turn cap",
                var n => $"{n} turns",
            };

            // SURFACED HERE OR NOWHERE. WritesAPlanFile decides that the spawner names a path and
            // then contradicts the answer if no file appears — behaviour a user would otherwise meet
            // only as a warning in a transcript.
            var writes = type.WritesAPlanFile ? " · writes a plan file" : "";

            lines.Add($"- **{Md.Escape(type.Name)}** · {runsOn} · {turns}{writes}");

            // FIRST SENTENCE ONLY. The shipped descriptions run to several hundred characters —
            // planner's is eight lines on an 80-column terminal — because they are written for a
            // model choosing between types, not for a human scanning a list. Printing them whole
            // buries the names they belong to, which is the same failure as printing the briefings
            // here. The full text is one keystroke away under /agents <name>.
            var described = string.IsNullOrWhiteSpace(type.Description)
                ? "no description — the catalog says \"runs where you do, no special instructions\""
                : FirstSentence(type.Description);
            lines.Add($"  {Md.Escape(described)}");
            lines.Add("");
        }

        lines.Add("`/agents <name>` for the full briefing that type is given.");
        return lines;
    }

    /// <summary>
    /// The opening sentence, for the list.
    ///
    /// <para>Cut at ". " rather than the first period, so "file_path:line_number." and abbreviations
    /// do not truncate a line mid-thought. A description with no sentence break is clipped on width
    /// instead — better a visible ellipsis than a row that wraps four times.</para>
    /// </summary>
    private static string FirstSentence(string text)
    {
        var stop = text.IndexOf(". ", StringComparison.Ordinal);
        var first = stop > 0 ? text[..(stop + 1)] : text;
        return first.Length <= 150 ? first : first[..149].TrimEnd() + "…";
    }

    private List<string> Detail(string name)
    {
        if (catalog.Resolve(name) is not { } type || !string.Equals(type.Name, name, StringComparison.Ordinal))
        {
            // A BLANK NAME RESOLVES TO `general`, which would make "/agents typo" print the default
            // type's briefing and look like an answer. Names are compared exactly for that reason.
            return
            [
                $"No agent type '{Md.Escape(name)}'",
                $"available: {catalog.Names}",
            ];
        }

        var lines = new List<string> { $"## {Md.Escape(type.Name)}", "" };

        if (!string.IsNullOrWhiteSpace(type.Description))
        {
            lines.Add("When to choose it — what the parent reads:");
            lines.Add(Md.Escape(type.Description));
            lines.Add("");
        }

        if (string.IsNullOrWhiteSpace(type.Briefing))
        {
            // `general` has none, deliberately, and saying so is better than printing an empty block:
            // "no briefing" is what makes a bare spawn ordinary rather than special.
            lines.Add("No briefing. A child of this type runs with no special instructions beyond "
                    + "the session's own.");
        }
        else
        {
            lines.Add("Its briefing — what the child reads, in full:");
            lines.Add("");
            lines.Add(Md.Escape(type.Briefing));
        }

        lines.Add("");
        var runsOn = type.Routing.InstanceName is { Length: > 0 } instance
            ? instance
            : "the session's model";
        lines.Add($"Runs on {runsOn}.");

        if (BuiltinAgentTypes.IsBuiltin(type.Name))
            lines.Add("Built in: this text ships with cxagent. A 'briefing' or "
                    + $"'description' under agents.{type.Name} in config.json is ignored — rename the "
                    + "type to write your own.");

        return lines;
    }
}
