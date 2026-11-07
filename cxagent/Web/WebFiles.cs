using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CxAgent.Core.Jobs.Builtin;
using CxAgent.Core.Sessions;
using CxAgent.UI;

namespace CxAgent.Web;

/// <summary>
/// The files one browser tab has open in its editor: what each was when read, so a save writes it back with the same
/// byte-order mark and line endings and refuses to overwrite a change it never saw — the terminal's /open, for a page.
///
/// <para>PER TAB, as the terminal's buffers are per window: two browsers editing one file each hold their own buffer,
/// and the second to save is told the file changed under it, as an agent's write would be.</para>
///
/// <para>WATCHED BY POLLING, once a second, for the files open here. A file watcher on every open file's folder would
/// cost a handle each and miss a write made through a link; the read is the same one the tab opened with.</para>
/// </summary>
internal sealed class WebFiles(Action<JsonObject> send) : IDisposable
{
    private sealed class OpenedFile(Session session, string path, FileSnapshot snapshot)
    {
        public Session Session { get; } = session;
        public string Path { get; } = path;

        /// <summary>The file as this buffer last agreed with it: read, reloaded, or saved.</summary>
        public FileSnapshot Snapshot { get; set; } = snapshot;

        /// <summary>The disk's text last reported to the page, so one change is said once.</summary>
        public string? Reported { get; set; }
    }

    private readonly ConcurrentDictionary<string, OpenedFile> _open = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _watch;

    /// <summary>The key a page addresses an open file by: its session and its full path.</summary>
    public static string Key(string sessionId, string path) => $"{sessionId}@{path}";

    /// <summary>Whether <paramref name="key"/> names a file this tab has open.</summary>
    public static bool IsFileKey(string key) => key.Contains('@');

    /// <summary>
    /// Opens <paramref name="allowed"/> — <paramref name="path"/> with every link followed, already checked against
    /// what a browser may open — for <paramref name="session"/>. Null when it opened; otherwise why not.
    /// </summary>
    public string? Open(Session session, string path, string allowed)
    {
        if (Directory.Exists(allowed)) return $"{System.IO.Path.GetFileName(path)} is a folder.";
        if (!File.Exists(allowed)) return $"There is no file {path}.";
        if (FileLoad.TryLoad(allowed, out var refusal) is not { } loaded) return refusal ?? "That file could not be read.";

        var key = Key(session.Id, allowed);
        _open[key] = new OpenedFile(session, allowed, loaded.Snapshot);
        send(new JsonObject
        {
            ["type"] = "openedFile",
            ["sessionId"] = key,
            ["parent"] = session.Id,
            ["path"] = allowed,
            ["name"] = System.IO.Path.GetFileName(allowed),
            ["text"] = loaded.Text,
            ["language"] = loaded.Language,
        });
        _watch ??= WatchAsync(_stop.Token);
        return null;
    }

    /// <summary>The page took the file as it now is on disk: its buffer is the disk's again.</summary>
    public void Reload(string key)
    {
        if (!_open.TryGetValue(key, out var open)) return;
        Open(open.Session, open.Path, open.Path);
    }

    /// <summary>
    /// Writes the page's buffer. Unless <paramref name="force"/>, a file that changed on disk since this buffer last
    /// agreed with it is not written: the page is sent what the disk holds now and asks first, as the terminal's Save
    /// does over the agent's write.
    /// </summary>
    public async Task SaveAsync(string key, string text, bool force)
    {
        if (!_open.TryGetValue(key, out var open)) { send(Error("That file is no longer open here.")); return; }

        var disk = FileLoad.TryLoad(open.Path, out _);
        var changed = disk is null ? open.Snapshot.Existed : disk.Text != open.Snapshot.Text;
        if (changed && !force)
        {
            send(new JsonObject { ["type"] = "fileConflict", ["sessionId"] = key, ["text"] = disk?.Text, ["deleted"] = disk is null });
            return;
        }

        // OVER THE DISK'S OWN CONVENTIONS when forced: the write checks it is replacing what its snapshot says, and the
        // snapshot that says what is there now is the one just read.
        var on = changed ? disk?.Snapshot ?? open.Snapshot with { Existed = false } : open.Snapshot;
        try
        {
            await FileMutation.WriteAsync(open.Path, text, on, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            send(Error($"Not saved: {e.Message}"));
            return;
        }

        // THE NEW BASELINE, and nothing to report: what is on disk is what this buffer just wrote.
        open.Snapshot = on with { Text = text, Existed = true };
        open.Reported = null;
        send(new JsonObject { ["type"] = "fileSaved", ["sessionId"] = key });

        // TOLD AFTERWARDS, queued with the next message, so the model does not go on editing from a read the user has
        // since changed.
        open.Session.Inject(FileTab.SaveMessage(open.Path, open.Session.WorkingDirectory));
    }

    /// <summary>The page closed the file's tab.</summary>
    public bool Close(string key) => _open.TryRemove(key, out _);

    /// <summary>Every file of a session the page left.</summary>
    public void CloseSession(string sessionId)
    {
        foreach (var key in _open.Keys.Where(k => k.StartsWith(sessionId + "@", StringComparison.Ordinal))) _open.TryRemove(key, out _);
    }

    private async Task WatchAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(1000, stop).ConfigureAwait(false);
                foreach (var (key, open) in _open)
                {
                    var disk = FileLoad.TryLoad(open.Path, out _);
                    var now = disk?.Text;
                    if (now == open.Snapshot.Text || now == open.Reported) continue;
                    if (now is null && open.Reported == "\0deleted") continue;
                    open.Reported = now ?? "\0deleted";
                    send(new JsonObject { ["type"] = "fileChanged", ["sessionId"] = key, ["text"] = now, ["deleted"] = now is null });
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static JsonObject Error(string message) => new() { ["type"] = "error", ["message"] = message };

    public void Dispose()
    {
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        _open.Clear();
    }
}
