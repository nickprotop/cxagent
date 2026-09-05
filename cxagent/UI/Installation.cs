using System.Reflection;
using System.Runtime.InteropServices;
using CxAgent.Core.Storage;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// What this copy of cxagent is, and what it knows about the times it ran before.
///
/// <para>THE SUBJECT IS THE INSTALL, not the session and not the project. A session's totals live in
/// <see cref="SessionRecord"/>; a folder's trust and edit mode live in the permission rules, scoped
/// by path. What belongs here is true of this app on this machine regardless of which folder it is
/// opened in — when it was first seen, how many times it has run, which version ran last.</para>
///
/// <para>READ ONCE PER LAUNCH. <see cref="Read"/> both reports and records: the launch count is
/// incremented and the version written as it answers, so asking twice reports the second launch as
/// ordinary. A launch reads this once and passes the value to whoever wants it.</para>
///
/// <para>NOTHING HERE LEAVES THE MACHINE. It is written to the local store and read by the app
/// itself; there is no telemetry endpoint, and adding one would be a decision about the user's data
/// rather than an extension of this type.</para>
///
/// <para>IN THE APP, NOT IN CORE. Every field here is about the front end — the executable a user
/// launched, the terminal library it draws with — and Core is a library other consumers embed, for
/// which none of that is true. The store it persists to is Core's; the questions are not.</para>
/// </summary>
/// <param name="IsFirstRun">
/// Nothing has run here before — no recorded version, and no telemetry from an earlier session.
/// </param>
/// <param name="UpgradedFrom">
/// The version that ran last when it differs from <paramref name="Version"/>, else null. A DOWNGRADE
/// REPORTS TOO: the question is whether the build changed, and answering "did it go up" would need a
/// version parser this deliberately does not have. Null on a first run — a fresh install has not
/// upgraded from anything, and a "what's new" gated on this must not greet someone who has never
/// seen the old thing.
/// </param>
/// <param name="Version">This build, from the assembly — what the release workflow stamped.</param>
/// <param name="FirstSeen">
/// When this install first ran. Null only where the row could not be read or parsed; a first run
/// sets it to now and reports that value.
/// </param>
/// <param name="LaunchCount">
/// How many times it has run, including this one. Starts at 1 on a first run.
/// </param>
/// <param name="Path">Where the binary lives, which is how it was installed as far as anything here
/// can tell — a tool package, a release archive and a local build sit in different places.</param>
/// <param name="CoreVersion">
/// The CxAgent.Core build this app is running against.
///
/// <para>NORMALLY THE SAME AS <paramref name="Version"/>, and that is not a reason to hide it. The
/// release packs both from one git tag in one job precisely so they cannot drift, so a line that
/// repeats the app version is the healthy case being reported — and the value of showing it is the
/// day it does NOT match, which is a development build against a stale Core and exactly the thing a
/// diagnostic should surface.</para>
/// </param>
/// <param name="UiVersion">The terminal UI library's build. Independently versioned, so this
/// carries information on every build rather than only on a mismatch.</param>
/// <param name="Runtime">The .NET that is running it.</param>
/// <param name="Os">The operating system, as the runtime describes it.</param>
/// <param name="Architecture">The process architecture — x64, arm64.</param>
public sealed record Installation(
    bool IsFirstRun,
    string? UpgradedFrom,
    string Version,
    string CoreVersion,
    DateTimeOffset? FirstSeen,
    int LaunchCount,
    string Path,
    string UiVersion,
    string Runtime,
    string Os,
    string Architecture)
{
    private const string VersionKey = "last_version";
    private const string FirstSeenKey = "first_seen";
    private const string LaunchCountKey = "launch_count";

    /// <summary>
    /// Reads what this install is and records that it ran.
    ///
    /// <para>THE WRITE HAPPENS ON READ, not at exit. A process that crashes or is killed still counts
    /// as having run: the alternative replays a first-run experience for someone who already saw it
    /// and quit, which is the more annoying of the two failures.</para>
    /// </summary>
    public static Installation Read(UsageHistoryStore history, string version)
    {
        var previous = history.GetState(VersionKey);
        history.SetState(VersionKey, version);

        // A NULL VERSION MEANS "NEVER RAN" OR "COULD NOT READ", and those want opposite answers —
        // GetState returns null for both. A store holding telemetry has plainly run before whatever
        // the version row says, so an empty one is what lets a null mean "new". TotalRows counts the
        // telemetry tables and NOT app_state, so the rows written here cannot make a genuine first
        // run look like a return visit; it throws where the reads swallow, and a store that cannot
        // be counted is not evidence of a fresh install.
        bool empty;
        try { empty = history.TotalRows() == 0; }
        catch (Exception) { empty = false; }

        bool firstRun = previous is null && empty;

        var now = DateTimeOffset.UtcNow;
        var firstSeen = ReadFirstSeen(history, now);
        var launches = ReadLaunchCount(history);

        return new Installation(
            IsFirstRun: firstRun,
            UpgradedFrom: previous is not null && previous != version ? previous : null,
            Version: version,
            FirstSeen: firstSeen,
            LaunchCount: launches,
            Path: ExecutableDirectory(),
            CoreVersion: VersionOf(typeof(UsageHistoryStore).Assembly),
            UiVersion: VersionOf(typeof(ConsoleWindowSystem).Assembly),
            Runtime: RuntimeInformation.FrameworkDescription,
            Os: RuntimeInformation.OSDescription,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
    }

    /// <summary>
    /// The install date, written once and never revised.
    ///
    /// <para>SET ON THE FIRST LAUNCH THAT LACKS IT, not only on a first run. An install that predates
    /// this field has no row and is not new — stamping it with today would claim it was installed
    /// today, but leaving it null forever means the question is never answerable for the users who
    /// have been here longest. Today is wrong by the age of the install; null is wrong forever.
    /// </para>
    /// </summary>
    private static DateTimeOffset? ReadFirstSeen(UsageHistoryStore history, DateTimeOffset now)
    {
        if (history.GetState(FirstSeenKey) is { } stored
            && DateTimeOffset.TryParse(stored, System.Globalization.CultureInfo.InvariantCulture,
                                       System.Globalization.DateTimeStyles.RoundtripKind, out var seen))
            return seen;

        history.SetState(FirstSeenKey, now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return now;
    }

    /// <summary>
    /// This launch's number, counting from 1.
    ///
    /// <para>A VALUE THAT WILL NOT PARSE RESTARTS THE COUNT rather than suppressing it. The row is
    /// written by this file alone, so a garbled one means a corrupted or hand-edited store, and an
    /// undercount is harmless where a crash on a bad row is not.</para>
    /// </summary>
    private static int ReadLaunchCount(UsageHistoryStore history)
    {
        _ = int.TryParse(history.GetState(LaunchCountKey), out var previous);
        var current = previous < 0 ? 1 : previous + 1;
        history.SetState(LaunchCountKey, current.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return current;
    }

    /// <summary>
    /// Where the running executable lives.
    ///
    /// <para>NOT <c>AppContext.BaseDirectory</c>. The releases are published with
    /// <c>PublishSingleFile</c>, and under that BaseDirectory is the EXTRACTION directory — a temp
    /// path that changes between runs — so the one build a user actually downloads is the one where
    /// it would answer with something meaningless. <c>ProcessPath</c> is the real executable, and
    /// AbiPluginLoader.cs:55 carries the same warning from the time this bit somebody else.</para>
    /// </summary>
    private static string ExecutableDirectory()
    {
        var exe = Environment.ProcessPath;
        return exe is not null
            ? System.IO.Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;
    }

    /// <summary>
    /// An assembly's informational version, without the source-revision the SDK appends.
    ///
    /// <para>INFORMATIONAL, NOT <c>GetName().Version</c>: the four-part assembly version drops the
    /// prerelease tag and is frequently left at a default, where the informational one is what the
    /// build actually stamped.</para>
    /// </summary>
    private static string VersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?.Split('+')[0]
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";
}
