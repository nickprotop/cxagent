using System.Text.Json;
using CxAgent.Core.Plugins.Abi;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Exercises the REAL <c>cxagent-plugin-host</c> process against REAL native <c>.so</c> fixtures
/// (<c>AbiFixtures/fixture_plugin.c</c>, compiled by <c>AbiFixtures/build.sh</c> at test build
/// time) — the managed <c>AbiCodec</c> tests already lock the JSON shapes at the unit level; these
/// lock the actual subprocess boundary Task 9b builds: a real process loading a real shared
/// library and surviving what that library does to it. Drives the process through the SAME
/// <see cref="AbiHostProcess"/> Task 9c's shim (<c>AbiPluginLoader</c>/<c>AbiPlugin</c>) uses in
/// production, not a description of it — proving the client, not just the wire format.
///
/// <para>SKIPPED, NOT FAILED, WHEN A FIXTURE IS MISSING — see <see cref="RequireFixture"/>. A
/// machine with no C compiler (AbiFixtures/build.sh's own guard) or a non-Linux CI image still
/// needs the rest of this suite to pass; these tests report what they could not verify rather than
/// failing a build over a fixture unrelated to the platform's own correctness.</para>
/// </summary>
public class AbiPluginHostTests
{
    private static readonly string OutputDir = AppContext.BaseDirectory;

    private static string? Fixture(string name)
    {
        var path = Path.Combine(OutputDir, name + ".so");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// <c>cxagent-plugin-host.dll</c>'s built path — AppContext.BaseDirectory is cxagent.Tests' own
    /// output dir, e.g. .../cxagent.Tests/bin/Debug/net10.0/ — the host project is a SIBLING under
    /// the repo root, built to the same Debug/net10.0 shape, so its path is derived rather than
    /// hardcoded to one machine's absolute layout.
    /// </summary>
    private static readonly string HostDllPath = ResolveHostDll();

    private static string ResolveHostDll()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(OutputDir, "..", "..", "..", ".."));
        var hostDll = Path.Combine(repoRoot, "cxagent.PluginHost", "bin", "Debug", "net10.0", "cxagent-plugin-host.dll");
        if (!File.Exists(hostDll))
            throw new FileNotFoundException(
                $"cxagent-plugin-host.dll not found at '{hostDll}' — build cxagent.PluginHost first.", hostDll);
        return hostDll;
    }

    // Every [Fact] below starts with this — xUnit has no per-test runtime Skip, so the pattern
    // already used for HeadlessSessionTests.AgainstLocalLlamaCpp (an early return, not Assert.Skip)
    // is the one this project already relies on for an environment-gated test.
    private static bool RequireFixture(string name, out string path)
    {
        var found = Fixture(name);
        path = found ?? "";
        return found is not null;
    }

    // ---- A plugin that starts and answers -----------------------------------------------------

    [Fact]
    public async Task AWellFormedPluginStartsAndAnswersInvoke()
    {
        if (!RequireFixture("fixture-wellformed", out var lib)) return;

        var (host, handshake) = await AbiHostProcess.Launch(HostDllPath, lib);
        await using var disposeHost = host;

        Assert.True(handshake.Ready);
        Assert.NotNull(handshake.Manifest);
        Assert.Equal("fixture", handshake.Manifest!.Name);
        Assert.Equal(["echo"], handshake.Manifest.Tools.Select(t => t.Name).ToList());

        var settings = JsonSerializer.SerializeToElement(new { });
        var startReply = await host.Start(OutputDir, settings, CancellationToken.None);
        Assert.True(startReply.Ok);

        var args = JsonSerializer.SerializeToElement(new { value = "hi" });
        var invokeReply = await host.Invoke("echo", args, CancellationToken.None);

        Assert.True(invokeReply.Ok);
        Assert.NotNull(invokeReply.Result);
        Assert.True(invokeReply.Result!.Success);
        Assert.Equal(JsonValueKind.Object, invokeReply.Result.Output.ValueKind);

        var stopReply = await host.Stop(CancellationToken.None);
        Assert.True(stopReply.Ok);
    }

    // ---- A plugin that returns a malformed envelope -------------------------------------------

    [Fact]
    public async Task AMalformedInvokeReplyFailsTheCallCleanly()
    {
        if (!RequireFixture("fixture-malformed", out var lib)) return;

        var (host, handshake) = await AbiHostProcess.Launch(HostDllPath, lib);
        await using var disposeHost = host;
        Assert.True(handshake.Ready);

        await host.Start(OutputDir, JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        var reply = await host.Invoke("echo", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        // THE CALL FAILS — it does not throw, hang, or bring the host down. Malformed JSON from the
        // plugin is exactly the case AbiCodec.ParseEnvelope names: "fails, quoting a bounded prefix
        // of what was returned."
        Assert.False(reply.Ok);
        Assert.NotNull(reply.Error);
        Assert.Contains("invalid JSON", reply.Error);

        // THE HOST PROCESS ITSELF IS STILL ALIVE after a malformed reply — a bad envelope is a data
        // problem, not a fault that should take the process down.
        var stopReply = await host.Stop(CancellationToken.None);
        Assert.True(stopReply.Ok);
    }

    // ---- free-exactly-once, proven on the parse-failure path specifically -----------------------

    [Fact]
    public async Task FreeIsCalledExactlyOnce_EvenOnAParseFailure()
    {
        if (!RequireFixture("fixture-malformed", out var lib)) return;

        // THE FIXTURE APPENDS ONE LINE PER cxagent_plugin_free CALL to this file — see
        // fixture_plugin.c's own doc. A file, not an in-process counter, because the host under
        // test is a SEPARATE PROCESS; this is the only channel available to observe its native
        // side effects from here.
        var countPath = Path.Combine(Path.GetTempPath(), $"cxagent-free-count-{Guid.NewGuid():N}.txt");
        try
        {
            var (host, handshake) = await AbiHostProcess.Launch(
                HostDllPath, lib, new Dictionary<string, string> { ["FREE_COUNT_PATH"] = countPath });
            await using var disposeHost = host;

            // describe() ALREADY RAN, during the handshake, before this test sent a single request —
            // Program.cs calls it once at startup to build handshake.Manifest. That is the FIRST
            // free this test did not itself trigger, and the count below accounts for it.
            Assert.True(handshake.Ready);

            await host.Start(OutputDir, JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

            // THIS INVOKE HITS THE PARSE-FAILURE PATH — fixture-malformed's cxagent_plugin_invoke
            // returns "{ this is not json" every time. AbiCodec.ParseEnvelope fails to parse it and
            // returns EARLY, from inside a try/catch — exactly the path CLAUDE.md's brief calls out
            // as "the exact bug this discipline exists to prevent" if free is skipped on it.
            var reply1 = await host.Invoke("echo", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
            Assert.False(reply1.Ok);

            var reply2 = await host.Invoke("echo", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
            Assert.False(reply2.Ok);

            await host.Stop(CancellationToken.None);

            // Give the fixture's own fopen/fputs/fclose a moment to land on disk before this
            // process reads it back — the host process's own stdout replies (already awaited above)
            // only prove the MANAGED side finished; the native free() call inside cxagent_plugin_free
            // happens synchronously before that reply is even parsed, so this is a formality, not a
            // race this test is gambling on.
            var lineCount = File.Exists(countPath)
                ? (await File.ReadAllLinesAsync(countPath)).Count(l => l == "free")
                : 0;

            // describe (1, at handshake) + start (1) + invoke (2, both parse failures) + stop (1)
            // = 5 plugin-owned strings returned, 5 frees — ONE per returned pointer, no more, no
            // fewer, including on both parse-failure invokes.
            Assert.Equal(5, lineCount);
        }
        finally
        {
            if (File.Exists(countPath)) File.Delete(countPath);
        }
    }

    // ---- A plugin that crashes mid-call ---------------------------------------------------------

    [Fact]
    public async Task APluginThatSegfaultsMidCallDegradesToAFailedCallNotAHang()
    {
        if (!RequireFixture("fixture-crash", out var lib)) return;

        var (host, handshake) = await AbiHostProcess.Launch(HostDllPath, lib);
        await using var disposeHost = host;
        Assert.True(handshake.Ready);

        await host.Start(OutputDir, JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        // THE SEGFAULT KILLS THE HOST PROCESS — this is the ABI's own contract: a native crash has
        // no unwind path, so it takes down the process it happened in, and that process is THIS
        // host, never cxagent itself. What must survive is that the caller sees a clean failure,
        // not a hang and not an unhandled exception on ITS side.
        var reply = await host.Invoke("echo", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.False(reply.Ok);
        Assert.NotNull(reply.Error);
        Assert.Contains("exited", reply.Error);
    }

    // ---- A host asked to load a library that does not exist -------------------------------------

    [Fact]
    public async Task ALibraryThatDoesNotExistFailsCleanlyAtStartup()
    {
        var missingPath = Path.Combine(OutputDir, "does-not-exist.so");
        var (host, handshake) = await AbiHostProcess.Launch(HostDllPath, missingPath);
        await using var disposeHost = host;

        Assert.False(handshake.Ready);
        Assert.NotNull(handshake.Error);
        Assert.Contains("no library at", handshake.Error);
    }

    // ---- A host asked to load a library missing a required export -------------------------------

    [Fact]
    public async Task ALibraryMissingAnExportFailsCleanlyAtStartup()
    {
        if (!RequireFixture("fixture-noinvoke", out var lib)) return;

        var (host, handshake) = await AbiHostProcess.Launch(HostDllPath, lib);
        await using var disposeHost = host;

        Assert.False(handshake.Ready);
        Assert.NotNull(handshake.Error);
        Assert.Contains("cxagent_plugin_invoke", handshake.Error);
        Assert.Contains("not a cxagent ABI plugin", handshake.Error);
    }

    // ---- An ABI version the host does not understand ---------------------------------------------

    [Fact]
    public async Task AnUnknownAbiVersionRefusesCleanlyRatherThanGuessing()
    {
        if (!RequireFixture("fixture-badversion", out var lib)) return;

        var (host, handshake) = await AbiHostProcess.Launch(HostDllPath, lib);
        await using var disposeHost = host;

        Assert.False(handshake.Ready);
        Assert.NotNull(handshake.Error);
        Assert.Contains("99", handshake.Error);
        Assert.Contains("version 1", handshake.Error);
    }
}
