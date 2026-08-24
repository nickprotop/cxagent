using System.Diagnostics;
using System.Text.Json;
using CxAgent.Core.Plugins;
using CxAgent.PluginHost;

namespace CxAgent.Tests;

/// <summary>
/// Drives one <c>cxagent-plugin-host</c> subprocess exactly as Task 9c's managed shim will —
/// launch it with a native library path on argv, read its startup line, then send newline-JSON
/// requests and match replies by id. Lives in the test project rather than production code because
/// 9c (a separate dispatch) owns building the real, permanent version of this client; this one
/// exists only so AbiPluginHostTests can exercise the ACTUAL host process rather than trust its
/// wire format from the managed side alone.
/// </summary>
public sealed class AbiHostProcess : IAsyncDisposable
{
    private readonly Process _process;
    private long _nextId;

    private AbiHostProcess(Process process)
    {
        _process = process;
    }

    /// <summary>What Program.cs wrote before its first request line — success with a manifest, or a
    /// named startup failure. See <see cref="HostReady"/>/<see cref="HostStartupFailure"/>.</summary>
    public sealed record StartResult(bool Ready, PluginManifest? Manifest, string? Error);

    /// <summary>
    /// Launches the host against <paramref name="libraryPath"/> and reads its one startup line.
    ///
    /// <para><c>dotnet &lt;cxagent-plugin-host.dll&gt;</c>, NOT THE NATIVE APPHOST — a
    /// ProjectReference guarantees the framework-dependent .dll exists at a known path regardless of
    /// which RID this machine restored an apphost for, and "launch a .NET exe" is exactly what
    /// PluginRegistry's real caller (9c) will also do, so nothing about how this test spawns the
    /// process is a shortcut around what production does.</para>
    /// </summary>
    public static async Task<(AbiHostProcess Process, StartResult Handshake)> Launch(
        string libraryPath, IReadOnlyDictionary<string, string>? environment = null)
    {
        var hostDll = ResolveHostDll();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { hostDll, libraryPath },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        var host = new AbiHostProcess(process);

        var line = await process.StandardOutput.ReadLineAsync()
            ?? throw new InvalidOperationException(
                "host process closed stdout before writing a startup line — stderr: "
                + await process.StandardError.ReadToEndAsync());

        using var doc = JsonDocument.Parse(line);
        var ready = doc.RootElement.GetProperty("ready").GetBoolean();
        if (!ready)
        {
            var error = doc.RootElement.GetProperty("error").GetString();
            return (host, new StartResult(false, null, error));
        }

        var manifest = JsonSerializer.Deserialize<PluginManifest>(doc.RootElement.GetProperty("manifest").GetRawText());
        return (host, new StartResult(true, manifest, null));
    }

    private static string ResolveHostDll()
    {
        // AppContext.BaseDirectory is cxagent.Tests' own output dir, e.g.
        // .../cxagent.Tests/bin/Debug/net10.0/ — the host project is a SIBLING under the repo root,
        // built to the same Debug/net10.0 shape, so its path is derived rather than hardcoded to
        // one machine's absolute layout.
        var testsOutputDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(testsOutputDir, "..", "..", "..", ".."));
        var hostDll = Path.Combine(repoRoot, "cxagent.PluginHost", "bin", "Debug", "net10.0", "cxagent-plugin-host.dll");
        if (!File.Exists(hostDll))
            throw new FileNotFoundException(
                $"cxagent-plugin-host.dll not found at '{hostDll}' — build cxagent.PluginHost first.", hostDll);
        return hostDll;
    }

    /// <summary>Sends a <c>start</c> request and awaits its reply, with a timeout — a host that
    /// never replies (its plugin hung, or its process died without closing stdout cleanly) must not
    /// hang the test suite; see this method's <paramref name="timeout"/>.</summary>
    public Task<HostReply> Start(string workingDirectory, JsonElement settings, TimeSpan? timeout = null) =>
        Send(HostProtocol.RequestKind.Start, null,
            JsonSerializer.SerializeToElement(new { workingDirectory, settings }), timeout);

    /// <summary>Sends an <c>invoke</c> request for <paramref name="toolName"/> and awaits its reply.</summary>
    public Task<HostReply> Invoke(string toolName, JsonElement arguments, TimeSpan? timeout = null) =>
        Send(HostProtocol.RequestKind.Invoke, toolName, arguments, timeout);

    /// <summary>Sends a <c>stop</c> request and awaits its reply.</summary>
    public Task<HostReply> Stop(TimeSpan? timeout = null) =>
        Send(HostProtocol.RequestKind.Stop, null, null, timeout);

    private async Task<HostReply> Send(HostProtocol.RequestKind kind, string? toolName, JsonElement? arguments,
        TimeSpan? timeout)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new HostRequest(id, kind, toolName, arguments);
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
        await _process.StandardInput.FlushAsync();

        var readTask = _process.StandardOutput.ReadLineAsync();
        var completed = await Task.WhenAny(readTask, Task.Delay(timeout ?? TimeSpan.FromSeconds(10)));

        if (completed != readTask)
        {
            // THIS IS THE MANAGED CONTRACT'S CANCELLATION, REPRODUCED AT THE TEST SEAM — Abi/README.md,
            // "Cancellation": the caller stops WAITING and reports failure; it does not attempt to
            // signal the native call. A real caller (9c) does the identical thing on its own
            // CancellationToken; this test-only client does it on a bare timeout because it has no
            // token to observe, and the behaviour under test is "the wait was abandoned," not "a
            // token fired."
            return new HostReply(id, false, null, "timed out waiting for host reply — call abandoned.");
        }

        var line = await readTask;
        if (line is null)
        {
            // STDOUT CLOSED WITH NO REPLY — the host process died (a crash, most commonly) before
            // answering this request. Exactly the failure mode a crashing plugin must degrade to:
            // a failed call, not a hung test or a propagated exception.
            var stderr = await _process.StandardError.ReadToEndAsync();
            return new HostReply(id, false, null,
                $"host process exited without a reply (exit code {SafeExitCode()}) — stderr: {stderr}");
        }

        var reply = JsonSerializer.Deserialize<HostReply>(line)
            ?? throw new InvalidOperationException($"host wrote a reply line that parsed to null: '{line}'");
        return reply;
    }

    private int? SafeExitCode()
    {
        try { return _process.HasExited ? _process.ExitCode : null; }
        catch (Exception) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(2000))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best-effort teardown — a process that raced its own exit between HasExited and Kill
            // is not this disposal's to fail over, the same tolerance ChildProcessStore.Kill applies.
        }
        finally
        {
            _process.Dispose();
        }
        await Task.CompletedTask;
    }
}
