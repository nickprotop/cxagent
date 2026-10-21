using TS = TreeSitter;

namespace CxAgent.Plugins.RepoMap.Parsing;

/// <summary>A grammar ready to read files: its parser and its compiled query.</summary>
public sealed class Reader(TS.Language language, TS.Parser parser, TS.Query query) : IDisposable
{
    public TS.Parser Parser => parser;
    public TS.Query Query => query;

    public void Dispose()
    {
        query.Dispose();
        parser.Dispose();
        language.Dispose();
    }
}

/// <summary>
/// Tree-sitter for one map: a parser and a compiled query per grammar, made the first time a file
/// needs it, and what fell back to the text reader and why.
///
/// <para>ONE PER MAP, NOT PER PROCESS. Two maps can run at once — two sub-agents asking — and a
/// Tree-sitter parser is not thread-safe. Compiling a query is the slow part and takes milliseconds,
/// so making them again per map costs less than the locking would.</para>
///
/// <para>A FAILURE IS A NOTE, NEVER AN ERROR. Natives that will not load send every file to the text
/// reader; a grammar or query that will not load sends that language there. Either way the map is
/// still made, and its header says what happened, once.</para>
/// </summary>
public sealed class Engine : IDisposable
{
    private readonly string? _unavailable;
    private readonly Func<Grammar, string> _queryText;
    private readonly Dictionary<Grammar, Reader?> _readers = [];
    private readonly List<string> _notes = [];

    private Engine(string? unavailable, Func<Grammar, string> queryText)
    {
        _unavailable = unavailable;
        _queryText = queryText;
    }

    /// <summary>The engine a map uses: real natives, embedded queries.</summary>
    public static Engine Open() => new(Natives.Problem, Grammars.QueryText);

    /// <summary>An engine whose natives did not load, for this reason — what a machine without them gets.</summary>
    public static Engine Unavailable(string reason) => new(reason, Grammars.QueryText);

    /// <summary>An engine reading its queries from elsewhere — how a test shows a query that does not compile.</summary>
    public static Engine WithQueries(Func<Grammar, string> queryText) => new(Natives.Problem, queryText);

    public IReadOnlyList<string> Notes =>
        _unavailable is null ? _notes : [$"Parsed without Tree-sitter: {_unavailable}"];

    /// <summary>The reader for a grammar, or null when that language goes to the text reader.</summary>
    public Reader? ReaderFor(Grammar grammar)
    {
        if (_unavailable is not null) return null;
        if (_readers.TryGetValue(grammar, out var known)) return known;

        Reader? reader = null;
        TS.Language? language = null;
        TS.Parser? parser = null;
        try
        {
            language = new TS.Language(grammar.Id);
            parser = new TS.Parser(language);
            reader = new Reader(language, parser, new TS.Query(language, _queryText(grammar)));
        }
        catch (Exception ex)
        {
            // A BOUNDARY: a native library, a grammar's ABI and a query's text can each fail in their
            // own way, and every one of them means the same thing here — read this language as text.
            parser?.Dispose();
            language?.Dispose();
            _notes.Add($"{grammar.Name} read as text: {ex.Message}");
        }
        _readers[grammar] = reader;
        return reader;
    }

    public void Dispose()
    {
        foreach (var reader in _readers.Values) reader?.Dispose();
        _readers.Clear();
    }
}
