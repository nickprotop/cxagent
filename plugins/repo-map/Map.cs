using System.Text;

namespace CxAgent.Plugins.RepoMap;

/// <summary>One map to make.</summary>
/// <param name="Root">The folder, absolute.</param>
/// <param name="Shown">How the folder is named in the map's header — what the caller asked for.</param>
/// <param name="Focus">Paths, folders or symbol names to centre on; empty for the whole folder.</param>
/// <param name="MaxTokens">The budget, already clamped.</param>
public sealed record MapRequest(string Root, string Shown, IReadOnlyList<string> Focus, int MaxTokens = Map.DefaultTokens)
{
    /// <summary>The parsers to read with; null opens this map's own. A caller that passes one owns it.</summary>
    public Parsing.Engine? Engine { get; init; }
}

/// <summary>What one map found: which files were listed, how many were read, their ranking, and what was read as text and why.</summary>
public sealed record Survey(FileList List, int Read, Ranking Ranking, IReadOnlyList<string> Notes);

/// <summary>
/// Lists, outlines, ranks and renders — the whole tool, apart from reading its arguments.
/// </summary>
public static class Map
{
    public const int DefaultTokens = 2048;
    public const int MinTokens = 256;
    public const int MaxTokens = 16384;

    /// <summary>
    /// Most declarations one file shows. Past this a file's least-used members are left out and
    /// counted: a file that declares two hundred things would otherwise spend the whole budget.
    /// </summary>
    private const int MaxSymbolsPerFile = 30;

    public static async Task<string> Build(MapRequest request, Action<int> onProcess, CancellationToken ct)
    {
        var list = FileSource.List(request.Root, onProcess);

        var owned = request.Engine is null ? Parsing.Engine.Open() : null;
        var engine = request.Engine ?? owned!;
        var outlines = new List<FileOutline>(list.Files.Count);
        IReadOnlyList<string> notes;
        try
        {
            foreach (var file in list.Files)
            {
                ct.ThrowIfCancellationRequested();
                var language = Language.For(file)!;
                string text;
                try { text = await File.ReadAllTextAsync(file, ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                var relative = Path.GetRelativePath(request.Root, file).Replace('\\', '/');
                outlines.Add(Parsing.FileReader.Outline(relative, text, language, engine));
            }
            // Copied before the engine is disposed with this map.
            notes = engine.Notes.ToList();
        }
        finally
        {
            owned?.Dispose();
        }

        var ranking = Ranker.Rank(outlines, request.Focus);
        return Render(request, new Survey(list, outlines.Count, ranking, notes));
    }

    private static string Render(MapRequest request, Survey survey)
    {
        var (list, read, ranking) = (survey.List, survey.Read, survey.Ranking);
        // A TOKEN IS ABOUT FOUR CHARACTERS OF CODE. An estimate, and the budget is one: the point
        // is a map that fits beside the work, not an exact count.
        var budget = request.MaxTokens * 4;
        var output = new StringBuilder();

        if (ranking.Files.Count == 0)
            return read == 0
                ? $"No source files under {request.Shown} in a language repo_map reads."
                : $"{read} source files under {request.Shown}, and no declarations found in any of them.";

        output.Append($"{read} source files under {request.Shown}, {ranking.Files.Count} declaring something. ");
        // ONLY WHAT MATCHED IS CLAIMED AS THE CENTRE. A focus that matched nothing left the whole
        // folder ranked as usual, and the header must not say otherwise.
        var centre = request.Focus.Select(f => f.Trim())
            .Where(f => f is not ("" or "." or "./") && !ranking.Unmatched.Contains(f)).ToList();
        output.Append(centre.Count > 0
            ? $"Centred on {string.Join(", ", centre)}: what it uses and what uses it come first."
            : "Most-used first.");
        output.Append(" Each line is `line: declaration`.\n");
        if (ranking.Unmatched.Count > 0)
            output.Append($"No file, folder or symbol matched: {string.Join(", ", ranking.Unmatched)}.\n");
        // WHAT WAS READ AS TEXT AND WHY, once each. A map read without its parser is still a map, but
        // a reader comparing two of them deserves to know why one is thinner.
        foreach (var note in survey.Notes)
            output.Append(note.TrimEnd('.')).Append(".\n");
        if (list.Truncated)
            output.Append($"This folder has more than {FileSource.MaxFiles} source files; only the first {FileSource.MaxFiles} by path were read. Map a subfolder for the rest.\n");
        output.Append('\n');

        // FENCED. Tool output is rendered as markdown, which would strip the indentation that shows
        // a member inside its type and swallow `<T>` as a tag; in a fence the outline reads as written.
        output.Append("```\n");
        var shown = 0;
        foreach (var file in ranking.Files)
        {
            var block = FileBlock(file, ranking.UsedBy);
            // THE FIRST FILE IS ALWAYS SHOWN, however small the budget: a map with a header and no
            // files answers nothing.
            if (shown > 0 && output.Length + block.Length > budget) break;
            output.Append(block);
            shown++;
        }
        output.Append("```\n");

        var rest = ranking.Files.Skip(shown).ToList();
        if (rest.Count == 0) return output.ToString();

        // WHAT DID NOT FIT IS STILL NAMED, while the budget lasts: a path costs a few tokens, and
        // knowing a file exists is most of what deciding to look at it needs.
        var names = new StringBuilder();
        var listed = 0;
        foreach (var file in rest)
        {
            var line = $"  {file.Path}\n";
            if (output.Length + names.Length + line.Length > budget + 200) break;
            names.Append(line);
            listed++;
        }

        output.Append('\n');
        if (listed > 0)
        {
            output.Append("Next by rank, declarations not shown:\n");
            output.Append(names);
        }
        var unnamed = rest.Count - listed;
        output.Append(unnamed > 0
            ? $"…and {unnamed} more files. Pass focus, map a subfolder, or raise max_tokens (now {request.MaxTokens}) to see them.\n"
            : $"Raise max_tokens (now {request.MaxTokens}) to see their declarations.\n");

        return output.ToString();
    }

    private static string FileBlock(FileOutline file, IReadOnlyDictionary<string, int> usedBy)
    {
        var symbols = file.Symbols.Where(s => s.Kind != SymbolKind.Namespace).ToList();
        var omitted = 0;
        if (symbols.Count > MaxSymbolsPerFile)
        {
            // TYPES ALWAYS, THEN WHAT ANOTHER FILE CAN CALL, MOST USED FIRST, shown back in source
            // order so the outline still reads top to bottom. A private member is the file's own
            // business, so it is the first thing a full file stops showing.
            var kept = symbols
                .OrderBy(s => s.Kind == SymbolKind.Type ? 0 : 1)
                .ThenBy(s => s.Signature.StartsWith("private ", StringComparison.Ordinal) ? 1 : 0)
                .ThenByDescending(s => usedBy.GetValueOrDefault(s.Name))
                .ThenBy(s => s.Line)
                .Take(MaxSymbolsPerFile)
                .ToHashSet();
            omitted = symbols.Count - kept.Count;
            symbols = symbols.Where(kept.Contains).ToList();
        }

        var block = new StringBuilder();
        block.Append(file.Path).Append('\n');
        foreach (var symbol in symbols)
            block.Append(' ', 2 + 2 * symbol.Depth).Append(symbol.Line).Append(": ").Append(symbol.Signature).Append('\n');
        if (omitted > 0)
            block.Append("  … ").Append(omitted).Append(" less-used declarations not shown\n");
        return block.ToString();
    }
}
