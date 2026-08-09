using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

public class LogFileManagerTests : IDisposable
{
    private readonly string _dir;
    private readonly AppPaths _paths;
    public LogFileManagerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cxagent-logs-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_dir);
        _paths.EnsureCreated();
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public async Task Append_ConcurrentWritesToOneFile_LoseNothing()
    {
        // ProcessRunner appends from OutputDataReceived — a thread-pool callback, once per line — so
        // a chatty job issues many OVERLAPPING appends to the same path. File.AppendAllTextAsync
        // opens/writes/closes with no coordination, and those races LOST BYTES.
        //
        // Measured on a live drive: `ls ~/bin` logged "ncode-remote" where "opencode-remote" should
        // have been, and dropped lines outright. The orchestrator is shown that captured output, so a
        // corrupted log becomes a wrong ANSWER — the same run concluded "the ~/bin directory is
        // empty" about a directory holding six scripts.
        var logs = new LogFileManager(_paths);
        const int lines = 200;

        // Distinct, self-identifying lines: a torn write shows up as a missing or malformed entry,
        // not merely a wrong total length.
        await Task.WhenAll(Enumerable.Range(0, lines)
            .Select(i => logs.AppendAsync("g1", "j1", "stdout", $"line-{i:D3}\n")));

        var text = await logs.ReadAsync("g1", "j1", "stdout");

        for (int i = 0; i < lines; i++)
            Assert.Contains($"line-{i:D3}", text);

        // And nothing extra or mangled: exactly the lines written, no partial fragments.
        Assert.Equal(lines, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task Append_ThenRead_RoundTripsPerStream()
    {
        var mgr = new LogFileManager(_paths);
        await mgr.AppendAsync("g1", "j1", "stdout", "line one\n");
        await mgr.AppendAsync("g1", "j1", "stdout", "line two\n");
        await mgr.AppendAsync("g1", "j1", "stderr", "an error\n");

        Assert.Equal("line one\nline two\n", await mgr.ReadAsync("g1", "j1", "stdout"));
        Assert.Equal("an error\n", await mgr.ReadAsync("g1", "j1", "stderr"));
    }

    /// <summary>One agent, one directory. Turn logs from a whole session must land together — they
    /// used to scatter across a directory per user message, with turn numbering restarting in each.</summary>
    [Fact]
    public async Task AppendAsync_PutsOneAgentsTurnsInOneDirectory()
    {
        var logs = new LogFileManager(_paths);

        await logs.AppendAsync("agent-1", "context-000", "log", "first");
        await logs.AppendAsync("agent-1", "context-001", "log", "second");

        var files = Directory.GetFiles(Path.Combine(_paths.LogsDir, "agent-1"));
        Assert.Equal(2, files.Length);
    }

    [Fact]
    public async Task Append_CreatesAgentSubdirectory()
    {
        var mgr = new LogFileManager(_paths);
        await mgr.AppendAsync("g1", "j1", "log", "x");
        Assert.True(File.Exists(mgr.PathFor("g1", "j1", "log")));
        Assert.True(Directory.Exists(Path.Combine(_paths.LogsDir, "g1")));
    }

    [Fact]
    public async Task DeleteAgentLogs_RemovesTheAgentDirectory()
    {
        var mgr = new LogFileManager(_paths);
        await mgr.AppendAsync("g1", "j1", "log", "x");
        mgr.DeleteAgentLogs("g1");
        Assert.False(Directory.Exists(Path.Combine(_paths.LogsDir, "g1")));
    }

    [Fact]
    public async Task Read_MissingFile_ReturnsEmptyString()
    {
        var mgr = new LogFileManager(_paths);
        Assert.Equal("", await mgr.ReadAsync("nope", "nope", "log"));
    }
}
