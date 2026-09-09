namespace CxAgent.Core.Storage;

/// <summary>
/// One agent's directory, and the writes that must not be seen half-finished.
///
/// <para>THE AGENT'S ID NAMES THE DIRECTORY, NOT THE SESSION'S, and the two differ. <c>Session.Id</c>
/// is "this session's own identity, for the life of the session"; <c>Session.SessionId</c> is the
/// agent's — a fresh ULID per <c>Agent</c>, replaced on every re-wire and again on resume. Resume
/// state and the history archive both key on the agent's, because a resumed session IS a new agent
/// writing its own rows, and <see cref="LogFileManager"/> already lays out
/// <c>logs/&lt;agent-id&gt;/</c>. Keying this on the session's would split one agent's material
/// across two trees.</para>
/// </summary>
public sealed class SessionFolder(AppPaths paths, string agentId)
{
    public string Dir { get; } = Path.Combine(paths.LogsDir, agentId);

    /// <summary>The small header every listing reads — see <see cref="SessionHeader"/>.</summary>
    public string HeaderPath => Path.Combine(Dir, "session.json");

    /// <summary>The messages. Read only by an actual resume or wake, never by a listing.</summary>
    public string ContextPath => Path.Combine(Dir, "context.json");

    /// <summary>Scrollback, one JSON object per line.</summary>
    public string TranscriptPath => Path.Combine(Dir, "transcript.jsonl");

    /// <summary>
    /// Writes through a temp file and renames, so a reader never sees half a file.
    ///
    /// <para>SQLITE WAS GIVING THIS AWAY FREE AND A PLAIN WRITE DOES NOT. An interrupted
    /// <c>WriteAllText</c> leaves truncated JSON that will not parse — and this is the RESUME file,
    /// so a crash during the save costs exactly the thing resume exists for. <c>File.Move</c> with
    /// overwrite is atomic on both platforms cxagent targets.</para>
    ///
    /// <para>THE TEMP FILE IS A SIBLING, never in the system temp directory: a rename across devices
    /// is a copy, which is not atomic and defeats the point.</para>
    /// </summary>
    public static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// The file's text, or null when it is absent or unreadable.
    ///
    /// <para>ABSENT AND UNREADABLE ANSWER THE SAME, because callers do the same thing with both:
    /// treat this agent as having no such artefact. Distinguishing them would buy a message nobody
    /// acts on and an exception path in the middle of a listing.</para>
    /// </summary>
    public static string? ReadOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Every agent directory under the logs root, by id. Loose files are not agents.
    ///
    /// <para>TOP LEVEL ONLY, WHICH IS WHAT KEEPS A SUB-AGENT OUT OF EVERY LISTING. A child nests
    /// inside its parent's directory, so recursing here would offer it as a resumable session the
    /// user never ran — the exact failure <c>SubAgentFactory</c> avoids by never building a child
    /// through <c>AgentHost</c>.</para>
    /// </summary>
    public static IEnumerable<string> AgentIdsUnder(AppPaths paths)
    {
        if (!Directory.Exists(paths.LogsDir)) return [];
        try
        {
            return Directory.EnumerateDirectories(paths.LogsDir)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Select(n => n!)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
