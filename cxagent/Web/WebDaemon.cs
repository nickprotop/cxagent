using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CxAgent.Web;

/// <summary>A running <c>cxagent web</c>, as its state file records it.</summary>
/// <param name="Pid">Its process.</param>
/// <param name="Url">Where it serves.</param>
/// <param name="Started">When it started.</param>
/// <param name="Daemon">Whether it runs in the background, with no terminal.</param>
public sealed record WebRunning(int Pid, string Url, DateTimeOffset Started, bool Daemon);

/// <summary>
/// <c>cxagent web --daemon</c>, <c>status</c> and <c>stop</c>: one server per config folder, found by a state file beside
/// config.json.
///
/// <para>A STATE FILE, NOT A PID FILE ALONE: status answers where it serves and since when without asking it, and a
/// stale file — the machine restarted under it — is told apart from a live one by the process it names.</para>
///
/// <para>STOPPED BY A FILE, NOT A SIGNAL: Windows has no SIGTERM to send another process, and a file the server watches
/// stops it the same way everywhere — as Ctrl+C does, its running turns cancelled and saved.</para>
/// </summary>
public static class WebDaemon
{
    /// <summary>Set in the background child's environment, so it knows it is the daemon and does not start another.</summary>
    public const string ChildVariable = "CXAGENT_WEB_DAEMON";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The state file of the server for <paramref name="configDir"/>.</summary>
    public static string StatePath(string configDir) => Path.Combine(configDir, "web.json");

    /// <summary>The file that asks it to stop.</summary>
    public static string StopPath(string configDir) => Path.Combine(configDir, "web.stop");

    /// <summary>Where the daemon's output goes.</summary>
    public static string LogPath(string configDir) => Path.Combine(configDir, "web.log");

    /// <summary>Records this process as the server for <paramref name="configDir"/>.</summary>
    public static void Record(string configDir, WebRunning running)
    {
        try { File.Delete(StopPath(configDir)); } catch (IOException) { }
        File.WriteAllText(StatePath(configDir), JsonSerializer.Serialize(running, Json));
    }

    /// <summary>Removes the record, when it is this process's — a second server refused at start must not erase the first's.</summary>
    public static void Forget(string configDir, int pid)
    {
        if (Read(configDir) is { } running && running.Pid != pid) return;
        try { File.Delete(StatePath(configDir)); } catch (IOException) { }
        try { File.Delete(StopPath(configDir)); } catch (IOException) { }
    }

    /// <summary>
    /// The server running for <paramref name="configDir"/>, or null. A record whose process is gone is stale — the
    /// machine restarted, or it was killed — and is removed.
    /// </summary>
    public static WebRunning? Running(string configDir)
    {
        if (Read(configDir) is not { } running) return null;
        if (IsAlive(running.Pid)) return running;
        try { File.Delete(StatePath(configDir)); } catch (IOException) { }
        return null;
    }

    private static WebRunning? Read(string configDir)
    {
        try
        {
            return File.Exists(StatePath(configDir))
                ? JsonSerializer.Deserialize<WebRunning>(File.ReadAllText(StatePath(configDir)), Json)
                : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    // ALIVE AND CXAGENT: a pid reused by another program since is not this server.
    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.ProcessName.Contains("cxagent", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>Asks the server to stop, and waits for it; false when it did not stop in <paramref name="wait"/>.</summary>
    public static bool Stop(string configDir, WebRunning running, TimeSpan wait)
    {
        File.WriteAllText(StopPath(configDir), "");
        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until)
        {
            if (!IsAlive(running.Pid)) return true;
            Thread.Sleep(200);
        }
        return false;
    }

    /// <summary>Watches for the stop file, and stops <paramref name="stop"/> when it appears.</summary>
    public static async Task WatchForStopAsync(string configDir, CancellationTokenSource stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(500, stop.Token).ConfigureAwait(false);
                if (File.Exists(StopPath(configDir))) { stop.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Starts this command again in the background — the same arguments without <c>--daemon</c> — its output in
    /// <see cref="LogPath"/>, and returns its process; the caller waits for it to record itself.
    /// </summary>
    public static Process StartChild(string configDir, IReadOnlyList<string> args)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("cxagent cannot find its own executable.");
        var log = LogPath(configDir);
        var psi = new ProcessStartInfo(self)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("web");
        foreach (var a in args) if (a is not ("--daemon" or "-d")) psi.ArgumentList.Add(a);
        psi.Environment[ChildVariable] = "1";
        var child = Process.Start(psi) ?? throw new InvalidOperationException("The background server could not start.");
        child.StandardInput.Close();

        // ITS OUTPUT TO THE LOG, copied by this process only until it returns — after that the child writes its own: it
        // reopens the log as its standard output and error the moment it starts (see Detach).
        _ = Pump(child.StandardOutput, log);
        _ = Pump(child.StandardError, log);
        return child;
    }

    private static async Task Pump(StreamReader from, string log)
    {
        try
        {
            while (await from.ReadLineAsync().ConfigureAwait(false) is { } line)
                await File.AppendAllTextAsync(log, line + Environment.NewLine).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    /// <summary>
    /// In the background child, first thing: its output to the log for good, and — on Linux and macOS — a session of its
    /// own, so the terminal that started it closing does not hang it up.
    /// </summary>
    public static void Detach(string configDir)
    {
        var log = new StreamWriter(new FileStream(LogPath(configDir), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Console.SetOut(log);
        Console.SetError(log);
        if (!OperatingSystem.IsWindows())
        {
            try { setsid(); } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    /// <summary>How long it has run, as a person says it.</summary>
    public static string Uptime(DateTimeOffset started)
    {
        var t = DateTimeOffset.Now - started;
        return t.TotalMinutes < 1 ? $"{(int)t.TotalSeconds}s"
            : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m"
            : t.TotalDays < 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
            : $"{(int)t.TotalDays}d {t.Hours}h";
    }
}
