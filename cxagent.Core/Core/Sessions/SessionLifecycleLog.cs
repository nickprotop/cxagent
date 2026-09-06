using System.Text.Json;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Sessions;

/// <summary>
/// What the manager has opened, resumed, re-wired and closed — one JSON line each.
///
/// <para>WHAT THIS ANSWERS THAT NOTHING ELSE DOES. Nothing records a session's CREATION anywhere:
/// the archive holds what a session spent and the resume store holds what it said, so "which folder
/// did that session start in, and against which model" could only be inferred by reading a
/// transcript. Invisible with one session in front of you; unanswerable with three.</para>
///
/// <para>A FILE RATHER THAN A VIEW, and that is a choice about a daemon rather than about today. A
/// long-lived host has no terminal to show anything, so a file is what would be read anyway — and
/// the same records serve a status query later by being READ rather than reinvented. `tail -f`
/// answers "what has this thing started" while it runs, which is the question during development.
/// </para>
///
/// <para>ONE LINE PER EVENT, JSON, APPEND ONLY. Structured because it will be filtered by session
/// and by folder long before it is read end to end, and append-only because the interesting case is
/// what happened before something went wrong.</para>
/// </summary>
public static class SessionLifecycleLog
{
    /// <summary>The agent this is filed under. Not a real agent — the log belongs to the process.</summary>
    private const string Agent = "app";

    /// <summary>The file, beside the watchdog's, for the same reason: it is about the app.</summary>
    private const string Job = "sessions";

    private const string Stream = "log";

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>
    /// Records one lifecycle event.
    ///
    /// <para>FIRE AND FORGET, AND SWALLOWING ITS OWN FAILURES — the stance every diagnostic here
    /// takes. A log that cannot be written must not stop a session opening, and this runs on the
    /// path that opens one.</para>
    ///
    /// <para>THE SESSION'S OWN ID, NOT THE AGENT'S. A re-wire replaces the agent and its id, so a
    /// log keyed on that could not be followed across the one event most worth following.</para>
    /// </summary>
    public static void Write(LogFileManager? logs, string what, Session session,
                             string? model = null, string? mode = null, string? detail = null)
    {
        if (logs is null) return;

        var path = logs.PathFor(Agent, Job, Stream);
        var root = logs.LogsDir;

        // NOTHING TO WRITE INTO MEANS NOTHING TO WRITE. AppendAsync CREATES the directory it needs,
        // which is right for the first log of a session and wrong for a late one: a write still in
        // flight when a caller removes its own log root puts the folder back, and a test tearing
        // down a temp directory then fails with "directory not empty" somewhere unrelated. Checking
        // first costs one stat on a path that is about to open a database anyway.
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.UtcNow.ToString("O"),
                what,
                session = session.Id,
                agent = session.SessionId,
                dir = session.WorkingDirectory,
                model,
                mode,
                detail,
            }, Compact);

            // WRITTEN HERE RATHER THAN THROUGH LogFileManager, and both halves of that matter.
            //
            // NOT AWAITED THROUGH IT: AppendAsync waits on a process-wide semaphore per path, and
            // Open runs on the UI thread from a command handler — blocking there stalled the loop
            // for five seconds, which the watchdog reported as phase Input.
            //
            // AND NOT FIRE-AND-FORGET THROUGH IT EITHER: AppendAsync CREATES the directory it needs,
            // so a write still in flight when a caller removes its own log root puts the folder
            // back, and a test tearing down a temp directory then fails with "directory not empty".
            //
            // A plain synchronous append to a file whose directory must ALREADY exist has neither
            // problem: it takes no shared lock, and it declines rather than recreating.
            // THE FIRST RECORD CREATES THE FOLDER, later ones do not. A log needs somewhere to go
            // on a fresh install; what it must not do is put back a folder somebody deleted while a
            // write was in flight. Creating only when the log root itself is present distinguishes
            // "never existed" from "has been taken away".
            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            if (!Directory.Exists(dir))
            {
                if (!Directory.Exists(root)) return;
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // A DIAGNOSTIC MUST NOT TAKE DOWN WHAT IT IS DIAGNOSING.
        }
    }
}
