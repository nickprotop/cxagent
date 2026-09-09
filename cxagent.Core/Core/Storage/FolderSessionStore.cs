using System.Text.Json;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;

namespace CxAgent.Core.Storage;

/// <summary>
/// Resume state, one directory per agent.
///
/// <para>THE SURFACE IS THE ROW STORE'S, DELIBERATELY UNCHANGED. Every caller — the launch resume
/// offer, <c>/sessions</c>, uid lookup — keeps working against the same methods, so this is a change
/// of where bytes live and nothing else. Anything that reads differently afterwards is a defect
/// rather than an improvement.</para>
///
/// <para>AND THE CONTENT IS WHY IT MOVED. A context holds whatever the agent touched: file contents,
/// command output, a token that appeared in a command. "Delete this session" across databases is
/// several coordinated deletes and a missed one is invisible; a directory removed takes all of it,
/// verifiably. <c>LogsDir</c> is already forced to owner-only 0700, which is the posture this content
/// needs and which another database would have had to be given separately.</para>
/// </summary>
public sealed class FolderSessionStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>How long a finished session's folder survives. See <see cref="Prune"/>.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);

    /// <summary>One turn's worth of resume state.</summary>
    /// <remarks>
    /// A RECORD BECAUSE THE LIST REACHED SIX — two strings and two ints in a row, which is exactly
    /// the shape where transposing a pair compiles cleanly and writes the wrong field.
    /// </remarks>
    public readonly record struct ResumeTurn(string AgentId, IReadOnlyList<ChatMessage> Context,
        int InputTokens, int OutputTokens, string? WorkingDir = null, EditMode? Edits = null);

    /// <summary>The five-argument form kept for callers that have no mode to record.</summary>
    public void SaveTurn(string agentId, IReadOnlyList<ChatMessage> context,
        int inputTokens, int outputTokens, string? workingDir = null) =>
        SaveTurn(new ResumeTurn(agentId, context, inputTokens, outputTokens, workingDir));

    /// <summary>
    /// Writes this agent's header and context, replacing both.
    ///
    /// <para>THE WHOLE CONTEXT GOES EACH TIME, as it did into the row: compression rewrites it
    /// wholesale, so an append-only log would have to be reconciled against a list that no longer
    /// matches. That same sentence is why this file is not JSONL while the transcript is.</para>
    ///
    /// <para>AND THE STATE IS ONLY EVER SET TO RUNNING HERE, never cleared: a session that stops
    /// saving is a session that crashed, and it is offered for resume precisely BECAUSE nothing
    /// marked it finished.</para>
    /// </summary>
    public void SaveTurn(ResumeTurn turn)
    {
        try
        {
            var folder = new SessionFolder(paths, turn.AgentId);
            Directory.CreateDirectory(folder.Dir);
            SessionFolder.WriteAtomic(folder.ContextPath,
                JsonSerializer.Serialize(turn.Context, Json));
            SessionHeader.Write(folder, new SessionHeader(
                turn.AgentId, TitleOf(turn.Context), turn.WorkingDir,
                turn.InputTokens, turn.OutputTokens,
                SessionEndState.Running, DateTimeOffset.UtcNow, turn.Edits));
        }
        catch (Exception)
        {
            // Resume state is worth less than the turn in flight. See SessionHeader.Write.
        }
    }

    /// <summary>This agent's snapshot, or null when its folder holds nothing readable.</summary>
    public SessionSnapshot? LoadById(string agentId)
    {
        var folder = new SessionFolder(paths, agentId);
        var header = SessionHeader.Read(folder);
        if (header is null) return null;

        var context = ReadContext(folder);
        if (context is null) return null;

        return new SessionSnapshot(header.AgentId, context, header.InputTokens,
            header.OutputTokens, header.UpdatedAt, header.Edits, header.WorkingDir);
    }

    private static IReadOnlyList<ChatMessage>? ReadContext(SessionFolder folder)
    {
        var text = SessionFolder.ReadOrNull(folder.ContextPath);
        if (text is null) return null;
        try
        {
            return JsonSerializer.Deserialize<List<ChatMessage>>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every readable header, newest first — the basis of every listing here.</summary>
    private List<SessionHeader> Headers()
    {
        var found = new List<SessionHeader>();
        foreach (var id in SessionFolder.AgentIdsUnder(paths))
        {
            if (SessionHeader.Read(new SessionFolder(paths, id)) is { } h) found.Add(h);
        }
        found.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return found;
    }

    /// <summary>
    /// The newest session in this folder that nothing marked finished.
    ///
    /// <para>READS HEADERS ONLY. The context of the winner is read once, afterwards — which is the
    /// whole reason the header is a separate file.</para>
    /// </summary>
    public SessionSnapshot? LoadLatestUnfinished(string? workingDir = null)
    {
        foreach (var h in Headers())
        {
            if (h.State != SessionEndState.Running) continue;
            if (workingDir is not null && !PathsMatch(h.WorkingDir, workingDir)) continue;
            if (LoadById(h.AgentId) is { } snap) return snap;
        }
        return null;
    }

    /// <summary>
    /// The sessions, newest first.
    ///
    /// <para><paramref name="all"/> WIDENS THE FOLDER, NOT THE STATE. Every session is listed
    /// whatever its ending — the palette offers finished ones too, and always has; what
    /// <c>Finished</c> stops is the STARTUP offer proposing a context already resumed. A row hidden
    /// here would be a conversation the user could no longer reach by name.</para>
    ///
    /// <para>AND A ROW WITH NO WORKING DIRECTORY IS NOT IN ANY FOLDER, so a folder-scoped listing
    /// omits it — matching the row store's <c>working_dir IS NOT NULL AND working_dir = $dir</c>.</para>
    /// </summary>
    public IReadOnlyList<SessionInfo> List(string? workingDir = null, bool all = false)
    {
        var rows = new List<SessionInfo>();
        foreach (var h in Headers())
        {
            if (!all && (h.WorkingDir is null || !PathsMatch(h.WorkingDir, workingDir))) continue;
            rows.Add(new SessionInfo(h.AgentId, h.Title, h.WorkingDir, h.InputTokens,
                h.OutputTokens, h.State != SessionEndState.Running, h.UpdatedAt));
        }
        return rows;
    }

    /// <summary>
    /// What a uid prefix names.
    ///
    /// <para>AMBIGUITY IS REPORTED, NEVER RESOLVED TO THE NEWEST MATCH — silently picking is how
    /// someone restores the wrong conversation and does not notice.</para>
    /// </summary>
    public UidLookup LoadByUid(string prefix, string? withinFolder = null)
    {
        // EITHER END, because a ULID starts with a timestamp. Sessions begun in the same few minutes
        // share their opening characters, so the listing shows the TAIL — the random half — and a
        // leading match alone could never resolve what a user reads off the screen. A full uid pasted
        // from --sessions or an exit hint still matches from the front.
        //
        // AND CASE-INSENSITIVE, because a ULID is stored uppercase and a user retypes what they see
        // in whatever case their fingers produce.
        var matches = Headers()
            .Where(h => h.AgentId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                     || h.AgentId.EndsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(h => withinFolder is null || PathsMatch(h.WorkingDir, withinFolder))
            .Select(h => h.AgentId)
            .ToList();

        if (matches.Count != 1) return new UidLookup(null, matches);
        return new UidLookup(LoadById(matches[0]), matches);
    }

    /// <summary>Ended cleanly; nothing to come back to.</summary>
    public void MarkFinished(string agentId) => Retire(agentId, SessionEndState.Exited);

    /// <summary>Replaced by a resume; kept, but never offered again.</summary>
    public void MarkSuperseded(string agentId) => Retire(agentId, SessionEndState.Superseded);

    private void Retire(string agentId, int state)
    {
        var folder = new SessionFolder(paths, agentId);
        if (SessionHeader.Read(folder) is not { } header) return;
        SessionHeader.Write(folder, header with { State = state });
    }

    public int CountSessions() => Headers().Count;

    /// <summary>
    /// Deletes finished sessions past their retention.
    ///
    /// <para>THIS IS NOT WHAT THE ROW STORE'S PRUNE WAS — IT IS MORE IMPORTANT. Nothing swept
    /// <c>logs/</c> before, so a directory that now holds contexts as well as diagnostics would
    /// otherwise grow forever. It is the only backstop this layout has.</para>
    ///
    /// <para>AND A DIRECTORY WITH NO READABLE HEADER IS LEFT ALONE, never swept. It may be a session
    /// mid-write, a half-created folder, or something the user put there; deleting on absence turns
    /// an unreadable file into data loss, which is the opposite of the rule that discards a context
    /// and keeps the folder.</para>
    /// </summary>
    public void Prune(TimeSpan keepFinishedFor)
    {
        var cutoff = DateTimeOffset.UtcNow - keepFinishedFor;
        foreach (var id in SessionFolder.AgentIdsUnder(paths).ToList())
        {
            var folder = new SessionFolder(paths, id);
            if (SessionHeader.Read(folder) is not { } h) continue;

            // EXITED ONLY, NEVER SUPERSEDED. A session someone continued is the history behind a
            // conversation that is still going — deleting it would cut the past off a live thread.
            // Running is likewise untouched: it has not ended at all.
            if (h.State != SessionEndState.Exited) continue;
            if (h.UpdatedAt > cutoff) continue;
            try
            {
                Directory.Delete(folder.Dir, recursive: true);
            }
            catch (Exception)
            {
                // A folder that will not delete is a folder swept next time.
            }
        }
    }

    /// <summary>Two working directories naming the same place, tolerating a trailing separator.</summary>
    private static bool PathsMatch(string? a, string? b) =>
        string.Equals(a?.TrimEnd(Path.DirectorySeparatorChar),
                      b?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);

    /// <summary>
    /// The first real user message, shortened — what a listing shows instead of a ULID.
    ///
    /// <para>SKIPS TOOL RESULTS, which also carry the user role: the first thing a PERSON typed is
    /// the title, and a tool result would name the session after something it did rather than
    /// something it was asked.</para>
    /// </summary>
    public static string? TitleOf(IReadOnlyList<ChatMessage> context)
    {
        var first = context.FirstOrDefault(m =>
            string.Equals(m.Role, "user", StringComparison.Ordinal)
            && m.ToolCallId is null
            && !string.IsNullOrWhiteSpace(m.Content));

        if (first is null) return null;

        var text = first.Content.ReplaceLineEndings(" ").Trim();
        return text.Length <= 80 ? text : text[..80].TrimEnd() + "…";
    }
}
/// <param name="Title">The first user message, clipped, or null for a session that never got one.</param>
/// <param name="Finished">
/// Ended cleanly, OR superseded by a resume. Two meanings in one flag: see the resume path, which
/// retires the row it restored so one conversation cannot be accepted twice.
/// </param>
/// <param name="WorkingDir">Which project the session ran in.</param>
/// <param name="InputTokens">Tokens sent across the session.</param>
/// <param name="OutputTokens">Tokens generated across the session.</param>
/// <param name="UpdatedAt">When it last did anything — what orders a resume list.</param>
public sealed record SessionInfo(
    string Uid,
    string? Title,
    string? WorkingDir,
    int InputTokens,
    int OutputTokens,
    bool Finished,
    DateTimeOffset UpdatedAt);

/// <summary>What a uid lookup found. Ambiguity is REPORTED, never resolved to the newest match —
/// silently picking is how someone restores the wrong conversation and does not notice.</summary>
public sealed record UidLookup(SessionSnapshot? Session, IReadOnlyList<string> Ambiguous)
{
    public bool IsAmbiguous => Ambiguous.Count > 1;
}

/// <param name="Edits">
    /// The session's edit mode, so resume does not silently widen it. NULLABLE because a row from a
    /// database predating the column genuinely has no mode, and that absence must stay
    /// distinguishable from a recorded choice — see <c>LoadLatestUnfinished</c> for how it resolves.
    /// </param>
/// <param name="AgentId">The session's own id.</param>
/// <param name="Context">The restored conversation.</param>
/// <param name="InputTokens">Tokens the session had sent when saved.</param>
/// <param name="OutputTokens">Tokens it had generated.</param>
/// <param name="UpdatedAt">When it was last written.</param>
/// <param name="WorkingDir">
/// The folder the conversation was working in, or null for a row written before the column
/// existed.
///
/// <para>CARRIED SO A RESUME CAN GO THERE. The column was always stored — it is what scopes the
/// listing — but the snapshot dropped it, so resuming a conversation from another project left
/// the session working in the folder it was already in: the model remembered one project while
/// its tools acted on another.</para>
///
/// <para>NULL IS NOT A FOLDER. An older row says nothing about where it ran, and guessing is
/// worse than staying put — a resume that silently re-scoped to the wrong project would change
/// which files a turn may touch.</para>
/// </param>
public sealed record SessionSnapshot(
    string AgentId,
    IReadOnlyList<ChatMessage> Context,
    int InputTokens,
    int OutputTokens,
    DateTimeOffset UpdatedAt,
    EditMode? Edits = null,

    string? WorkingDir = null);
