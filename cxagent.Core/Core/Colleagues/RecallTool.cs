using System.Text;
using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Memory;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Web;

namespace CxAgent.Core.Colleagues;

/// <summary>What a recall reaches: one colleague's library, and what a browser may see while the session is served to one.</summary>
/// <param name="Library">The library the colleague lives in.</param>
/// <param name="Colleague">Whose past and experience.</param>
/// <param name="Served">The served folders, or null for a session no browser sees.</param>
public sealed record RecallScope(ColleagueStore Library, string Colleague, ServedFolders? Served)
{
    /// <summary>
    /// How a recall asks before returning what was read in a folder a served browser cannot reach — the session's gate,
    /// with the caller's own policy. Null where nobody can be asked: such a recall is held back.
    /// </summary>
    public Func<PermissionRequest, IJobContext, CancellationToken, Task<bool>>? Ask { get; init; }
}

/// <summary>
/// A colleague's way back into what it has done: words across its past, one round whole, and the current version of its
/// experience — what its starting experience left out, or what grew elsewhere since it was brought in.
///
/// <para>ITS OWN LIBRARY, READ ONLY, AND NOT WRAPPED IN THE GATE: a colleague reading its own past is not a request to
/// ask about. The one exception is a session served to a browser: a round holds, word for word, what was read in a folder
/// that browser may not reach — where it was lived, and every folder it read elsewhere — so recalling it asks as reading
/// the folder would (user, 2026-11-08: "colleague must know"). ASKED WHILE IT WORKS, NOT BEFORE: which rounds it returns
/// is known only once it has looked, and a search whose hits are all in view asks nothing.</para>
///
/// <para>EVERY ANSWER FITS ONE RESULT, and says what it left out and how to narrow — a past grows without end, and a
/// round can be hundreds of messages.</para>
/// </summary>
public sealed class RecallTool(RecallScope scope) : IAgentTool
{
    public const string ToolName = "recall";

    /// <summary>The most an answer carries — a tool result's room, with space left for the conversation around it.</summary>
    public const int MaxChars = 24_000;

    /// <summary>The most rounds a search lists.</summary>
    public const int MaxHits = 12;

    private const int ExcerptChars = 160;

    public ToolDefinition Definition { get; } = new(ToolName,
        "Your own past and experience. search: rounds of your earlier work that hold all the given words, newest first. "
        + "read: one round, whole — the round number a statement in your experience names, or one a search found. "
        + "experience: the current version of your core, or of a codebase you know — it may have grown since you began. "
        + "Use it before working something out again that you may already have learned, and to see what a statement in "
        + "your experience rests on.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                action = new { type = "string", @enum = new[] { "search", "read", "experience" } },
                query = new { type = "string", description = "search: the words; every one must appear." },
                codebase = new { type = "string", description = "search, experience: one codebase you know, by its folder's name." },
                round = new { type = "integer", description = "read: the round's number." },
                part = new { type = "integer", description = "read: which part of a round too long for one answer; 1 first." },
            },
            required = new[] { "action" },
        }));

    /// <summary>None up front: what a recall would return is known only once it has looked — see the class.</summary>
    public PermissionRequest? Gate(JobParameters call) => null;

    public async Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct)
    {
        string text;
        try
        {
            var asked = new Asked(context, ct);
            text = Action(call) switch
            {
                "search" => await SearchAsync(call, asked),
                "read" => await ReadAsync(call, asked),
                "experience" => ExperienceOf(Value(call, "codebase")),
                var other => $"recall knows search, read and experience, not '{other}'.",
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            text = $"Your library could not be read: {e.Message}";
        }
        return new JobResult { Success = true, Output = new() { ["content"] = Hidden(text) } };
    }

    /// <summary>The call's context and token, for the questions a recall may ask partway.</summary>
    private sealed record Asked(IJobContext Context, CancellationToken Ct);

    /// <summary>
    /// Whether <paramref name="round"/> may be returned: always, unless the session is served to a browser and the round
    /// was lived in, or read, a folder that browser cannot reach — then the user is asked, once per such folder in this call.
    /// </summary>
    private async Task<bool> MayReturnAsync(PastRound round, Asked asked, Dictionary<string, bool> answered)
    {
        if (scope.Served?.Roots is not { } roots) return true;
        foreach (var folder in round.AlsoRead.Concat(round.ReadElsewhere).Prepend(round.Folder).Where(f => !Inside(roots, f)))
        {
            if (!answered.TryGetValue(folder, out var yes))
            {
                yes = scope.Ask is { } ask
                    && await ask(new PermissionRequest(PermissionKind.FileRead, folder, folder + Path.DirectorySeparatorChar), asked.Context, asked.Ct);
                answered[folder] = yes;
            }
            if (!yes) return false;
        }
        return true;
    }

    private async Task<string> SearchAsync(JobParameters call, Asked asked)
    {
        var words = (Value(call, "query") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "Say what to look for: search needs a query.";
        var found = Rounds(call).Where(r => words.All(w => Text(r).Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        if (found.Count == 0)
            return $"Nothing in your past holds all of: {string.Join(' ', words)}. Your experience may: recall with action experience.";
        var answered = new Dictionary<string, bool>(StringComparer.Ordinal);
        var hits = new List<PastRound>();
        foreach (var round in found)
            if (await MayReturnAsync(round, asked, answered)) hits.Add(round);
        var held = found.Count - hits.Count;
        if (hits.Count == 0)
            return $"{held} round(s) hold that, but they were lived or read where this session's browser cannot see, and recalling them was not allowed.";

        var sb = new StringBuilder();
        var shown = 0;
        foreach (var round in hits.Take(MaxHits))
        {
            var line = $"round {round.Seq} · {Short(round.Session)} · {FolderName(round.Folder)} · "
                + $"{round.Ended:yyyy-MM-dd} — {Excerpt(round, words[0])}\n";
            if (sb.Length + line.Length > MaxChars - 200) break;
            sb.Append(line);
            shown++;
        }
        if (hits.Count > shown)
            sb.Append($"… and {hits.Count - shown} more. Add words, or name a codebase, to narrow it.\n");
        if (held > 0)
            sb.Append($"{held} more held back: lived or read where this session's browser cannot see, and not allowed.\n");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ReadAsync(JobParameters call, Asked asked)
    {
        var past = Past();
        if (RoundNumber(call) is not { } seq || past.FirstOrDefault(r => r.Seq == seq) is not { } round)
            return past.Count == 0 ? "Your past is empty." : $"There is no such round; yours are {past[0].Seq} to {past[^1].Seq}.";
        if (!await MayReturnAsync(round, asked, new Dictionary<string, bool>(StringComparer.Ordinal)))
            return $"Round {seq} was not recalled: it was lived or read where this session's browser cannot see, and that was not allowed.";

        // HIDDEN BEFORE IT IS CUT: a secret line split across two parts would pass as two halves neither of which looks
        // like one.
        var text = Hidden(PastRounds.Render(round, full: true));
        var room = MaxChars - 200;
        var parts = Math.Max(1, (text.Length + room - 1) / room);
        if (parts == 1) return text;
        var part = Math.Clamp(Number(call, "part") ?? 1, 1, parts);
        var piece = text.Substring((part - 1) * room, Math.Min(room, text.Length - (part - 1) * room));
        return $"(part {part} of {parts}{(part < parts ? $" — read part {part + 1} for more" : "")})\n" + piece;
    }

    private string ExperienceOf(string? codebase)
    {
        var experience = scope.Library.ReadExperience(scope.Colleague);
        if (string.IsNullOrWhiteSpace(codebase))
            return Capped(experience.Core.Length > 0 ? experience.Core : "Nothing is distilled into your core yet.");
        if (Workplace(codebase) is not { } place)
            return $"You know no codebase called '{codebase}'. You know: {string.Join(", ", Known())}.";
        return Capped(experience.Codebases.TryGetValue(place.Codebase, out var text) && text.Length > 0
            ? text : $"Nothing is distilled about {FolderName(place.Folder)} yet.");
    }

    /// <summary>The rounds a search or its gate looks at: every one, or one codebase's, newest first.</summary>
    private IEnumerable<PastRound> Rounds(JobParameters call)
    {
        var rounds = Past().AsEnumerable().Reverse();
        if (Value(call, "codebase") is { Length: > 0 } codebase)
            rounds = Workplace(codebase) is { } place ? rounds.Where(r => SameCodebase(r.Folder, place)) : [];
        return rounds;
    }

    private IReadOnlyList<PastRound> Past() => scope.Library.Past(scope.Colleague);

    /// <summary>A codebase it knows, by its folder's name or its key — matched against the record, not recomputed from a
    /// folder that may have moved since.</summary>
    private Workplace? Workplace(string codebase) =>
        scope.Library.Find(scope.Colleague)?.Workplaces.FirstOrDefault(w =>
            string.Equals(FolderName(w.Folder), codebase, StringComparison.OrdinalIgnoreCase)
            || string.Equals(w.Codebase, codebase, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<string> Known() =>
        scope.Library.Find(scope.Colleague)?.Workplaces.Select(w => FolderName(w.Folder)) ?? [];

    private static string Text(PastRound round) => string.Join('\n', round.Messages.Select(m => m.Content));

    private static string Excerpt(PastRound round, string word)
    {
        var line = round.Messages.SelectMany(m => m.Content.Split('\n'))
            .FirstOrDefault(l => l.Contains(word, StringComparison.OrdinalIgnoreCase))?.Trim() ?? "";
        return line.Length <= ExcerptChars ? line : line[..ExcerptChars] + "…";
    }

    private static string Capped(string text) =>
        text.Length <= MaxChars ? text : text[..(MaxChars - 80)] + $"\n[… {text.Length - MaxChars + 80} characters left out]";

    /// <summary>
    /// Lines that look like a key or a password, replaced — the memory tool's rule. A round holds what a command printed,
    /// word for word, and a recall puts it back in front of a model and, through the transcript, a person.
    /// </summary>
    private static string Hidden(string text) =>
        string.Join('\n', text.Split('\n').Select(l => MemoryStore.LooksSecret(l) ? "[a line that looked like a secret is left out]" : l));

    private static bool Inside(IReadOnlyList<string> roots, string folder)
    {
        var real = PathResolver.RealFolder(folder);
        return roots.Any(root => PathResolver.Contains(root, real));
    }

    /// <summary>A round of this codebase: lived in its recorded folder, or in any folder of the same codebase — a worktree.</summary>
    private static bool SameCodebase(string folder, Workplace place)
    {
        if (string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(place.Folder), StringComparison.Ordinal))
            return true;
        try { return Codebase.Key(folder) == place.Codebase; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static string FolderName(string folder) => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));

    private static string Short(string session) => session.Length <= 8 ? session : session[..8];

    private static string? Action(JobParameters call) => Value(call, "action")?.Trim().ToLowerInvariant();

    private static long? RoundNumber(JobParameters call) => Number(call, "round");

    private static int? Number(JobParameters call, string key) =>
        int.TryParse(Value(call, key), out var n) ? n : null;

    private static string? Value(JobParameters call, string key) =>
        call.Values.TryGetValue(key, out var v) ? v?.ToString() : null;
}
