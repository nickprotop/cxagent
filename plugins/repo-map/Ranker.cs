using System.Text.RegularExpressions;

namespace CxAgent.Plugins.RepoMap;

/// <summary>One file as the ranker sees it: what it declares, and every name it mentions.</summary>
/// <param name="Path">Relative to the mapped folder, with forward slashes.</param>
/// <param name="Symbols">Its declarations, in source order.</param>
/// <param name="Mentions">Every identifier in its code, comments and strings excluded, with how often it appears.</param>
public sealed record FileOutline(string Path, IReadOnlyList<Symbol> Symbols, IReadOnlyDictionary<string, int> Mentions);

/// <summary>The files in rank order, best first, and how many other files mention each symbol's name.</summary>
/// <param name="Files">Every file that declares something, best first.</param>
/// <param name="UsedBy">For a declared name, how many files other than its own mention it.</param>
/// <param name="Unmatched">The focus entries that named no file, folder or symbol.</param>
public sealed record Ranking(IReadOnlyList<FileOutline> Files, IReadOnlyDictionary<string, int> UsedBy,
    IReadOnlyList<string> Unmatched);

/// <summary>
/// Orders files by how much the rest of the code depends on them.
///
/// <para>PAGERANK OVER "MENTIONS A NAME DECLARED THERE". A file that mentions a name another file
/// declares is taken to use that file. A file many others use ranks high, and a file used by a
/// file many others use ranks higher still — which is the order a newcomer wants: the types
/// everything is built from first, the leaves last. Counting mentions alone would rank a file of
/// constants that every test imports above the engine the tests are about.</para>
///
/// <para>BY NAME, NOT BY RESOLVED REFERENCE. Without a compiler, <c>Run</c> in one file cannot be
/// tied to the <c>Run</c> declared in another, so a name declared in several files spreads its
/// weight across all of them, and names too common to mean anything are not counted at all. It is
/// wrong in detail and right in aggregate, which is all a map needs.</para>
///
/// <para>FOCUS IS WHERE THE WALK RESTARTS. With a focus, the random surfer of PageRank jumps back
/// to the focused files instead of anywhere at all, so what they use ranks first; the files that
/// use them get a share of the restarts too, because "who calls this" is half of what someone
/// focusing on a file wants to know.</para>
/// </summary>
public static class Ranker
{
    private const double Damping = 0.85;
    private const int Iterations = 30;

    /// <summary>A name declared in more files than this is more likely a convention than one thing —
    /// every handler's <c>Handle</c>, every example's <c>ConsoleSink</c> — and weighs a tenth.</summary>
    private const int ManyDeclarers = 5;

    private static readonly HashSet<string> TooCommon = new(StringComparer.Ordinal)
    {
        "Main", "main", "ToString", "Equals", "GetHashCode", "Dispose", "DisposeAsync", "init", "__init__",
        "constructor", "get", "set", "run", "Run", "test", "Test", "setUp", "tearDown", "new", "New",
        "String", "Error", "Value", "Name", "Type", "Data", "Item", "Result", "Get", "Set", "Add", "Create",
        "Execute", "Start", "Stop", "Load", "Close", "Open", "Reset", "Update", "Apply", "Build", "Parse",
        "render", "default", "Default", "self", "this", "value", "name", "data", "type", "args", "options",
    };

    private static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z0-9_]{2,}", RegexOptions.Compiled);

    /// <summary>Every identifier a file's code mentions, counted — its blanked text, so a comment's prose is not counted.</summary>
    public static IReadOnlyDictionary<string, int> MentionsIn(IEnumerable<string> code)
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in code)
            foreach (Match m in Identifier.Matches(line))
                names[m.Value] = names.GetValueOrDefault(m.Value) + 1;
        return names;
    }

    public static Ranking Rank(IReadOnlyList<FileOutline> files, IReadOnlyList<string> focus)
    {
        var n = files.Count;

        // Who declares each name worth counting.
        var declarers = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
            foreach (var name in files[i].Symbols.Where(Links).Select(s => s.Name).Distinct())
            {
                if (name.Length < 3 || TooCommon.Contains(name)) continue;
                if (!declarers.TryGetValue(name, out var list)) declarers[name] = list = [];
                list.Add(i);
            }

        // Edges: file A → file B for each of B's names A mentions. A shared name's weight is split
        // between everyone who declares it.
        //
        // AIDER'S WEIGHTS, because a name is evidence in proportion to how unlikely it is to turn
        // up by accident. A compound name (`SessionFactory`, `cxp_call`) means one thing; a plain
        // word (`Message`, `count`) is written for a dozen reasons in every file, so it weighs a
        // tenth as much. And mentions count by their square root: a file that names a type forty
        // times uses it more than one that names it once, but not forty times more.
        var outgoing = new Dictionary<int, double>[n];
        for (var a = 0; a < n; a++)
        {
            outgoing[a] = [];
            foreach (var (name, count) in files[a].Mentions)
            {
                if (!declarers.TryGetValue(name, out var owners) || owners.Contains(a)) continue;
                var weight = Distinctiveness(name) * (owners.Count > ManyDeclarers ? 0.1 : 1.0) * Math.Sqrt(count);
                foreach (var b in owners)
                    outgoing[a][b] = outgoing[a].GetValueOrDefault(b) + weight / owners.Count;
            }
        }

        // HOW MANY OTHER FILES MENTION EACH DECLARED NAME, methods included. Too loose to link
        // files by, but within one file it is the right order for deciding which members to keep
        // when there are too many to show.
        var usedBy = new Dictionary<string, int>(StringComparer.Ordinal);
        var owner = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
            foreach (var symbol in files[i].Symbols)
                owner.TryAdd(symbol.Name, i);
        for (var a = 0; a < n; a++)
            foreach (var name in files[a].Mentions.Keys)
                if (owner.TryGetValue(name, out var first) && first != a)
                    usedBy[name] = usedBy.GetValueOrDefault(name) + 1;

        var (restart, unmatched) = Restarts(files, focus, outgoing);
        var rank = PageRank(outgoing, restart);

        var order = Enumerable.Range(0, n)
            .Where(i => files[i].Symbols.Any(s => s.Kind != SymbolKind.Namespace))
            .OrderByDescending(i => rank[i])
            .ThenBy(i => files[i].Path, StringComparer.Ordinal)
            .Select(i => files[i])
            .ToList();

        return new Ranking(order, usedBy, unmatched);
    }

    /// <summary>
    /// Whether a declaration's name links the files that mention it to the file that declares it.
    ///
    /// <para>TYPES AND TOP-LEVEL FUNCTIONS, NOT METHODS. A type's name is distinctive and is written
    /// out wherever it is used; a method is reached as <c>x.Close()</c>, and its bare name —
    /// <c>Close</c>, <c>State</c>, <c>Options</c> — turns up in a hundred files that have nothing to do
    /// with this one. Counted, they would rank a dialog box above the engine it belongs to.</para>
    ///
    /// <para>AND ONLY AT THE TOP OF A FILE. A nested type is written through its parent
    /// (<c>PluginRequest.List</c>), so the parent's name already links its users; its own name is
    /// often a plain word — <c>List</c>, <c>Load</c> — that every file mentions for other reasons.</para>
    ///
    /// <para>AND NOT A FILE-LOCAL FUNCTION. In C and C++ a <c>static</c> function at the top of a
    /// file cannot be called from any other, and its names — <c>count</c>, <c>copy</c>, <c>result</c>
    /// — are words hundreds of files use for something else.</para>
    /// </summary>
    private static bool Links(Symbol symbol) =>
        symbol.Depth == 0 && symbol.Kind is SymbolKind.Type or SymbolKind.Member
        && !(symbol.Kind == SymbolKind.Member && symbol.Signature.StartsWith("static ", StringComparison.Ordinal));

    /// <summary>
    /// How much a mention of this name says: ten for a compound name of some length, a tenth for a
    /// private-looking one, one otherwise.
    /// </summary>
    public static double Distinctiveness(string name)
    {
        if (name.StartsWith('_')) return 0.1;
        var humps = name.Skip(1).Count(char.IsUpper) + name.Trim('_').Count(c => c == '_');
        return name.Length >= 8 && humps >= 1 ? 10 : 1;
    }

    /// <summary>
    /// Where the walk restarts: everywhere equally, or — with a focus — mostly at the focused files
    /// and partly at the files that use them.
    /// </summary>
    private static (double[] Restart, List<string> Unmatched) Restarts(IReadOnlyList<FileOutline> files,
        IReadOnlyList<string> focus, Dictionary<int, double>[] outgoing)
    {
        var n = files.Count;
        var restart = new double[n];
        var unmatched = new List<string>();
        var focused = new HashSet<int>();

        foreach (var raw in focus)
        {
            var entry = raw.Trim().Replace('\\', '/');
            if (entry.StartsWith("./", StringComparison.Ordinal)) entry = entry[2..];
            entry = entry.TrimEnd('/');
            if (entry is "" or ".") continue;
            // MATCHED, NOT "ADDED SOMETHING NEW": an entry inside a folder an earlier entry already
            // focused matches files that are already in the set, and still matched.
            var matched = false;

            for (var i = 0; i < n; i++)
            {
                var path = files[i].Path;
                // A PATH, A SUFFIX OF ONE, OR A FOLDER: "Agent.cs" finds src/Agents/Agent.cs, and
                // "src/Agents" finds everything under it.
                if (path == entry || path.EndsWith("/" + entry, StringComparison.Ordinal)
                    || path.StartsWith(entry + "/", StringComparison.Ordinal) || path.Contains("/" + entry + "/", StringComparison.Ordinal))
                {
                    focused.Add(i);
                    matched = true;
                }
            }
            // A SYMBOL: whoever declares it, counted even when the name is too common for ranking —
            // a reader asking about "Run" by name means it.
            for (var i = 0; i < n; i++)
                if (files[i].Symbols.Any(s => s.Name == raw.Trim()))
                {
                    focused.Add(i);
                    matched = true;
                }

            if (!matched) unmatched.Add(raw.Trim());
        }

        if (focused.Count == 0)
        {
            Array.Fill(restart, n == 0 ? 0 : 1.0 / n);
            return (restart, unmatched);
        }

        var users = Enumerable.Range(0, n).Where(i => !focused.Contains(i) && outgoing[i].Keys.Any(focused.Contains)).ToList();
        foreach (var i in focused) restart[i] = 0.8 / focused.Count;
        foreach (var i in users) restart[i] = 0.2 / users.Count;
        if (users.Count == 0) foreach (var i in focused) restart[i] = 1.0 / focused.Count;

        return (restart, unmatched);
    }

    private static double[] PageRank(Dictionary<int, double>[] outgoing, double[] restart)
    {
        var n = outgoing.Length;
        var rank = (double[])restart.Clone();
        var totals = outgoing.Select(o => o.Values.Sum()).ToArray();

        for (var round = 0; round < Iterations; round++)
        {
            var next = new double[n];
            var dangling = 0.0;
            for (var a = 0; a < n; a++)
            {
                if (totals[a] == 0) { dangling += rank[a]; continue; }
                foreach (var (b, weight) in outgoing[a])
                    next[b] += Damping * rank[a] * weight / totals[a];
            }
            // A FILE THAT USES NOTHING sends its rank back to where walks restart, rather than
            // losing it — otherwise the leaves would drain rank out of the whole graph.
            for (var i = 0; i < n; i++)
                next[i] += (1 - Damping) * restart[i] + Damping * dangling * restart[i];
            rank = next;
        }

        return rank;
    }
}
