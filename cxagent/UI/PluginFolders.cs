namespace CxAgent.UI;

/// <summary>
/// The rules for a folder added to <c>pluginPaths</c> in the Sources view, apart from the view so they can be
/// tested without a window.
///
/// <para>A FOLDER THAT DOES NOT EXIST IS NOT REFUSED. A relative entry resolves against whichever project is
/// open, so one missing for this project may exist for the next, and a folder made later starts being searched
/// then. Its row says it is not found here instead.</para>
/// </summary>
public static class PluginFolders
{
    /// <summary>The folder as it will be written: what was typed, without the spaces around it.</summary>
    public static string Clean(string typed) => typed.Trim();

    /// <summary>Why <paramref name="typed"/> cannot be added to <paramref name="listed"/>, or null when it can.</summary>
    public static string? Problem(string typed, IReadOnlyList<string> listed)
    {
        var folder = Clean(typed);
        if (folder.Length == 0) return "Type a folder to add.";
        return listed.Contains(folder, StringComparer.Ordinal) ? $"{folder} is listed already." : null;
    }
}
