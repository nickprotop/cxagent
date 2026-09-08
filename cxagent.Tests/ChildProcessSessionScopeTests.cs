using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Whose children an unwire reaps.
///
/// <para><b>A PLUGIN IS LOADED PER SESSION</b> — each gets its own instance and its own spawned
/// processes — and <c>ReapPlugin</c> matched on the plugin NAME alone. So `/plugin unwire csharp-lsp`
/// in the third tab killed the language servers of the first two, which went on advertising three LSP
/// tools backed by nothing.</para>
///
/// <para>Drive-verified before the fix: three sessions, three `csharp-ls` processes, one unwire, zero
/// left — and the other two sessions still reporting the plugin loaded.</para>
/// </summary>
public class ChildProcessSessionScopeTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-children-" + Guid.NewGuid().ToString("N"));

    private readonly ChildProcessStore _store;

    public ChildProcessSessionScopeTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ChildProcessStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// A pid that is not running, recorded for one session.
    /// </summary>
    /// <remarks>
    /// A DEAD PID IS ENOUGH TO TEST THE MATCHING. Kill checks the recorded start time against the
    /// live process before killing anything, so a stale record is skipped — what this exercises is
    /// which records are SELECTED and which survive in the file, not the killing itself.
    /// </remarks>
    /// <summary>
    /// What the store has left, read from its file.
    /// </summary>
    /// <remarks>
    /// THE FILE IS THE STATE — the store exposes no reader, deliberately, since it exists to survive
    /// a crash. Reading it here tests the persisted shape too: a `Session` that did not round-trip
    /// through JSON would leave every record looking sessionless and reap them all.
    /// </remarks>
    private List<ChildProcessRecord> Remaining()
    {
        var path = Path.Combine(_dir, "plugin-children.json");
        if (!File.Exists(path)) return [];

        return System.Text.Json.JsonSerializer.Deserialize<List<ChildProcessRecord>>(
            File.ReadAllText(path)) ?? [];
    }

    private void Record(string plugin, string? session) =>
        _store.Add(new ChildProcessRecord(999_000 + Random.Shared.Next(999),
            DateTime.UtcNow.AddMinutes(-5), plugin, session));

    /// <summary>UNWIRING IN ONE SESSION LEAVES THE OTHER'S RECORD ALONE.</summary>
    [Fact]
    public void ReapingOneSessionsPluginKeepsAnothersChildren()
    {
        Record("csharp-lsp", "session-alpha");
        Record("csharp-lsp", "session-beta");

        _store.ReapPlugin("csharp-lsp", _ => { }, sessionId: "session-alpha");

        var left = Remaining();
        Assert.Single(left);
        Assert.Equal("session-beta", left[0].Session);
    }

    /// <summary>
    /// AND NAMING NO SESSION REAPS EVERY SESSION'S — which is what process shutdown wants, and what a
    /// caller must ask for explicitly rather than get by omission.
    /// </summary>
    [Fact]
    public void ReapingWithNoSessionTakesThemAll()
    {
        Record("csharp-lsp", "session-alpha");
        Record("csharp-lsp", "session-beta");

        _store.ReapPlugin("csharp-lsp", _ => { });

        Assert.Empty(Remaining());
    }

    /// <summary>
    /// A RECORD WITH NO SESSION IS REAPED WHOEVER ASKS. It was written before the field existed, or
    /// by a run that has since died — and leaving a previous crash's children running is the failure
    /// this store exists to prevent.
    /// </summary>
    [Fact]
    public void ARecordWithNoSessionIsAlwaysReaped()
    {
        Record("csharp-lsp", session: null);
        Record("csharp-lsp", "session-beta");

        _store.ReapPlugin("csharp-lsp", _ => { }, sessionId: "session-alpha");

        var left = Remaining();
        Assert.Single(left);
        Assert.Equal("session-beta", left[0].Session);
    }

    /// <summary>AND ANOTHER PLUGIN'S CHILDREN ARE NEVER TOUCHED, whatever the session.</summary>
    [Fact]
    public void AnotherPluginsChildrenSurvive()
    {
        Record("csharp-lsp", "session-alpha");
        Record("clone-finder", "session-alpha");

        _store.ReapPlugin("csharp-lsp", _ => { }, sessionId: "session-alpha");

        var left = Remaining();
        Assert.Single(left);
        Assert.Equal("clone-finder", left[0].Plugin);
    }
}
