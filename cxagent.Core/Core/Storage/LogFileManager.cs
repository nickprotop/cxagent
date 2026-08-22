namespace CxAgent.Core.Storage;

/// <summary>
/// Manages per-job log files under logs/&lt;agent_id&gt;/&lt;job_id&gt;.{log,stdout,stderr}.
///
/// <para>ONE DIRECTORY PER AGENT, FOR ITS WHOLE LIFE. The key is the agent's id, which is stable,
/// so everything one agent ever logged lands together and its turns number straight through. Keying
/// on anything minted per user message — a goal id, say — would scatter a single linear session's
/// diagnostics across a directory per prompt with turn numbering restarting at 000 in each, splitting
/// the run you want to read across several directories with no way to tell which came first.</para>
///
/// <para>Log I/O is diagnostic and best-effort — write failures are surfaced to the caller but must
/// not be treated as job failures by callers (see spec).</para>
/// </summary>
public class LogFileManager
{
    private static readonly string[] Streams = { "log", "stdout", "stderr" };
    private readonly AppPaths _paths;

    /// <summary>
    /// Ids of the agents this one was spawned under, outermost first. Empty for a session.
    ///
    /// <para>A SUB-AGENT NESTS UNDER ITS PARENT. Children mint their own <c>Agent.Id</c>, so logging
    /// them through a flat <c>logs/&lt;id&gt;/</c> scheme would give every spawn a TOP-LEVEL directory
    /// indistinguishable from a real session — `ls -t` would put a child above the parent that created
    /// it, and the newest directory would routinely not be the newest session. The work is
    /// hierarchical; the record of it should be too, so the ancestry prefixes the path.</para>
    /// </summary>
    private readonly string[] _ancestry;

    public LogFileManager(AppPaths paths) : this(paths, []) { }

    private LogFileManager(AppPaths paths, string[] ancestry)
    {
        _paths = paths;
        _ancestry = ancestry;
    }

    /// <summary>
    /// A manager that writes beneath <paramref name="parentAgentId"/>, for an agent it spawned.
    ///
    /// <para>Returns a NEW instance rather than mutating: one factory serves every child, and a
    /// mutable parent id would be shared state under concurrent spawns — which step 3 makes real.</para>
    /// </summary>
    public LogFileManager Under(string parentAgentId) =>
        new(_paths, [.. _ancestry, parentAgentId]);

    public string PathFor(string agentId, string jobId, string stream)
    {
        if (Array.IndexOf(Streams, stream) < 0)
            throw new ArgumentException($"stream must be one of log/stdout/stderr, got '{stream}'.", nameof(stream));
        return Path.Combine(_paths.LogsDir, Path.Combine([.. _ancestry, agentId]), $"{jobId}.{stream}");
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
