using System.Diagnostics;
using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Plugins.Abi;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Exercises <see cref="AbiPluginLoader"/> and <see cref="AbiPlugin"/> — the shim Task 9c builds:
/// an <see cref="IPlugin"/> backed by a real <c>cxagent-plugin-host</c> subprocess, indistinguishable
/// from a managed plugin to <see cref="PluginRegistry"/>. <see cref="AbiPluginHostTests"/> already
/// locks the subprocess boundary itself; these tests lock the layer above it — the one
/// <see cref="PluginRegistry.Load"/> actually calls.
///
/// <para>SKIPPED, NOT FAILED, WHEN A FIXTURE IS MISSING — see <see cref="RequireFixture"/>, the same
/// pattern <see cref="AbiPluginHostTests"/> already uses for a machine with no C compiler.</para>
/// </summary>
public class AbiPluginLoaderTests
{
    private static readonly string OutputDir = AppContext.BaseDirectory;

    private static string? Fixture(string name)
    {
        var path = Path.Combine(OutputDir, name + ".so");
        return File.Exists(path) ? path : null;
    }

    private static bool RequireFixture(string name, out string path)
    {
        var found = Fixture(name);
        path = found ?? "";
        return found is not null;
    }

    private static readonly string HostDllPath = ResolveHostDll();

    private static string ResolveHostDll()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(OutputDir, "..", "..", "..", ".."));
        // THE CONFIGURATION THIS TEST RUN WAS BUILT IN, not a hardcoded "Debug". These tests pass
        // locally under `dotnet test` (Debug by default) and failed in CI, which runs
        // --configuration Release: the host is a sibling project, so it lands in ITS bin/Release,
        // and a Debug path finds nothing. Read it off this assembly's own output directory rather
        // than guessing — AppContext.BaseDirectory is .../cxagent.Tests/bin/<config>/net10.0/.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var hostDll = Path.Combine(repoRoot, "cxagent.PluginHost", "bin", configuration, "net10.0",
            "cxagent-plugin-host.dll");
        if (!File.Exists(hostDll))
            throw new FileNotFoundException(
                $"cxagent-plugin-host.dll not found at '{hostDll}' — build cxagent.PluginHost first.", hostDll);
        return hostDll;
    }

    private sealed class FakeLogger : IPluginLogger
    {
        public List<string> Lines { get; } = [];
        public void Log(string message) => Lines.Add(message);
    }

    /// <summary>Records every pid this context was asked to register — RegisterChildProcess is the
    /// obligation the brief calls out by name: the host process is itself a child process this
    /// loader must register, exactly as CxagentLspPlugin.Start registers its own language server.</summary>
    private sealed class FakeContext(string workingDirectory) : IPluginContext
    {
        public List<int> RegisteredPids { get; } = [];
        public string WorkingDirectory { get; } = workingDirectory;
        public JsonElement Settings { get; } = JsonSerializer.SerializeToElement(new { });
        public int HostContract => PluginContract.Version;
        public string HostVersion => PluginContract.HostVersionOf(GetType().Assembly);
        public IPluginLogger Logger { get; } = new FakeLogger();
        public CancellationToken Lifetime { get; } = CancellationToken.None;
        public void RegisterChildProcess(int processId) => RegisteredPids.Add(processId);
    }

    private static FakeContext Context() => new(OutputDir);

    // ---- The clean case: load, start, invoke, stop ----------------------------------------------

    /// <summary>
    /// THE GATE CROSSES THE PROCESS BOUNDARY. The fixture decides from the ARGUMENTS — "loud" asks,
    /// anything else does not — which is the case a manifest boolean cannot express, proven here
    /// against a real host process rather than an in-process fake.
    /// </summary>
    [Fact]
    public async Task AnAbiPluginsGateDecidesPerCallFromTheArguments()
    {
        if (!RequireFixture("fixture-wellformed", out var lib)) return;

        var result = await AbiPluginLoader.Load(HostDllPath, lib, Context(), CancellationToken.None);
        var loaded = Assert.IsType<AbiPluginLoadResult.Loaded>(result);
        var source = Assert.IsAssignableFrom<IPluginGateSource>(loaded.Instance);

        try
        {
            Assert.Null(source.Gate("echo_dynamic", new JobParameters(
                new Dictionary<string, object?> { ["text"] = "quiet" })));

            var gate = source.Gate("echo_dynamic", new JobParameters(
                new Dictionary<string, object?> { ["text"] = "loud" }));
            Assert.NotNull(gate);
            Assert.Equal("echo loudly", gate.Display);
        }
        finally
        {
            await loaded.Instance.Stop(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AWellFormedPluginLoadsStartsInvokesAndStops()
    {
        if (!RequireFixture("fixture-wellformed", out var lib)) return;

        var context = Context();
        var result = await AbiPluginLoader.Load(HostDllPath, lib, context, CancellationToken.None);

        var loaded = Assert.IsType<AbiPluginLoadResult.Loaded>(result);
        Assert.Equal("fixture", loaded.Manifest.Name);
        Assert.Equal(["echo", "echo_dynamic"], loaded.Manifest.Tools.Select(t => t.Name).ToList());

        // THE HOST PROCESS ITSELF IS A REGISTERED CHILD — the brief's own requirement: "the host
        // process is itself a child process that must be reaped if this session crashes."
        Assert.Single(context.RegisteredPids);

        var plugin = loaded.Instance;
        await plugin.Start(CancellationToken.None);

        var jobResult = await plugin.Invoke("echo",
            new JobParameters(new Dictionary<string, object?> { ["value"] = "hi" }),
            FakeJobContext(), CancellationToken.None);

        Assert.True(jobResult.Success);

        await plugin.Stop(CancellationToken.None);
    }

    /// <summary>
    /// THE SAME REFUSAL THE MANAGED LOADER MAKES, and at the same point: read from the sidecar
    /// before a host process is spawned or the library mapped. Two loaders refusing one manifest
    /// for different reasons, or at different costs, would be two behaviours where the file
    /// describes one.
    /// </summary>
    [Theory]
    [InlineData("""{"name":"fixture","version":"1.0.0","spawns":false,"tools":[]}""", "no 'pluginContract'")]
    [InlineData("""{"pluginContract":1,"name":"fixture","version":"1.0.0","spawns":false,"tools":[]}""", "contract 1")]
    public async Task ASidecarThisBuildCannotVouchForIsRefusedBeforeAnythingIsSpawned(
        string manifest, string expected)
    {
        if (!RequireFixture("fixture-wellformed", out var lib)) return;

        var copy = Path.Combine(OutputDir, $"fixture-wellformed-contract-{Guid.NewGuid():N}.so");
        File.Copy(lib, copy);
        var sidecarPath = Path.ChangeExtension(copy, null) + ".plugin.json";
        await File.WriteAllTextAsync(sidecarPath, manifest);
        try
        {
            var result = await AbiPluginLoader.Load(HostDllPath, copy, Context(), CancellationToken.None);

            var failed = Assert.IsType<AbiPluginLoadResult.Failed>(result);
            Assert.Contains(expected, failed.Reason);
        }
        finally
        {
            File.Delete(sidecarPath);
            File.Delete(copy);
        }
    }

    // ---- Sidecar / describe mismatch --------------------------------------------------------------

    [Fact]
    public async Task ASidecarMismatchFailsAndNamesTheDifference()
    {
        if (!RequireFixture("fixture-wellformed", out var lib)) return;

        // A SIDECAR THAT DISAGREES WITH THE REAL fixture-wellformed.so DESCRIBE — written beside a
        // COPY of the library so the real sidecar (needed by the other tests sharing this output
        // directory) is never disturbed.
        var copy = Path.Combine(OutputDir, $"fixture-wellformed-mismatch-{Guid.NewGuid():N}.so");
        File.Copy(lib, copy);
        var sidecarPath = Path.ChangeExtension(copy, null) + ".plugin.json";
        await File.WriteAllTextAsync(sidecarPath,
            """{"pluginContract":2,"name":"fixture","version":"1.0.0","spawns":false,"tools":[{"name":"different_tool","description":"d","inputSchema":{"type":"object"},"gated":false}]}""");
        try
        {
            var result = await AbiPluginLoader.Load(HostDllPath, copy, Context(), CancellationToken.None);

            var failed = Assert.IsType<AbiPluginLoadResult.Failed>(result);
            Assert.Contains("does not match its sidecar manifest", failed.Reason);
            Assert.Contains("different_tool", failed.Reason);
        }
        finally
        {
            File.Delete(copy);
            File.Delete(sidecarPath);
        }
    }

    // ---- Missing sidecar --------------------------------------------------------------------------

    [Fact]
    public async Task AMissingSidecarFails()
    {
        if (!RequireFixture("fixture-wellformed", out var lib)) return;

        var copy = Path.Combine(OutputDir, $"fixture-wellformed-nosidecar-{Guid.NewGuid():N}.so");
        File.Copy(lib, copy);
        try
        {
            var result = await AbiPluginLoader.Load(HostDllPath, copy, Context(), CancellationToken.None);

            var failed = Assert.IsType<AbiPluginLoadResult.Failed>(result);
            Assert.Contains("no sidecar manifest at", failed.Reason);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    // ---- A library that does not exist --------------------------------------------------------------

    [Fact]
    public async Task ANonexistentLibraryFails()
    {
        var missing = Path.Combine(OutputDir, "does-not-exist.so");
        var result = await AbiPluginLoader.Load(HostDllPath, missing, Context(), CancellationToken.None);

        var failed = Assert.IsType<AbiPluginLoadResult.Failed>(result);
        Assert.Contains("no plugin library at", failed.Reason);
    }

    // ---- THE HARD REQUIREMENT: a host killed BETWEEN calls degrades to a failed call ---------------

    /// <summary>
    /// Task 9's brief, verbatim: "The host process can die at any moment: crashed native code, an
    /// OOM kill, a user's kill -9. The shim must degrade to failed calls with a clear message, never
    /// a hang and never an exception escaping into the agent loop." AbiPluginHostTests already
    /// proves a crash INSIDE a call degrades correctly (FIXTURE_CRASH, a real segfault); this proves
    /// the shim survives the host dying BETWEEN calls too — a live process one moment, killed with
    /// SIGKILL the next, with NOTHING inside this plugin's own control causing it.
    /// </summary>
    [Fact]
    public async Task AHostKilledBetweenCallsDegradesToAFailedCallNotAHangOrException()
    {
        if (!RequireFixture("fixture-wellformed", out var lib)) return;

        var context = Context();
        var result = await AbiPluginLoader.Load(HostDllPath, lib, context, CancellationToken.None);
        var loaded = Assert.IsType<AbiPluginLoadResult.Loaded>(result);
        var plugin = loaded.Instance;
        await plugin.Start(CancellationToken.None);

        // A NORMAL CALL FIRST, to prove the host actually answers before it is killed — otherwise a
        // failure below would prove nothing about "between calls" specifically.
        var beforeKill = await plugin.Invoke("echo",
            new JobParameters(new Dictionary<string, object?> { ["value"] = "hi" }),
            FakeJobContext(), CancellationToken.None);
        Assert.True(beforeKill.Success);

        // KILL -9, THE EXACT FAILURE MODE THE BRIEF NAMES — SIGKILL, not a graceful Stop, and not a
        // crash inside a call: the process is simply gone by the time the next Invoke is sent.
        var hostPid = context.RegisteredPids.Single();
        var hostProcess = Process.GetProcessById(hostPid);
        hostProcess.Kill(entireProcessTree: true);
        hostProcess.WaitForExit(5000);

        // THE NEXT CALL MUST FAIL CLEANLY — no hang (this whole test would time out under the 20s
        // suite budget if it did), and no exception escaping this Invoke to the caller, exactly the
        // requirement's own wording: "never a hang and never an exception escaping into the agent loop."
        var afterKill = await plugin.Invoke("echo",
            new JobParameters(new Dictionary<string, object?> { ["value"] = "hi" }),
            FakeJobContext(), CancellationToken.None);

        Assert.False(afterKill.Success);
        Assert.NotNull(afterKill.ErrorMessage);

        // STOP MUST ALSO DEGRADE CLEANLY on an already-dead host — never throw for a plugin whose
        // process is already gone, matching PluginRegistry.UnwireAsync's own tolerance for a plugin
        // that cannot clean up after itself.
        await plugin.Stop(CancellationToken.None);
    }

    private static IJobContext FakeJobContext() => new TestJobContext();
}
