using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// TWO LIVE SESSIONS IN ONE PROCESS, on two folders, running at the same time.
///
/// <para>This is the end of the isolation work rather than a step in it: it does what the app
/// cannot yet do from its UI, using only the seams the core already exposes. AgentHost takes
/// ISessionObserver and IToolObserver, and BufferedChatSink/BufferedJobPanel are real non-UI implementations —
/// so a headless second session needs nothing new. What is singular is MainWindow, not the
/// kernel.</para>
///
/// <para>WHY IT IS WORTH A TEST NOW rather than when a second session ships: every guarantee below
/// is one a later change could quietly break — a static reintroduced, a store keyed by the wrong
/// thing, a working directory read from the process again. Failing here names the regression
/// precisely; discovering it when the UI finally supports tabs would not.</para>
/// </summary>
public class TwoLiveSessionsTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-live2-" + Guid.NewGuid().ToString("N"));
    private readonly string _a;
    private readonly string _b;

    public TwoLiveSessionsTests()
    {
        _a = Path.Combine(_dir, "project-a");
        _b = Path.Combine(_dir, "project-b");
        Directory.CreateDirectory(_a);
        Directory.CreateDirectory(_b);
        File.WriteAllText(Path.Combine(_a, "which.txt"), "I am project A");
        File.WriteAllText(Path.Combine(_b, "which.txt"), "I am project B");
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private AppPaths Paths() => new(Path.Combine(_dir, "config"));

    /// <summary>A host rooted in one folder, with everything else shared exactly as the design says
    /// it may be.</summary>
    private (AgentHost Host, BufferedChatSink Sink, BufferedJobPanel Jobs) Build(
        string workingDir, string instance, MockLlmProvider provider, AppPaths paths)
    {
        var sink = new BufferedChatSink();
        var jobs = new BufferedJobPanel();
        var host = new AgentHost(
            new AgentHost.AgentRuntime
            {
                Provider = provider,
                Plugins = PluginRegistry.CreateWithBuiltins(),
                InstanceName = instance,
                WorkingDir = workingDir,
                Mode = AgentMode.Single,
            },
            sink,
            jobs,
            new AgentHost.SessionStores
            {
                // SHARED ON PURPOSE — see TwoSessionsTests for why splitting these would break the
                // features that depend on the sharing.
                Resume = new SqliteSessionStore(paths),
                History = new UsageHistoryStore(paths),
                Logs = new LogFileManager(paths),
            });
        return (host, sink, jobs);
    }

    private static LlmResponse ReadWhich() =>
        LlmResponse.WithToolCall("read_file", new { path = "which.txt" });

    private static LlmResponse Done(string text) =>
        new() { Text = text, StopReason = "end_turn", Usage = new LlmUsage { InputTokens = 10, OutputTokens = 2 } };

    /// <summary>
    /// THE WHOLE POINT: the same relative path, read by two sessions at the same time, reaches two
    /// different files. Before the working directory became session state this resolved against the
    /// PROCESS directory, so both would have read the same file — or neither.
    /// </summary>
    [Fact]
    public async Task TwoSessions_ResolveTheSameRelativePathToTheirOwnFile()
    {
        var paths = Paths();

        var providerA = new MockLlmProvider();
        providerA.EnqueueResponse(ReadWhich());
        providerA.EnqueueResponse(Done("read A"));

        var providerB = new MockLlmProvider();
        providerB.EnqueueResponse(ReadWhich());
        providerB.EnqueueResponse(Done("read B"));

        var (hostA, _, jobsA) = Build(_a, "local", providerA, paths);
        var (hostB, _, jobsB) = Build(_b, "small", providerB, paths);

        using (hostA)
        using (hostB)
        {
            // CONCURRENTLY, not one after the other — the interesting failures are the shared ones.
            await Task.WhenAll(
                hostA.SendAsync("what am I?", CancellationToken.None),
                hostB.SendAsync("what am I?", CancellationToken.None));

            // THE TOOL RESULT, not the transcript: the transcript carries the model's reply, and
            // what this test is about is which FILE the read reached.
            static string Read(BufferedJobPanel jobs) => string.Join("\n",
                jobs.Jobs.Select(j => j.Result?.Output.TryGetValue("content", out var c) == true
                    ? c?.ToString() ?? "" : ""));

            var textA = Read(jobsA);
            var textB = Read(jobsB);

            Assert.Contains("I am project A", textA, StringComparison.Ordinal);
            Assert.DoesNotContain("I am project B", textA, StringComparison.Ordinal);

            Assert.Contains("I am project B", textB, StringComparison.Ordinal);
            Assert.DoesNotContain("I am project A", textB, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// SEPARATE CONVERSATIONS AND SEPARATE LEDGERS. Two sessions sharing a ledger would report one
    /// session's spend as the other's, and sharing a context is the failure the whole sub-agent
    /// design exists to prevent — reached here by a different route.
    /// </summary>
    [Fact]
    public async Task TwoSessions_KeepSeparateContextsAndLedgers()
    {
        var paths = Paths();

        var providerA = new MockLlmProvider();
        providerA.EnqueueResponse(Done("A"));
        var providerB = new MockLlmProvider();
        providerB.EnqueueResponse(Done("B"));
        providerB.EnqueueResponse(Done("B again"));

        var (hostA, _, _) = Build(_a, "local", providerA, paths);
        var (hostB, _, _) = Build(_b, "small", providerB, paths);

        using (hostA)
        using (hostB)
        {
            await hostA.SendAsync("one", CancellationToken.None);
            await hostB.SendAsync("one", CancellationToken.None);
            await hostB.SendAsync("two", CancellationToken.None);

            Assert.NotSame(hostA.Ledger, hostB.Ledger);
            Assert.NotSame(hostA.Context, hostB.Context);
            Assert.NotEqual(hostA.SessionId, hostB.SessionId);

            // B ran twice, A once — so the ledgers cannot be the same object by accident.
            Assert.True(hostB.Ledger.TotalTokens > hostA.Ledger.TotalTokens);
        }
    }

    /// <summary>
    /// THE SHARED HISTORY DATABASE SEPARATES THEM BY FOLDER AND BY INSTANCE. Both sessions write to
    /// one file — that is what makes /stats span sessions — and each row must still say which folder
    /// and which endpoint it came from.
    /// </summary>
    [Fact]
    public async Task TwoSessions_WriteDistinguishableHistoryRows()
    {
        var paths = Paths();

        var providerA = new MockLlmProvider("qwen3");
        providerA.EnqueueResponse(Done("A"));
        var providerB = new MockLlmProvider("qwen3");   // the SAME model, a different instance
        providerB.EnqueueResponse(Done("B"));

        var (hostA, _, _) = Build(_a, "local", providerA, paths);
        var (hostB, _, _) = Build(_b, "small", providerB, paths);

        using (hostA) using (hostB)
        {
            await hostA.SendAsync("one", CancellationToken.None);
            await hostB.SendAsync("one", CancellationToken.None);
        }

        var rows = new UsageHistoryStore(paths).SessionsSince(DateTimeOffset.UtcNow.AddMinutes(-5));

        // instance:model, NOT the bare model — two entries can serve one model, and this is exactly
        // the case that makes the distinction load-bearing rather than pedantic.
        Assert.Contains(rows, r => r.ModelId == "local:qwen3" && r.WorkingDir == _a);
        Assert.Contains(rows, r => r.ModelId == "small:qwen3" && r.WorkingDir == _b);
    }
}
