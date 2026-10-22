namespace CxAgent.UI;

/// <summary>
/// A README's own title, dropped when it only repeats the plugin's name: the detail header already says
/// the name, and the README heading under it said it twice. Any other opening is the author's and stays.
/// </summary>
public static class ReadmeTitle
{
    /// <summary>The markdown without a first <c># name</c> heading that is the plugin's bare name.</summary>
    public static string Drop(string markdown, string pluginName)
    {
        var bare = pluginName[(pluginName.LastIndexOf('/') + 1)..];
        var body = markdown.TrimStart('\n', '\r');
        var end = body.IndexOf('\n');
        var first = (end < 0 ? body : body[..end]).TrimEnd('\r');
        if (!first.StartsWith("# ", StringComparison.Ordinal)) return markdown;
        if (!string.Equals(first[2..].Trim(), bare, StringComparison.OrdinalIgnoreCase)) return markdown;
        return end < 0 ? "" : body[(end + 1)..].TrimStart('\n', '\r');
    }
}
