using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CxAgent.Core.Colleagues;

/// <summary>
/// The library on disk: <c>&lt;config&gt;/colleagues/&lt;slug&gt;/</c> per colleague — <c>colleague.json</c>,
/// <c>experience/core.md</c> and one <c>experience/&lt;codebase&gt;.md</c> per codebase, and <c>past.jsonl</c>.
///
/// <para>IN CXAGENT'S OWN FOLDER, NEVER A REPOSITORY, as memory is: a colleague carries what it learned from many
/// projects, and none of them should find another's knowledge in its tree.</para>
///
/// <para>READ FROM DISK EVERY CALL. Several sessions — several processes — live with one colleague at once, and each
/// may have appended or distilled since this one last looked; a cache here would be a second, staler truth.</para>
/// </summary>
public sealed partial class ColleagueStore(string configDir)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    // ONE LINE PER ROUND: an indented round would span lines, and a reader skipping a damaged line would lose its place.
    private static readonly JsonSerializerOptions Line = new(Json) { WriteIndented = false };

    /// <summary>The library folder.</summary>
    public string Root { get; } = Path.Combine(configDir, "colleagues");

    /// <summary>Why <paramref name="name"/> cannot be a colleague's name, or null when it can.</summary>
    public static string? NameRefusal(string name) =>
        NamePattern().IsMatch(name)
            ? null
            : "A colleague's name is 1–40 letters, digits, spaces, '-' or '_', and does not start or end with a space.";

    /// <summary>Every colleague, the most recently worked first.</summary>
    public IReadOnlyList<ColleagueRecord> List()
    {
        if (!Directory.Exists(Root)) return [];
        return Folders()
            .Select(d => ReadRecord(Path.Combine(d, "colleague.json")))
            .OfType<ColleagueRecord>()
            .OrderByDescending(LastWorked)
            .ToList();
    }

    /// <summary>The colleague called <paramref name="name"/>, in any case, or null.</summary>
    public ColleagueRecord? Find(string name) =>
        NameRefusal(name) is null ? ReadRecord(Path.Combine(FolderOf(name), "colleague.json")) : null;

    /// <summary>Makes a new colleague's folder. Throws <see cref="ArgumentException"/> for a bad or taken name.</summary>
    public ColleagueRecord Create(ColleagueRecord record)
    {
        // REFUSED BEFORE ANY PATH IS BUILT FROM IT: the name becomes a folder, and "../x" would be one outside the library.
        if (NameRefusal(record.Name) is { } why) throw new ArgumentException(why, nameof(record));
        var folder = FolderOf(record.Name);
        Directory.CreateDirectory(Path.Combine(folder, "experience"));

        // colleague.json, CREATED NEW, IS THE CLAIM — case-folded by the slug, so two names differing only in case share
        // it, and atomic, so two processes keeping one name at once cannot both win. A folder left without it by a keep
        // that failed half-way is no claim.
        try
        {
            using (new FileStream(Path.Combine(folder, "colleague.json"), FileMode.CreateNew, FileAccess.Write)) { }
        }
        catch (IOException) when (File.Exists(Path.Combine(folder, "colleague.json")))
        {
            throw new ArgumentException($"There is already a colleague called '{Find(record.Name)?.Name ?? record.Name}'.", nameof(record));
        }
        Save(record);
        return record;
    }

    /// <summary>Rewrites a colleague's identity whole, through a temporary file so a reader never sees half of it.</summary>
    public void Save(ColleagueRecord record) => SaveTo(FolderOf(record.Name), record);

    /// <summary>
    /// Writes a record into the folder it was read from. NOT FOUND BY THE RECORD'S NAME: a rename writes the new name into
    /// a folder that, until it is written, is known only by the old one.
    /// </summary>
    private static void SaveTo(string folder, ColleagueRecord record)
    {
        var path = Path.Combine(folder, "colleague.json");
        var temp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Changes a colleague's identity under its own lock and answers the result, or null when there is no such colleague.
    ///
    /// <para>READ, CHANGED AND WRITTEN UNDER ONE HOLD: a distiller bumping the version and a session noting a workplace
    /// both rewrite the whole file, and without the hold the second writer would silently undo the first.</para>
    /// </summary>
    public ColleagueRecord? Update(string name, Func<ColleagueRecord, ColleagueRecord> change)
    {
        if (NameRefusal(name) is not null) return null;
        var folder = FolderOf(name);
        if (!Directory.Exists(folder)) return null;
        FileStream held;
        try
        {
            held = Hold(Path.Combine(folder, "colleague.lock"));
        }
        // A FOLDER GONE BETWEEN THE LOOK AND THE HOLD is a colleague forgotten meanwhile: nothing to change.
        catch (Exception e) when (e is DirectoryNotFoundException or FileNotFoundException)
        {
            return null;
        }
        using (held)
        {
            if (ReadRecord(Path.Combine(folder, "colleague.json")) is not { } now) return null;
            var changed = change(now);
            SaveTo(folder, changed);
            return changed;
        }
    }

    /// <summary>Appends one whole round to the past and returns the place it was given.</summary>
    public long Append(string name, PastRound round)
    {
        var folder = FolderOf(name);
        using var held = Hold(Path.Combine(folder, "past.lock"));
        var path = Path.Combine(folder, "past.jsonl");
        var seqPath = Path.Combine(folder, "past.seq");
        var seq = LastSeq(seqPath, path) + 1;

        // ONE WRITE OF ONE LINE, under the lock: two sessions finishing rounds together must never interleave bytes.
        // A LAST LINE WITH NO NEWLINE — a process that died mid-append — is ended first, so this round is a line of its
        // own rather than the tail of a line no reader can parse.
        var line = JsonSerializer.Serialize(round with { Seq = seq }, Line) + "\n";
        using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            if (stream.Length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                if (stream.ReadByte() != '\n') line = "\n" + line;
            }
            stream.Seek(0, SeekOrigin.End);
            stream.Write(Encoding.UTF8.GetBytes(line));
            stream.Flush(flushToDisk: true);
        }
        File.WriteAllText(seqPath, seq.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return seq;
    }

    /// <summary>How many rounds its past holds — from <c>past.seq</c>, so a large past is not read to count it.</summary>
    public long RoundCount(string name)
    {
        var folder = FolderOf(name);
        return LastSeq(Path.Combine(folder, "past.seq"), Path.Combine(folder, "past.jsonl"));
    }

    /// <summary>
    /// Every round after the watermark still to be learned from, in order — none while the colleague is frozen, and never
    /// one lived frozen.
    ///
    /// <para>EMPTY WHILE FROZEN, ASKED EVERY TIME: a drain asks again after each batch, so a freeze made while one is under
    /// way stops it at the next batch rather than after every round waiting.</para>
    /// </summary>
    public IReadOnlyList<PastRound> Undistilled(string name)
    {
        if (Find(name) is not { } record) return [];
        if (record.Frozen) return [];
        return Past(name).Where(r => r.Seq > record.DistilledThrough && !record.Unlearned.Any(u => u.Holds(r.Seq))).ToList();
    }

    /// <summary>
    /// Freezes a colleague: from now on its rounds are kept and none is learned from. A colleague already frozen stays
    /// frozen from where it began. Null for no such colleague.
    /// </summary>
    public ColleagueRecord? Freeze(string name) =>
        Update(name, r => r.Frozen ? r : r with { Frozen = true, FrozenAfter = HeldCount(name) });

    /// <summary>
    /// Lets a colleague learn again FROM NOW ON (user, 2026-11-08): the rounds it lived frozen stay in its past for recall
    /// and never become experience — "do not learn from this stretch" is what freezing was for. Null for no such colleague.
    /// </summary>
    public ColleagueRecord? Unfreeze(string name) =>
        Update(name, r =>
        {
            if (!r.Frozen) return r;
            var now = HeldCount(name);
            return r with
            {
                Frozen = false,
                Unlearned = now > r.FrozenAfter ? [.. r.Unlearned, new SeqRange(r.FrozenAfter + 1, now)] : r.Unlearned,
            };
        });

    /// <summary>
    /// The round count read under the past's own lock — the one every append holds — so a round being appended as the
    /// count is taken lands wholly before it or wholly after.
    /// </summary>
    private long HeldCount(string name)
    {
        using var held = Hold(Path.Combine(FolderOf(name), "past.lock"));
        return RoundCount(name);
    }

    /// <summary>Every round, in order. A line that cannot be read is skipped: one damaged round costs that round.</summary>
    public IReadOnlyList<PastRound> Past(string name)
    {
        var path = Path.Combine(FolderOf(name), "past.jsonl");
        if (!File.Exists(path)) return [];
        var rounds = new List<PastRound>();
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize<PastRound>(line, Line) is { } round) rounds.Add(round);
            }
            catch (JsonException) { }
        }
        return rounds.OrderBy(r => r.Seq).ToList();
    }

    /// <summary>Its experience as distilled so far — empty texts before the first distilling.</summary>
    public Experience ReadExperience(string name)
    {
        var folder = Path.Combine(FolderOf(name), "experience");
        var core = ReadText(Path.Combine(folder, "core.md"));
        var codebases = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.md")
                .Where(f => Path.GetFileName(f) != "core.md")
                .ToDictionary(f => Path.GetFileNameWithoutExtension(f), ReadText, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        return new Experience(core, codebases);
    }

    /// <summary>Rewrites a colleague's core experience, through a temporary file.</summary>
    public void WriteCore(string name, string text)
    {
        var folder = Path.Combine(FolderOf(name), "experience");
        Directory.CreateDirectory(folder);
        WriteText(Path.Combine(folder, "core.md"), text);
    }

    /// <summary>Rewrites one codebase's experience, through a temporary file.</summary>
    public void WriteCodebase(string name, string codebase, string text)
    {
        // A KEY IS MADE BY Codebase.Key, never typed — but it becomes a file name, so it is checked as one.
        if (!KeyPattern().IsMatch(codebase)) throw new ArgumentException($"'{codebase}' is not a codebase key.", nameof(codebase));
        var folder = Path.Combine(FolderOf(name), "experience");
        Directory.CreateDirectory(folder);
        WriteText(Path.Combine(folder, codebase + ".md"), text);
    }

    /// <summary>
    /// The folder a colleague of this name lives in — by its name now or a former one, in any case — or, for a name no
    /// colleague has had, the folder it would be made in.
    ///
    /// <para>THE FOLDER IS NAMED AFTER THE FIRST NAME AND NEVER MOVES: a rename rewrites the record only, since other
    /// processes may be appending to the folder as it happens. So a name is found by its folder first — the common case —
    /// then by reading the records.</para>
    /// </summary>
    public string FolderOf(string name)
    {
        var own = Path.Combine(Root, Slug(name));
        if (File.Exists(Path.Combine(own, "colleague.json"))) return own;
        var wanted = name.Trim();
        foreach (var folder in Folders())
            if (ReadRecord(Path.Combine(folder, "colleague.json")) is { } r
                && (Same(r.Name, wanted) || r.FormerNames.Any(f => Same(f, wanted))))
                return folder;
        return own;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every colleague's folder. ONE STARTING WITH A DOT IS NONE: a colleague being forgotten is first moved aside under a
    /// dot name, so nothing finds it while it is deleted.
    /// </summary>
    private IEnumerable<string> Folders() =>
        Directory.Exists(Root)
            ? Directory.EnumerateDirectories(Root).Where(d => !Path.GetFileName(d).StartsWith('.'))
            : [];

    /// <summary>The folder name and handle a name becomes: lower case, spaces as '-'.</summary>
    public static string Slug(string name) => name.Trim().ToLowerInvariant().Replace(' ', '-');

    private static DateTimeOffset LastWorked(ColleagueRecord r) =>
        r.Workplaces.Count > 0 ? r.Workplaces.Max(w => w.Last) : r.Created;

    private static ColleagueRecord? ReadRecord(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ColleagueRecord>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static void WriteText(string path, string text)
    {
        var temp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// The highest place in the past: from <c>past.seq</c>, written beside every append, so a round's end does not read
    /// a past that may run to hundreds of megabytes. A past from before it, or one whose counter was lost, is read once.
    /// </summary>
    private static long LastSeq(string seqPath, string path)
    {
        try
        {
            if (File.Exists(seqPath) && long.TryParse(File.ReadAllText(seqPath).Trim(), out var kept)) return kept;
        }
        catch (IOException) { }
        if (!File.Exists(path)) return 0;
        long last = 0;
        foreach (var line in File.ReadLines(path))
        {
            // ONLY THE SEQ IS WANTED, so only the start of each line is parsed — a round can be megabytes.
            var at = line.IndexOf("\"seq\":", StringComparison.Ordinal);
            if (at < 0) continue;
            var start = at + 6;
            var end = start;
            while (end < line.Length && char.IsAsciiDigit(line[end])) end++;
            if (long.TryParse(line.AsSpan(start, end - start), out var seq) && seq > last) last = seq;
        }
        return last;
    }

    /// <summary>
    /// An exclusive hold on a lock file, across processes: opened with no sharing, and retried while another holds it.
    /// The operating system releases it if its holder dies, so a crash never leaves the past locked.
    /// </summary>
    internal static FileStream Hold(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            // A FOLDER THAT IS GONE IS NOT A HELD LOCK: retried, it would cost seconds on a worker's release thread before
            // failing anyway.
            catch (IOException e) when (attempt < 600 && e is not (DirectoryNotFoundException or FileNotFoundException))
            {
                Thread.Sleep(10 + Random.Shared.Next(10));
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-](?:[A-Za-z0-9 _-]{0,38}[A-Za-z0-9_-])?$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex KeyPattern();
}
