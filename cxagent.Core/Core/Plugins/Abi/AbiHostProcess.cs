using System.Collections.Concurrent;
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

    // OUTSTANDING CALLS, KEYED BY THE ID THIS INSTANCE ASSIGNED THEM. HostProtocol documents that
    // replies MAY arrive out of order — cxagent_plugin_invoke "MAY BE CALLED CONCURRENTLY" and the
    // host does not serialise invokes onto one at a time — so a single shared read loop below
    // matches each line back to its own waiter rather than assuming the Nth line answers the Nth
    // call.
    private readonly ConcurrentDictionary<long, TaskCompletionSource<HostReply>> _outstanding = new();

    // ONE READER FOR THE LIFETIME OF THE PROCESS, not one per call — two concurrent Sends used to
    // race their own ReadLineAsync against the same StandardOutput, which StreamReader forbids
    // ("stream is currently in use") and which would let one call's line satisfy the other's read
    // even if it didn't throw. Assigned by StartReadLoop, called from Launch only AFTER the startup
    // line is already consumed off the same stream by a plain ReadLineAsync — starting it any
    // earlier would race that first read for the same bytes.
    private Task _readLoop = Task.CompletedTask;

    // THE HOST PROCESS ALREADY SERIALISES ITS OWN REPLY WRITES, and its own comment says why: two
    // replies racing to write could interleave their bytes mid-line and hand the parent a line
    // neither JSON. The parent side has the same hazard in the other direction — two Sends writing
    // a request line concurrently — so it gets the same discipline.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private AbiHostProcess(Process process)
    {
        _process = process;
    }

    /// <summary>Starts the background reply reader — called once, from <see cref="Launch"/>, only
    /// after the one startup line has already been read off <c>StandardOutput</c> directly. Every
    /// line the loop itself reads is therefore a reply to a request this instance sent, never the
    /// handshake line.</summary>
    private void StartReadLoop()
    {
        _readLoop = Task.Run(ReadLoop);
    }

    /// <summary>
    /// Reads reply lines until the stream ends, completing each line's matching waiter. A line
    /// whose id matches nothing outstanding is LOGGED AND SKIPPED, not fatal — the id this instance
    /// once assigned to a call whose <see cref="Send"/> already gave up and returned (its own
    /// <see cref="CancellationToken"/> fired, or <see cref="Gate"/>'s own timeout expired): the
    /// answer arrives late, nobody is waiting for it any more, and reading it as anything else would
    /// mean guessing which live call it belongs to — exactly the risk <see cref="Send"/>'s old
    /// per-call id check existed to refuse. When the stream ends (<c>null</c>, or the process died
    /// out from under the read), every waiter still in <see cref="_outstanding"/> is failed — a
    /// caller no longer has any read of its own to notice that, now that this loop owns the only one.
    /// </summary>
    private async Task ReadLoop()
    {
        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await _process.StandardOutput.ReadLineAsync();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    FailAllOutstanding($"lost connection to plugin host process while reading a reply: {ex.Message}");
                    return;
                }

                if (line is null)
                {
                    var stderr = await TryReadStderr(_process);
                    FailAllOutstanding(
                        $"plugin host process exited without a reply (exit code {SafeExitCode(_process)})"
                        + (string.IsNullOrEmpty(stderr) ? "." : $" — stderr: {stderr}"));
                    return;
                }

                HostReply? reply;
                try
                {
                    reply = JsonSerializer.Deserialize<HostReply>(line);
                }
                catch (JsonException)
                {
                    // AN UNPARSEABLE LINE HAS NO ID TO MATCH — this loop cannot know which waiter it
                    // was for, so it can only report and move on, never fail a specific call over it
                    // (the call it was meant for still gets its own answer, or its own eventual
                    // stream-end failure, from a later line).
                    continue;
                }

                if (reply is null) continue;

                if (!_outstanding.TryRemove(reply.Id, out var tcs))
                {
                    // A REPLY WITH NO ONE WAITING — a gate that timed out (AbiPlugin's 500ms
                    // GateTimeout) is the routine case: Send already returned its own timeout
                    // failure and moved on, and this is that call's answer arriving after the fact.
                    // Skipped rather than fatal, matching what HostProtocol documents.
                    continue;
                }

                tcs.TrySetResult(reply);
            }
        }
        catch (Exception ex)
        {
            // BELT AND BRACES: nothing above should throw uncaught, but a waiter left forever
            // pending because this loop died some other way would be a hang with no diagnostic —
            // worse than over-catching here.
            FailAllOutstanding($"plugin host reader loop failed: {ex.Message}");
        }
    }

    private void FailAllOutstanding(string message)
    {
        // TryRemove, not just enumerate-and-set: a Send racing this same moment to register its own
        // waiter (see Send's ordering) must not have that waiter left uncompleted because the sweep
        // ran just before it was added — the loop has already ended, so nothing will ever complete
        // it otherwise. Draining in a loop, rather than one Keys pass, catches exactly that race.
        while (!_outstanding.IsEmpty)
        {
            foreach (var id in _outstanding.Keys.ToArray())
                if (_outstanding.TryRemove(id, out var tcs))
                    tcs.TrySetResult(new HostReply(id, false, null, message));
        }
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

        // ONLY NOW, after the one startup line is off the stream — starting the loop any earlier
        // would race Launch's own ReadLineAsync above for the same bytes (StreamReader forbids two
        // reads in flight at once, same as two concurrent Sends used to race each other).
        host.StartReadLoop();
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
    /// <summary>
    /// One per-call permission decision, BOUNDED. The caller is synchronous — IAgentTool.Gate is,
    /// and a permission prompt is decided on the UI path — so this cannot wait indefinitely on
    /// another process: a plugin that never answers would freeze the interface rather than ask a
    /// question. The timeout expiring is not an error to report but a decision to make, and the
    /// caller reads it as "ask", never as "allow".
    /// </summary>
    public Task<HostReply> Gate(string toolName, JsonElement arguments, TimeSpan timeout,
        CancellationToken ct)
    {
        var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(timeout);
        return Send(HostProtocol.RequestKind.Gate, toolName, arguments, timed.Token)
            .ContinueWith(t => { timed.Dispose(); return t.Result; }, TaskScheduler.Default);
    }

    public Task<HostReply> Stop(CancellationToken ct) =>
        Send(HostProtocol.RequestKind.Stop, null, null, ct);

    /// <summary>
    /// Sends one request and waits for its reply, or gives up — never hangs and never throws past
    /// this method. Three ways this can end besides an ordinary reply:
    ///
    /// <para>1. <paramref name="ct"/> FIRES WHILE WAITING — Abi/README.md, "Cancellation": the wait
    /// is ABANDONED, not the native call cancelled. This method stops awaiting the reply and returns
    /// a failure immediately, removing its own waiter from <see cref="_outstanding"/> as it does —
    /// so whatever the host eventually writes for this id lands in <see cref="ReadLoop"/>'s "no
    /// waiter" branch and is logged and skipped, rather than mistaken for a later call reusing the
    /// same id (which cannot happen, since ids only ever increase) or left registered forever.</para>
    ///
    /// <para>2. THE WRITE ITSELF FAILS — the host died between the last successful send and this
    /// one (a killed process, a broken pipe). <see cref="IOException"/>/<see cref="ObjectDisposedException"/>
    /// from the write are caught here, not left to propagate into <see cref="AbiPlugin"/> as an
    /// unhandled exception — exactly the "killed host BETWEEN calls" case this task's test proves.</para>
    ///
    /// <para>3. THE READ LOOP ENDS WITHOUT EVER ANSWERING THIS ID — stdout closed, the process
    /// exited, or the loop itself faulted. <see cref="ReadLoop"/> fails every waiter it still holds
    /// when that happens, so this method only needs to await the same <see cref="TaskCompletionSource{TResult}"/>
    /// the loop completes either way; it does not read stdout itself any more.</para>
    /// </summary>
    private async Task<HostReply> Send(HostProtocol.RequestKind kind, string? toolName,
        JsonElement? arguments, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new HostRequest(id, kind, toolName, arguments);
        var tcs = new TaskCompletionSource<HostReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        // REGISTERED BEFORE THE WRITE, not after — the host could answer (and ReadLoop could read
        // that answer) faster than this method gets back from awaiting the write, and a waiter
        // added after that race would miss its own reply forever.
        _outstanding[id] = tcs;

        // THE READ LOOP MAY HAVE ALREADY ENDED before this waiter was registered — it drains
        // _outstanding once, on the way out, and cannot know a call that hadn't registered yet was
        // coming. Re-checking after registering (and after the write, below) closes that window:
        // either the loop's own drain catches this waiter, or this call catches the loop already
        // being done and fails itself the same way FailAllOutstanding would have.
        if (_readLoop.IsCompleted)
        {
            _outstanding.TryRemove(id, out _);
            return new HostReply(id, false, null, "plugin host reader loop is no longer running.");
        }

        try
        {
            // THE WRITE ITSELF IS SERIALISED, matching the host process's own discipline on its
            // reply writes for the same reason: two requests racing to write could interleave their
            // bytes mid-line and hand the host a line neither JSON.
            await _writeLock.WaitAsync(ct);
            try
            {
                await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
                await _process.StandardInput.FlushAsync(ct);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _outstanding.TryRemove(id, out _);
            return new HostReply(id, false, null,
                $"could not send to plugin host process (it may have died): {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            _outstanding.TryRemove(id, out _);
            return new HostReply(id, false, null, "call abandoned: cancelled before the host could be sent the request.");
        }

        // A SECOND CHECK, after the write: the loop could have ended (and run its drain) in the gap
        // between the check above and the write landing, which would leave this waiter registered
        // after the drain already swept past it.
        if (_readLoop.IsCompleted && _outstanding.TryRemove(id, out var stillWaiting))
        {
            stillWaiting.TrySetResult(new HostReply(id, false, null, "plugin host reader loop is no longer running."));
        }

        await using var registration = ct.CanBeCanceled
            ? ct.Register(() =>
            {
                // REMOVED, NOT LEFT REGISTERED — this call stops waiting, but the host still owns
                // this id and may answer it later; leaving the entry behind would hold a completed
                // TCS in the map forever (ReadLoop only ever removes an id it can match). Once
                // removed, ReadLoop's own "no waiter" branch is what safely discards that late reply.
                _outstanding.TryRemove(id, out _);
                tcs.TrySetResult(
                    new HostReply(id, false, null, "call abandoned: cancelled while waiting for the plugin host's reply."));
            })
            : default;

        // ABANDONED, NOT CANCELLED — see this method's own doc, point 1. The read loop keeps running
        // and will still complete this same TCS if the reply arrives later; ReadLoop's "no waiter"
        // branch is exactly what makes that safe once nobody is awaiting it any more, matching
        // PluginRegistry.UnwireAsync's own "the await here is abandoned, not cancelled" language for
        // a managed plugin's hung Stop.
        return await tcs.Task;
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

        // KILLING THE PROCESS CLOSES ITS STDOUT, which is what makes ReadLoop's own ReadLineAsync
        // return null and run FailAllOutstanding — the death fan-out is a consequence of the
        // teardown above, not something this method does itself. Awaited here only so a caller's
        // own await of DisposeAsync does not return before every outstanding waiter has actually
        // been failed, and so the loop's task is observed rather than left to fault silently in the
        // background.
        try
        {
            await _readLoop;
        }
        catch (Exception)
        {
            // ReadLoop itself never lets an exception escape — it catches everything down to
            // FailAllOutstanding — but nothing here depends on that holding forever either.
        }
    }
}
