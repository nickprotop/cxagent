using System.Text.Json;
using CxAgent.Core.Sessions;

namespace CxAgent.Core.Storage;

/// <summary>How a session ended, or that it has not. Mirrors the column the row store used.</summary>
public static class SessionEndState
{
    /// <summary>Still going, or died without saying so — which is what makes it resumable.</summary>
    public const int Running = 0;

    /// <summary>Ended cleanly. Nothing to come back to.</summary>
    public const int Exited = 1;

    /// <summary>Replaced by a resume. Kept, but never offered again.</summary>
    public const int Superseded = 2;
}

/// <summary>
/// What a listing needs, and nothing that would make it expensive.
///
/// <para>SEPARATE FROM THE CONTEXT BECAUSE EVERY LISTING READS THIS AND NONE OF THEM READ THAT.
/// Sorting sessions by recency, filtering by folder and resolving a uid prefix all run over the whole
/// SET; if the header lived inside the messages, every launch would parse megabytes per candidate to
/// answer "which was most recent". The row store had the same split and SQLite hid its cost.</para>
///
/// <para>THE TITLE IS STORED RATHER THAN DERIVED. It came from the messages on every read before;
/// computed once at save — when they are already in hand — it costs nothing at listing time.</para>
/// </summary>
public sealed record SessionHeader(
    string AgentId,
    string? Title,
    string? WorkingDir,
    int InputTokens,
    int OutputTokens,
    int State,
    DateTimeOffset UpdatedAt,
    EditMode? Edits = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>
    /// This agent's header, or null when it is absent or will not parse.
    ///
    /// <para>UNREADABLE IS DISCARDED, NEVER THROWN — the stance both stores this replaces already
    /// take ("NO TRANSCRIPT MEANS NO REPLAY. It must not mean no app"). It is also what removes the
    /// schema-migration problem a database would have had: there is no version column because there
    /// is no migration, only a file that either parses or does not.</para>
    /// </summary>
    public static SessionHeader? Read(SessionFolder folder)
    {
        var text = SessionFolder.ReadOrNull(folder.HeaderPath);
        if (text is null) return null;
        try
        {
            return JsonSerializer.Deserialize<SessionHeader>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the header, creating the agent's directory if this is its first write.</summary>
    public static void Write(SessionFolder folder, SessionHeader header)
    {
        try
        {
            Directory.CreateDirectory(folder.Dir);
            SessionFolder.WriteAtomic(folder.HeaderPath, JsonSerializer.Serialize(header, Json));
        }
        catch (Exception)
        {
            // A FAILED WRITE MUST NOT FAIL THE TURN. This is resume state: losing it costs the
            // ability to come back, which is worth strictly less than the work in flight.
        }
    }
}
