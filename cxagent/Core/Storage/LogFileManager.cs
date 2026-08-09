namespace CxAgent.Core.Storage;

/// <summary>
/// Manages per-job log files under logs/&lt;agent_id&gt;/&lt;job_id&gt;.{log,stdout,stderr}.
///
/// <para>ONE DIRECTORY PER AGENT, FOR ITS WHOLE LIFE. The key used to be a goal id, minted afresh on
/// every user message, so a single linear session scattered its diagnostics across a directory per
/// prompt with turn numbering restarting at 000 in each — the run you wanted to read was split
/// across several directories with no way to tell which came first. The agent's id is stable, so
/// everything one agent ever logged lands together and its turns number straight through.</para>
///
/// <para>Log I/O is diagnostic and best-effort — write failures are surfaced to the caller but must
/// not be treated as job failures by callers (see spec).</para>
/// </summary>
public class LogFileManager
{
    private static readonly string[] Streams = { "log", "stdout", "stderr" };
    private readonly AppPaths _paths;

    public LogFileManager(AppPaths paths) => _paths = paths;

    public string PathFor(string agentId, string jobId, string stream)
    {
        if (Array.IndexOf(Streams, stream) < 0)
            throw new ArgumentException($"stream must be one of log/stdout/stderr, got '{stream}'.", nameof(stream));
        return Path.Combine(_paths.LogsDir, agentId, $"{jobId}.{stream}");
    }

    /// <summary>
    /// One writer at a time PER FILE.
    ///
    /// <para>ProcessRunner appends from <c>OutputDataReceived</c>, which fires on a thread-pool
    /// thread once per line — so a chatty job issued many overlapping appends to the SAME path.
    /// <c>File.AppendAllTextAsync</c> opens, writes and closes with no coordination, so those raced
    /// and LOST BYTES: a live `ls ~/bin` logged "ncode-remote" where "opencode-remote" should have
    /// been, and dropped other lines outright.</para>
    ///
    /// <para>That is not merely a cosmetic log defect. The job's captured output is what the
    /// orchestrator is shown, so a corrupted log becomes a wrong ANSWER — the same drive concluded
    /// "the ~/bin directory is empty" about a directory holding six scripts.</para>
    ///
    /// <para>Keyed by path rather than one global lock: two different jobs writing two different
    /// files must still proceed in parallel, which is the normal case under a fan-out.</para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    public async Task AppendAsync(string agentId, string jobId, string stream, string text)
    {
        var path = PathFor(agentId, jobId, stream);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(path, text);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> ReadAsync(string agentId, string jobId, string stream)
    {
        var path = PathFor(agentId, jobId, stream);
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
    }

    public void DeleteAgentLogs(string agentId)
    {
        var dir = Path.Combine(_paths.LogsDir, agentId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
