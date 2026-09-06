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

            _ = logs.AppendAsync(Agent, Job, Stream, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // A DIAGNOSTIC MUST NOT TAKE DOWN WHAT IT IS DIAGNOSING.
        }
    }
}
