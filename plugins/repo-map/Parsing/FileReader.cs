namespace CxAgent.Plugins.RepoMap.Parsing;

/// <summary>
/// One file as the ranker sees it: its declarations, from the tree where there is a grammar and it
/// loaded, and every name its code mentions.
///
/// <para>MENTIONS ALWAYS COME FROM THE TEXT. Measured across 13 repositories, edges built from the
/// parser's references alone ranked a project's core lower than every word its code uses does: a
/// name passed as a value, aliased, or reached through a dynamic call is a use too, and no query
/// marks it. The text reader blanks comments and strings first, so neither counts.</para>
///
/// <para>A TREE WITH SYNTAX ERRORS IS CHECKED AGAINST THE TEXT. A parser recovers around what it
/// cannot read, but not always well: a macro it cannot expand (fmt's <c>FMT_BEGIN_NAMESPACE</c>), or
/// a contextual keyword this C# grammar takes for the keyword (a local named <c>partial</c>), can cost
/// a whole class. The text reader skips what it does not recognise, so for such a file the reader
/// that finds more declarations is kept. A file that parses cleanly takes the tree's alone — there
/// the text reader's extra lines are its misreadings.</para>
///
/// <para>THE TEXT READER ALONE for a language without a grammar, and for a file whose parse ran out
/// of time.</para>
/// </summary>
public static class FileReader
{
    public static FileOutline Outline(string path, string text, Language language, Engine engine)
    {
        var asText = TextOutliner.Outline(path, text, language);
        var reader = Grammars.For(path) is { } grammar ? engine.ReaderFor(grammar) : null;
        if (reader is null || TreeOutliner.Parse(path, text, reader, TreeOutliner.ParseBudget) is not { } parsed)
            return asText;
        if (!parsed.Clean && asText.Symbols.Count > parsed.Outline.Symbols.Count)
            return asText;
        return parsed.Outline with { Mentions = asText.Mentions };
    }
}
