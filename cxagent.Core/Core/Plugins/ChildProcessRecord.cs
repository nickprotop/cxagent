using System.Diagnostics;
using System.Text.Json;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Plugins;

/// <summary>
/// One process a plugin registered: a plugin declares the processes it spawns, Core records them,
/// and Core reaps them.
///
/// <para><see cref="StartTimeUtc"/> IS WHAT MAKES A PID SAFE TO KILL. A bare pid is reused by the
/// OS the moment the original process exits, so a record written for one process and reaped after
/// the machine has cycled through every other pid could kill a stranger. The OS also hands back a
/// process's own start time, immutable for that process's life, so a match on BOTH fields is a
/// match on the actual process rather than on a number that happens to be free again.</para>
/// </summary>
/// <param name="Pid">The process id, as returned by whatever spawned it.</param>
/// <param name="StartTimeUtc">
/// That process's own start time, read back from the OS at registration — not <c>DateTime.UtcNow</c>,
/// which would drift from the value the OS reports and defeat the match this exists for.
/// </param>
/// <param name="Plugin">Which plugin registered this child — named in the log line when a startup
/// reap kills or skips one, so a stray process is attributable rather than a silent kill.</param>
/// <param name="Session">
/// The session whose plugin spawned this, or null for a record written before the field existed.
///
/// <para>WITHOUT IT, UNWIRING IN ONE SESSION KILLED EVERY SESSION'S CHILDREN. `ReapPlugin` matched on
/// the plugin NAME alone, and a plugin is loaded per session — so `/plugin unwire csharp-lsp` in the
/// third tab killed the language servers of the first two, which went on advertising three LSP tools
/// backed by nothing. Drive-verified: three sessions, three `csharp-ls` processes, one unwire, zero
/// left.</para>
///
/// <para>NULL IS A RECORD FROM AN EARLIER RUN, and it is reaped by the plugin name alone as before —
/// a startup sweep has no session to match against, and leaving a previous crash's children running
/// is the worse failure.</para>
/// </param>
public sealed record ChildProcessRecord(int Pid, DateTime StartTimeUtc, string Plugin,
    string? Session = null)
{
    /// <summary>
    /// The <see cref="Plugin"/> value a BACKGROUNDED SHELL COMMAND is recorded under, since there is
    /// no plugin behind it.
    ///
    /// <para>THE SAME FILE RATHER THAN A SECOND STORE, because the problem is identical and this one
    /// is already solved: a pid plus a start time, an atomic write, a merge across windows, and a
    /// startup sweep wired into both of <c>SessionManager</c>'s construction paths. A parallel store
    /// would be a second merge to get right and a second sweep to forget.</para>
    ///
    /// <para>THE ANGLE BRACKETS MAKE IT UNCLAIMABLE. <see cref="ChildProcessStore.ReapPlugin"/>
    /// matches this field against a manifest name, so a marker a plugin could legally be called
    /// would let <c>/plugin unwire</c> kill the user's background commands. A manifest name is an
    /// identifier; <c>&lt;background&gt;</c> is not one.</para>
    /// </summary>
    public const string BackgroundCommand = "<background>";
}

/// <summary>
/// Persists <see cref="ChildProcessRecord"/>s across a crash — one JSON file
/// (<c>plugin-children.json</c>) under <see cref="AppPaths.ConfigDir"/> — and reaps what a previous
/// run never got to.
///
/// <para>WHY A FILE, NOT IN-MEMORY BOOKKEEPING. Everything in-process dies with the process, which
/// is exactly the case this exists for: whatever cannot be closed on the way down must be
/// collectable on the way up. A crash never runs <c>Close</c>, so the only place left to record a
/// child's existence is somewhere that survives the crash.</para>
///
/// <para>MULTIPLE PROCESSES CAN WRITE THIS FILE — two cxagent windows, each loading their own
/// plugins. <see cref="Add"/> and <see cref="Remove"/> therefore re-read the file under the lock
/// before writing, merging by pid rather than overwriting whole, so one window's plugin cannot erase
/// another's entry that is still legitimately running. The same shape as
/// <see cref="Permissions.PermissionRulesStore"/>'s own merge-on-save, for the same reason.</para>
///
/// <para>WRITES ARE ATOMIC — a temp file then a move — so a crash mid-write can never leave the
/// record itself corrupt and unreadable by the next startup's reap.</para>
/// </summary>
public sealed class ChildProcessStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();

    public ChildProcessStore(AppPaths paths) : this(paths.ConfigDir) { }

    /// <param name="configDir">Directly, for a caller that already has the string rather than the
    /// whole <see cref="AppPaths"/> — <see cref="Sessions.SharedServices.GlobalInstructionsDir"/> is
    /// already this same directory passed the same way.</param>
    public ChildProcessStore(string configDir)
    {
        _path = Path.Combine(configDir, "plugin-children.json");
    }

    /// <summary>Where this store persists, for tests that want to inspect the file directly.</summary>
    public string FilePath => _path;

    /// <summary>
    /// Records a BACKGROUNDED SHELL COMMAND so the next launch can kill it if this one never gets to.
    ///
    /// <para>THE FAILURE THIS CLOSES IS THE ONE BACKGROUNDING INTRODUCES. In-memory reaping runs from
    /// <c>SessionManager.Dispose</c>, which a SIGKILL or a crash never reaches — and unlike a
    /// plugin's child there was nothing on disk naming a detached command, because
    /// <c>IPluginContext.RegisterChildProcess</c> needs a plugin and this has none. A timeout used to
    /// guarantee a dead process; now that it hands the command over, something has to.</para>
    ///
    /// <para>THE START TIME IS READ BACK FROM THE OS, not taken as <c>DateTime.UtcNow</c> — see
    /// <see cref="ChildProcessRecord.StartTimeUtc"/>. A pid alone cannot be reaped safely, because the
    /// OS reuses the number the moment the process exits, and a value we invented here would not
    /// match the one <see cref="ReapOrphans"/> compares against.</para>
    ///
    /// <para>BEST-EFFORT, AND SILENT ON FAILURE. A command that exits in the instant between being
    /// started and being recorded has no start time to read — and there is then nothing to reap
    /// either, so refusing to background a command over it would trade a closed leak for a broken
    /// feature.</para>
    /// </summary>
    /// <param name="pid">The detached command's process id.</param>
    /// <param name="sessionId">Whose session backgrounded it, for the log line a reap writes. Not a
    /// match key here: a crash sweep has no session to compare against.</param>
    public void Record(int pid, string? sessionId = null)
    {
        try
        {
            var start = Process.GetProcessById(pid).StartTime.ToUniversalTime();
            Add(new ChildProcessRecord(pid, start, ChildProcessRecord.BackgroundCommand, sessionId));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to read: nothing survives a crash that is not running now.
        }
    }

    /// <summary>Records one child process so a future reap can find it. Called from
    /// <see cref="IPluginContext.RegisterChildProcess"/>'s implementation, not by a plugin directly —
    /// see that method's own doc for why the obligation is Core's.</summary>
    public void Add(ChildProcessRecord record)
    {
        lock (_lock)
        {
            var records = Load(_path);
            records.RemoveAll(r => r.Pid == record.Pid);
            records.Add(record);
            Save(records);
        }
    }

    /// <summary>Clears one pid from the record — at Stop, at unwire, and after a startup reap kills
    /// or confirms it. Not an error to remove a pid that is not there: <see cref="ReapOrphans"/> and
    /// an explicit Stop can both race to clear the same entry.</summary>
    public void Remove(int pid)
    {
        lock (_lock)
        {
            var records = Load(_path);
            if (records.RemoveAll(r => r.Pid == pid) == 0) return;
            Save(records);
        }
    }

    /// <summary>
    /// Kills every recorded process that is STILL the one that was recorded, and clears the file.
    ///
    /// <para>RUNS AT STARTUP, WHERE <c>SessionManager.Create</c> AND <c>SessionManager.Over</c> ARE
    /// — a pid record written where the next run can find it, and reaped at startup. Not in a UI
    /// layer: a headless embedder leaks exactly as readily as a windowed one, and only the manager's
    /// construction is common to both.</para>
    ///
    /// <para>MATCHES <see cref="ChildProcessRecord.Pid"/> AND <see cref="ChildProcessRecord.StartTimeUtc"/>
    /// BOTH before killing anything — see that type's own doc. A pid that is not running at all, or
    /// is running but under a different start time (the OS reused the number), is left alone and
    /// simply dropped from the record: there is nothing of the plugin's left to kill in the first
    /// case, and killing in the second case is exactly the stranger's-process mistake this match
    /// exists to prevent.</para>
    /// </summary>
    /// <param name="log">Told which plugin's process was killed or found already gone, so an orphan
    /// is attributable rather than a silent kill. Never throws from this method's own failures —
    /// killing a process that raced its own exit is not a reason to abandon the rest of the list.</param>
    public void ReapOrphans(Action<string> log)
    {
        List<ChildProcessRecord> records;
        lock (_lock) records = Load(_path);

        if (records.Count == 0) return;

        foreach (var record in records) Kill(record, log);

        lock (_lock) Save([]);
    }

    /// <summary>
    /// Kills whatever ONE plugin registered and clears only its own entries, leaving every other
    /// plugin's record untouched.
    ///
    /// <para>THE UNWIRE-TIME HALF OF REAPING, next to <see cref="ReapOrphans"/>'s startup-time
    /// sweep — a host process that startup reaping will not see, because startup is not coming.
    /// Called from <see cref="PluginRegistry.UnwireAsync"/>'s own
    /// step 4, after Stop, so a plugin unwired mid-session (or whose Stop hung and was abandoned)
    /// does not leave its children running for the rest of THIS process's life, only to be caught by
    /// a startup this run may never reach.</para>
    /// </summary>
    /// <param name="pluginName">Matches <see cref="ChildProcessRecord.Plugin"/> — set at
    /// registration from the manifest name the plugin was loaded under.</param>
    /// <param name="log">Told which pid was killed or found already gone — see
    /// <see cref="ReapOrphans"/>'s own parameter of the same name.</param>
    /// <param name="sessionId">
    /// Whose copy of the plugin is going. Only that session's children are killed — a plugin is
    /// loaded per session, so matching on the name alone reaped every other session's too.
    ///
    /// <para>NULL MEANS EVERY SESSION'S, which is what process shutdown wants and what a caller with
    /// no session to name must ask for explicitly rather than get by omission.</para>
    /// </param>
    public void ReapPlugin(string pluginName, Action<string> log, string? sessionId = null)
    {
        // A RECORD WITH NO SESSION BELONGS TO NOBODY LIVING — it was written before the field
        // existed, or by a run that has since died. Reaped either way: leaving a previous crash's
        // children running is the failure this store exists to prevent.
        bool Mine(ChildProcessRecord r) =>
            r.Plugin == pluginName
            && (sessionId is null || r.Session is null || r.Session == sessionId);

        List<ChildProcessRecord> mine;
        lock (_lock) mine = Load(_path).Where(Mine).ToList();

        if (mine.Count == 0) return;

        foreach (var record in mine) Kill(record, log);

        lock (_lock)
        {
            var remaining = Load(_path).Where(r => !Mine(r)).ToList();
            Save(remaining);
        }
    }

    /// <summary>Kills one recorded process if it is still the process that was recorded — see
    /// <see cref="ChildProcessRecord"/>'s own doc for why both fields must match. Shared by
    /// <see cref="ReapOrphans"/> (every record, at startup) and <see cref="ReapPlugin"/> (one
    /// plugin's records, at unwire) so the match-then-kill logic exists once.</summary>
    private static void Kill(ChildProcessRecord record, Action<string> log)
    {
        try
        {
            var process = Process.GetProcessById(record.Pid);

            // TRUNCATED TO SECONDS: the file round-trips StartTimeUtc through JSON, and
            // DateTime's serialised precision does not always survive to the tick Process.StartTime
            // reports natively. A comparison that demanded sub-second equality would treat every
            // record as a reused pid and never reap anything.
            if (Truncate(process.StartTime.ToUniversalTime()) != Truncate(record.StartTimeUtc))
            {
                log($"plugin '{record.Plugin}': pid {record.Pid} belongs to a different process now (reused by the OS) — left alone.");
                return;
            }

            process.Kill(entireProcessTree: true);
            log($"plugin '{record.Plugin}': reaped orphaned process {record.Pid}.");
        }
        catch (ArgumentException)
        {
            // GetProcessById throws when the pid is not running at all — the ordinary case for
            // a plugin that DID exit on its own; nothing to reap, nothing to log as an orphan.
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process exited between GetProcessById and Kill, or this process lacks
            // permission to signal it — either way, not this reap's to fail over.
            log($"plugin '{record.Plugin}': could not reap pid {record.Pid}: {ex.Message}");
        }
    }

    private static DateTime Truncate(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);

    // Must be called while holding _lock.
    private static List<ChildProcessRecord> Load(string path)
    {
        if (!File.Exists(path)) return [];

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<ChildProcessRecord>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // UNREADABLE IS TREATED AS EMPTY, not a crash: this file is a best-effort recovery aid,
            // and refusing to start cxagent over a corrupt recovery file would be a worse outcome
            // than the leak it exists to prevent.
            return [];
        }
    }

    // Must be called while holding _lock.
    private void Save(List<ChildProcessRecord> records)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(records, JsonOptions);
        var tmp = Path.Combine(dir, $".plugin-children.json.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}
