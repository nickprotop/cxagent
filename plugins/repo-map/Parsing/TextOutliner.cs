namespace CxAgent.Plugins.RepoMap.Parsing;

/// <summary>
/// One file read as text: the stripper, the outliner and every identifier left in its code.
///
/// <para>THE WHOLE MAP'S FALLBACK, and the only reader for Kotlin, Swift, Dart and Objective-C,
/// which have no grammar here. It needs nothing native, so it works on any machine cxagent runs on.</para>
/// </summary>
public static class TextOutliner
{
    public static FileOutline Outline(string path, string text, Language language)
    {
        var stripped = Stripper.Strip(text, language);
        return new FileOutline(path, Outliner.Outline(stripped, language), Ranker.MentionsIn(stripped.Code));
    }
}
