using System.Text.Json;

namespace CxAgent.Core.Storage;

/// <summary>Which kind of process holds a live session — what another process tells its user.</summary>
public enum LockHolder { Terminal, Web, Headless }

/// <summary>Who holds a session: the kind of process and its pid.</summary>
public sealed record HeldBy(LockHolder Kind, int Pid);

/// <summary>
/// One process's claim on one agent folder: while it is held, no other cxagent process opens that session.
///
/// <para>AN OS LOCK, NOT A MARKER FILE. A marker says "someone was here" and outlives a crash, so every
/// crash would leave a session nobody could open until a person deleted a file. The lock is an open
/// handle with <see cref="FileShare.None"/> — <c>flock(LOCK_EX)</c> on Unix — and the kernel drops it
/// with the process, however the process ended. The file itself staying behind means nothing.</para>
///
/// <para>WHO HOLDS IT IS A SIBLING FILE, written after the lock is taken. The locked file cannot be
/// read by anyone else while it is held, and a prober needs the holder's kind to say "open in terminal"
/// rather than "open somewhere". A <c>&lt;agent&gt;.json</c> without a held <c>&lt;agent&gt;.lock</c> is
/// stale and ignored.</para>
///
/// <para>IN ITS OWN FOLDER (<see cref="AppPaths.LocksDir"/>), NOT THE SESSION'S. A held file cannot be opened
/// by anything else in .NET, even to read, and code that walks a session's folder opens every file in it.</para>
///
/// <para>PER AGENT ID, BECAUSE ITS FOLDER IS WHAT TWO WRITERS WOULD CORRUPT. A session's agent id changes
/// on every re-wire and resume; the session manager moves the lock with it (see
/// <c>SessionManager.Open</c>), so the newest folder of a live conversation is never unclaimed.</para>
/// </summary>
public sealed class SessionLock : IDisposable
{
    private readonly FileStream _handle;
    private readonly string _holderPath;

    private SessionLock(FileStream handle, string holderPath, string agentId)
    {
        _handle = handle;
        _holderPath = holderPath;
        AgentId = agentId;
    }

    /// <summary>The agent folder this lock holds.</summary>
    public string AgentId { get; }

    /// <summary>Takes the lock on <paramref name="agentId"/>'s folder, or null when another holder has it.</summary>
    /// <param name="locksDir">The folder the locks live in — <see cref="AppPaths.LocksDir"/>.</param>
    /// <param name="agentId">The agent whose folder to claim.</param>
    /// <param name="kind">What this process is, for whoever probes it.</param>
    public static SessionLock? TryAcquire(string locksDir, string agentId, LockHolder kind)
    {
        Directory.CreateDirectory(locksDir);
        FileStream handle;
        try
        {
            handle = new FileStream(LockPath(locksDir, agentId), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        // A FAILED HOLDER NOTE DOES NOT GIVE THE LOCK BACK. The lock is what keeps a second writer out; the note
        // only improves what a prober can say, and "open in another process" is still true without it.
        var holder = HolderPath(locksDir, agentId);
        try { File.WriteAllText(holder, JsonSerializer.Serialize(new Note(Kind(kind), Environment.ProcessId))); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return new SessionLock(handle, holder, agentId);
    }

    /// <summary>Who holds <paramref name="agentId"/>'s folder, or null when nobody does.</summary>
    /// <param name="locksDir">The folder the locks live in — <see cref="AppPaths.LocksDir"/>.</param>
    /// <param name="agentId">The agent whose folder to look at.</param>
    public static HeldBy? Probe(string locksDir, string agentId)
    {
        var path = LockPath(locksDir, agentId);
        if (!File.Exists(path)) return null;
        try
        {
            // TAKING IT IS THE ONLY HONEST TEST: a free lock opens, and is let go at once.
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { return ReadHolder(HolderPath(locksDir, agentId)); }
        catch (UnauthorizedAccessException) { return ReadHolder(HolderPath(locksDir, agentId)); }
    }

    /// <summary>
    /// What to tell a user who tried to open a session <paramref name="held"/> has: where it is open, and for
    /// a terminal, how to reach it from a browser anyway.
    /// </summary>
    public static string HeldMessage(HeldBy held) => held.Kind switch
    {
        LockHolder.Terminal => "open in terminal · run /web there to share it",
        LockHolder.Web => "open on the web",
        _ => "open in another process",
    };

    /// <summary>Lets the session go: the folder can be opened by any process again.</summary>
    public void Dispose()
    {
        // THE NOTE GOES FIRST, while the lock still keeps anyone from taking the folder in between and writing
        // their own — deleting after would delete theirs.
        try { File.Delete(_holderPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _handle.Dispose();
    }

    private static string LockPath(string locksDir, string agentId) => Path.Combine(locksDir, agentId + ".lock");

    private static string HolderPath(string locksDir, string agentId) => Path.Combine(locksDir, agentId + ".json");

    private static HeldBy ReadHolder(string holderPath)
    {
        try
        {
            var note = JsonSerializer.Deserialize<Note>(File.ReadAllText(holderPath));
            if (note is not null) return new HeldBy(Parse(note.kind), note.pid);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }

        // HELD BUT UNNAMED — the holder is between taking the lock and writing its note, or could not write it.
        // Headless is the least specific thing to call it.
        return new HeldBy(LockHolder.Headless, 0);
    }

    private static string Kind(LockHolder kind) => kind switch
    {
        LockHolder.Terminal => "terminal",
        LockHolder.Web => "web",
        _ => "headless",
    };

    private static LockHolder Parse(string? kind) => kind switch
    {
        "terminal" => LockHolder.Terminal,
        "web" => LockHolder.Web,
        _ => LockHolder.Headless,
    };

#pragma warning disable IDE1006 // lower-case members ARE the file's field names
    private sealed record Note(string kind, int pid);
#pragma warning restore IDE1006
}
