namespace CxAgent.Core.Colleagues;

/// <summary>One colleague as the library shows it: who it is, what it knows, the facts of its growth, its experience.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Description">Its one line.</param>
/// <param name="Knows">The folders it has worked in, by their own names, most recent first.</param>
/// <param name="Facts">Its version, rounds and model.</param>
/// <param name="Experience">Its core, then each codebase's text, read-only.</param>
public sealed record LibraryEntry(string Name, string Description, IReadOnlyList<string> Knows, LibraryFacts Facts,
    IReadOnlyList<ExperiencePart> Experience);

/// <summary>How far a colleague has grown.</summary>
/// <param name="Version">Its experience version — one per distilling that changed it.</param>
/// <param name="Rounds">Rounds in its past.</param>
/// <param name="Distilled">Rounds distilled so far.</param>
/// <param name="Skipped">Rounds stepped past without being learned from.</param>
/// <param name="Type">The agent type it works as.</param>
/// <param name="Model">The provider entry it runs on, or null for the session's.</param>
public sealed record LibraryFacts(long Version, long Rounds, long Distilled, int Skipped, string Type, string? Model)
{
    /// <summary>What distilling it has cost, every process's calls together.</summary>
    public DistillTotals Spend { get; init; } = new();
}

/// <summary>One part of a colleague's experience.</summary>
/// <param name="Title">"Core", or the codebase's folder name.</param>
/// <param name="Markdown">The text, as distilled.</param>
public sealed record ExperiencePart(string Title, string Markdown);

/// <summary>
/// The library as both front ends show it — the terminal's tab and the browser's page are each a drawing of this list,
/// so neither can show a fact the other leaves out or name a codebase differently.
/// </summary>
public static class LibraryView
{
    /// <summary>Every colleague, most recently worked first, with its experience.</summary>
    public static IReadOnlyList<LibraryEntry> Build(ColleagueStore store) =>
        [.. store.List().Select(r =>
        {
            var line = ColleagueLine.Of(r);
            var experience = store.ReadExperience(r.Name);
            var parts = new List<ExperiencePart>();
            if (experience.Core.Length > 0) parts.Add(new ExperiencePart("Core", experience.Core));
            // EACH CODEBASE BY ITS FOLDER'S NAME, the most recently worked first — a key's hash means nothing to a reader.
            foreach (var w in r.Workplaces.OrderByDescending(w => w.Last))
                if (experience.Codebases.TryGetValue(w.Codebase, out var text) && text.Length > 0)
                    parts.Add(new ExperiencePart(Path.GetFileName(Path.TrimEndingDirectorySeparator(w.Folder)), text));
            foreach (var (key, text) in experience.Codebases)
                if (r.Workplaces.All(w => w.Codebase != key) && text.Length > 0) parts.Add(new ExperiencePart(key, text));
            return new LibraryEntry(r.Name, r.Description, line.Knows,
                new LibraryFacts(r.ExperienceVersion, store.RoundCount(r.Name), r.DistilledThrough, r.Skipped.Count, r.Type, r.Instance)
                {
                    Spend = r.Spend,
                },
                parts);
        })];

    /// <summary>
    /// The facts under a colleague's name, as one plain line — ONE TEXT FOR BOTH FRONT ENDS, so the terminal's tab and the
    /// browser's page cannot word a fact differently or leave one out.
    /// </summary>
    public static string FactsLine(LibraryFacts f)
    {
        var line = $"v{f.Version} · {f.Rounds} round{(f.Rounds == 1 ? "" : "s")}, {f.Distilled} distilled"
                   + (f.Skipped > 0 ? $", {f.Skipped} skipped" : "")
                   + (f.Model is { } m ? $" on {m}" : "");
        if (f.Spend.Calls > 0)
            line += $" · distilling: {f.Spend.Calls} call{(f.Spend.Calls == 1 ? "" : "s")}, "
                    + $"{Helpers.DisplayNumber.Compact(f.Spend.InputTokens + f.Spend.OutputTokens)} tokens";
        return line;
    }
}
