namespace CxAgent.Plugins.RepoMap.Parsing;

/// <summary>
/// Which reader one file's outline comes from.
///
/// <para>THE TREE, WHERE THERE IS A GRAMMAR AND IT LOADED; the text reader otherwise, and for a file
/// whose parse ran out of time.</para>
///
/// <para>A TREE WITH SYNTAX ERRORS IS CHECKED AGAINST THE TEXT. A parser recovers around what it
/// cannot read, but not always well: a macro it cannot expand (fmt's <c>FMT_BEGIN_NAMESPACE</c>), or
/// a contextual keyword this C# grammar takes for the keyword (a local named <c>partial</c>), can cost
/// a whole class. The text reader skips what it does not recognise, so for such a file both read
/// it and the one that finds more declarations is kept. A file that parses cleanly is the tree's
/// alone — there the text reader's extra lines are its misreadings.</para>
/// </summary>
public static class FileReader
{
    public static FileOutline Outline(string path, string text, Language language, Engine engine)
    {
        var reader = Grammars.For(path) is { } grammar ? engine.ReaderFor(grammar) : null;
        if (reader is null || TreeOutliner.Parse(path, text, reader, TreeOutliner.ParseBudget) is not { } parsed)
            return TextOutliner.Outline(path, text, language);
        if (parsed.Clean) return parsed.Outline;

        var asText = TextOutliner.Outline(path, text, language);
        return asText.Symbols.Count > parsed.Outline.Symbols.Count ? asText : parsed.Outline;
    }
}
