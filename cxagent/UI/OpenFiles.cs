namespace CxAgent.UI;

/// <summary>
/// Which files have a tab, and what that tab is called.
///
/// <para>KEYED ON THE FULL PATH so <c>./a.cs</c> and <c>a.cs</c> are one file — otherwise <c>/open</c>
/// on a path spelled differently would open a second tab onto the same bytes, and two buffers over
/// one file is the one state a save cannot reconcile.</para>
///
/// <para>TITLES ARE THE TAB API. <c>TabControl</c> addresses tabs by title, so the registry owns
/// them: it hands out a unique one and can map back.</para>
/// </summary>
public sealed class OpenFiles
{
    private readonly Dictionary<string, string> _titleByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pathByTitle = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Paths => _titleByPath.Keys;

    public bool TryGetTitle(string path, out string title)
        => _titleByPath.TryGetValue(Key(path), out title!);

    public string? PathFor(string title) => _pathByTitle.GetValueOrDefault(title);

    /// <summary>
    /// Registers a path and returns the tab title for it. Registering one already known returns the
    /// title it already has, so a second <c>/open</c> finds the tab rather than making one.
    ///
    /// <para>THE FILE NAME, DISAMBIGUATED ONLY WHEN IT HAS TO BE. Two <c>Program.cs</c> in one
    /// session cannot be told apart by name, so the second gets its parent directory — which is what
    /// distinguishes them — rather than a number, which does not.</para>
    /// </summary>
    public string Add(string path)
    {
        var key = Key(path);
        if (_titleByPath.TryGetValue(key, out var existing)) return existing;

        var title = Path.GetFileName(key);
        if (_pathByTitle.ContainsKey(title))
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(key) ?? string.Empty);
            title = string.IsNullOrEmpty(parent) ? key : Path.Combine(parent, title);
        }

        // Still taken — three files of a name, or a repeated parent — so fall back to the full path,
        // which is unique by construction.
        if (_pathByTitle.ContainsKey(title)) title = key;

        _titleByPath[key] = title;
        _pathByTitle[title] = key;
        return title;
    }

    public void Remove(string path)
    {
        var key = Key(path);
        if (!_titleByPath.Remove(key, out var title)) return;
        _pathByTitle.Remove(title);
    }

    private static string Key(string path) => Path.GetFullPath(path);
}
