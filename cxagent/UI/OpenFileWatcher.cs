namespace CxAgent.UI;

/// <summary>
/// Watches the working directory and reports changes to files that have a tab.
///
/// <para>ONE WATCHER, FILTERED BY THE REGISTRY, rather than one per open file: a watcher is a kernel
/// handle and a thread, and the number of open tabs is not a number to multiply those by. The
/// registry already knows which paths matter.</para>
///
/// <para>CALLBACKS ARRIVE OFF THE UI THREAD. The callback signature takes a path and nothing else, so
/// there is nothing here a consumer could mutate directly — everything it does with the path must be
/// marshalled before it touches a control.</para>
/// </summary>
public sealed class OpenFileWatcher : IDisposable
{
    private readonly OpenFiles _open;
    private readonly Action<string> _onChanged;
    private readonly FileSystemWatcher _watcher;

    public OpenFileWatcher(string root, OpenFiles open, Action<string> onChanged)
    {
        _open = open;
        _onChanged = onChanged;

        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };

        // FOUR EVENTS FOR ONE IDEA. A write in place raises Changed; a tool that writes to a temp
        // file and moves it into position raises Renamed or Created instead; and Deleted is the
        // fourth thing a person means by "the file changed under me". Subscribing only to Changed
        // misses the way most editors actually save.
        _watcher.Changed += (_, e) => Raise(e.FullPath);
        _watcher.Created += (_, e) => Raise(e.FullPath);
        _watcher.Renamed += (_, e) => Raise(e.FullPath);
        _watcher.Deleted += (_, e) => Raise(e.FullPath);

        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Test seam: raises the callback as a file-system event would.</summary>
    public void RaiseForTest(string path) => Raise(path);

    private void Raise(string path)
    {
        // OUR OWN WRITE IS NOT AN EXTERNAL CHANGE. Without this every save comes straight back as a
        // conflict with itself, and the tab reports the user's own edit as the agent's.
        if (FileTab.SuppressWatch) return;

        if (_open.TryGetTitle(path, out _))
            _onChanged(Path.GetFullPath(path));
    }

    public void Dispose() => _watcher.Dispose();
}
