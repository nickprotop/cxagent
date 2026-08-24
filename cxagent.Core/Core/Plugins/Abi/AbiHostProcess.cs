using System.Diagnostics;
using System.Text.Json;

namespace CxAgent.Core.Plugins.Abi;

/// <summary>
/// Drives one <c>cxagent-plugin-host</c> subprocess over its newline-JSON wire protocol — launches
/// it against a native library path, reads its one startup line, then sends requests and matches
/// replies by id. THE ONLY THING THAT SPEAKS <c>HostProtocol</c> ON THE MANAGED SIDE: <see
/// cref="AbiPlugin"/> holds one of these per plugin instance and never touches stdin/stdout itself,
/// the same one-process-per-plugin split <c>NativePlugin</c> keeps between the wire and the ABI.
///
/// <para>A REQUEST NEVER HANGS PAST ITS CALLER'S OWN CANCELLATION. Every send/read is abandoned,
/// not cancelled, when its <see cref="CancellationToken"/> fires (see <see cref="Send"/>'s own
/// doc), and a dead process (exited, stdout closed, a broken pipe) is read back as a failed
/// <see cref="HostReply"/> rather than a hang or an exception — the whole reason this type exists
/// is to make "the host died" indistinguishable, from <see cref="AbiPlugin"/>'s point of view, from
/// "the host answered ok:false."</para>
/// </summary>
internal sealed class AbiHostProcess : IAsyncDisposable
{
    private readonly Process _process;
    private long _nextId;

    private AbiHostProcess(Process process)
    {
        _process = process;
    }

    /// <summary>The process id of the running host — <see cref="AbiPluginLoader"/> registers this
    /// with <see cref="IPluginContext.RegisterChildProcess"/> the moment it is known, before the
    /// handshake even completes: a host that dies partway through its own startup still leaves a
    /// process that needs reaping.</summary>
    public int ProcessId => _process.Id;

    /// <summary>What the host wrote before its first request line — success with a manifest, or a
    /// named startup failure. See <see cref="HostReady"/>/<see cref="HostStartupFailure"/>.</summary>
    public sealed record StartResult(bool Ready, PluginManifest? Manifest, string? Error);

    /// <summary>
    /// Launches the host executable at <paramref name="hostDllPath"/> against
    /// <paramref name="libraryPath"/> and reads its one startup line.
    ///
    /// <para><c>dotnet &lt;cxagent-plugin-host.dll&gt;</c>, NOT A NATIVE APPHOST — a
    /// framework-dependent launch works regardless of which RID this machine restored an apphost
    /// for, matching how the host is built (see <c>cxagent.PluginHost.csproj</c>).</para>
    /// </summary>
    /// <param name="hostDllPath">Path to <c>cxagent-plugin-host.dll</c>.</param>
    /// <param name="libraryPath">Path to the native library the host should load.</param>
    /// <param name="environment">Extra environment variables for the host process — production
    /// callers never need this; it exists for a test fixture that reads its own knobs from the
    /// environment (see <c>AbiPluginHostTests</c>'s <c>FREE_COUNT_PATH</c>), which is otherwise the
    /// only channel available to observe a separate process's native side effects.</param>
    public static async Task<(AbiHostProcess Process, StartResult Handshake)> Launch(
        string hostDllPath, string libraryPath, IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { hostDllPath, libraryPath },
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

        string? line;
        try
        {
            line = await process.StandardOutput.ReadLineAsync();
        }
        catch (InvalidOperationException)
        {
            // THE PROCESS NEVER STARTED READABLY — e.g. `dotnet` itself is missing. Reported the
            // same way a startup line saying so would be, rather than letting the exception escape
            // to a caller that only expects a StartResult.
            line = null;
        }

        if (line is null)
        {
            var stderr = await TryReadStderr(process);
            return (host, new StartResult(false, null,
                $"host process closed stdout before writing a startup line (exit code {SafeExitCode(process)})"
                + (string.IsNullOrEmpty(stderr) ? "." : $" — stderr: {stderr}")));
        }

        HostReady? ready;
        try
        {
            ready = JsonSerializer.Deserialize<HostReady>(line);
        }
        catch (JsonException ex)
        {
            return (host, new StartResult(false, null, $"host wrote an unparseable startup line: {ex.Message}"));
        }

        if (ready is null)
            return (host, new StartResult(false, null, "host wrote a startup line that parsed to null."));

        if (!ready.Ready)
        {
            // A HostReady WITH Ready:false NEVER HAPPENS FROM Program.cs (it writes
            // HostStartupFailure instead), but a malformed or hand-crafted line could still set the
            // field this way — read defensively rather than assuming the two record shapes are
            // distinguishable from `ready` alone.
            using var doc = JsonDocument.Parse(line);
            var error = doc.RootElement.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
            return (host, new StartResult(false, null, error ?? "host reported a startup failure with no reason given."));
        }

        return (host, new StartResult(true, ready.Manifest, null));
    }

    /// <summary>Sends a <c>start</c> request and awaits its reply.</summary>
    public Task<HostReply> Start(string workingDirectory, JsonElement settings, CancellationToken ct) =>
        Send(HostProtocol.RequestKind.Start, null,
            JsonSerializer.SerializeToElement(new { workingDirectory, settings }), ct);

    /// <summary>Sends an <c>invoke</c> request for <paramref name="toolName"/> and awaits its reply.</summary>
    public Task<HostReply> Invoke(string toolName, JsonElement arguments, CancellationToken ct) =>
        Send(HostProtocol.RequestKind.Invoke, toolName, arguments, ct);

    /// <summary>Sends a <c>stop</c> request and awaits its reply.</summary>
    public Task<HostReply> Stop(CancellationToken ct) =>
        Send(HostProtocol.RequestKind.Stop, null, null, ct);

    /// <summary>
    /// Sends one request and waits for its reply, or gives up — never hangs and never throws past
    /// this method. Three ways this can end besides an ordinary reply:
    ///
    /// <para>1. <paramref name="ct"/> FIRES WHILE WAITING — Abi/README.md, "Cancellation": the wait
    /// is ABANDONED, not the native call cancelled. This method stops awaiting the read and returns
    /// a failure immediately; whatever the host eventually writes for this id is left unread on the
    /// pipe (or consumed and discarded by a later read racing ahead of it — either is fine, because
    /// nothing is still waiting on this id specifically).</para>
    ///
    /// <para>2. THE WRITE ITSELF FAILS — the host died between the last successful read and this
    /// send (a killed process, a broken pipe). <see cref="IOException"/>/<see cref="ObjectDisposedException"/>
    /// from the write are caught here, not left to propagate into <see cref="AbiPlugin"/> as an
    /// unhandled exception — exactly the "killed host BETWEEN calls" case this task's test proves.</para>
    ///
    /// <para>3. THE READ RETURNS NULL — stdout closed with no reply: the host exited (crashed, or
    /// was killed) after accepting the write but before answering. Reported naming the exit code and
    /// stderr, the same shape a crash INSIDE the call already produces via the ABI envelope.</para>
    /// </summary>
    private async Task<HostReply> Send(HostProtocol.RequestKind kind, string? toolName,
        JsonElement? arguments, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new HostRequest(id, kind, toolName, arguments);

        try
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
            await _process.StandardInput.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return new HostReply(id, false, null,
                $"could not send to plugin host process (it may have died): {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return new HostReply(id, false, null, "call abandoned: cancelled before the host could be sent the request.");
        }

        Task<string?> readTask;
        try
        {
            readTask = _process.StandardOutput.ReadLineAsync(ct).AsTask();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return new HostReply(id, false, null,
                $"could not read from plugin host process (it may have died): {ex.Message}");
        }

        string? line;
        try
        {
            line = await readTask;
        }
        catch (OperationCanceledException)
        {
            // ABANDONED, NOT CANCELLED — see this method's own doc, point 1. The read keeps running
            // in the background against the process's actual stdout; this call simply stops waiting
            // on it, matching PluginRegistry.UnwireAsync's own "the await here is abandoned, not
            // cancelled" language for a managed plugin's hung Stop.
            return new HostReply(id, false, null, "call abandoned: cancelled while waiting for the plugin host's reply.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return new HostReply(id, false, null,
                $"lost connection to plugin host process while waiting for a reply: {ex.Message}");
        }

        if (line is null)
        {
            var stderr = await TryReadStderr(_process);
            return new HostReply(id, false, null,
                $"plugin host process exited without a reply (exit code {SafeExitCode(_process)})"
                + (string.IsNullOrEmpty(stderr) ? "." : $" — stderr: {stderr}"));
        }

        HostReply? reply;
        try
        {
            reply = JsonSerializer.Deserialize<HostReply>(line);
        }
        catch (JsonException ex)
        {
            return new HostReply(id, false, null, $"plugin host wrote an unparseable reply: {ex.Message}");
        }

        return reply ?? new HostReply(id, false, null, $"plugin host wrote a reply line that parsed to null: '{line}'");
    }

    /// <summary>Best-effort stderr capture for an error message — never throws, because a process
    /// that has already died in some unusual way is not this diagnostic's to fail over.</summary>
    private static async Task<string> TryReadStderr(Process process)
    {
        try
        {
            return await process.StandardError.ReadToEndAsync();
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch (Exception) { return null; }
    }

    /// <summary>Kills the process if it is still alive — the last resort a caller reaches only when
    /// <see cref="Stop"/>'s own reply already ran and the process outlived it, or when this instance
    /// is being torn down without ever having started cleanly. Ordinary shutdown goes through
    /// <see cref="Stop"/> first; this does not send it.</summary>
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
