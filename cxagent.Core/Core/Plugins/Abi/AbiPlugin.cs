using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;

namespace CxAgent.Core.Plugins.Abi;

/// <summary>
/// An <see cref="IPlugin"/> backed by a <c>cxagent-plugin-host</c> subprocess — the shim Task 9's
/// brief asks for: "nothing downstream knows or cares which kind it loaded." <see cref="PluginRegistry"/>
/// holds this exactly like a managed plugin's instance; every method here maps one <see cref="IPlugin"/>
/// call onto one <see cref="AbiHostProcess"/> request and translates the reply back into the same
/// <see cref="Task"/>/<see cref="Task{JobResult}"/> shapes a managed plugin returns.
///
/// <para>A DEAD HOST DEGRADES TO A FAILED CALL, NEVER AN EXCEPTION — the hard requirement Task 9's
/// brief names: "a plugin whose host segfaulted reports a failed call rather than taking the
/// session with it." <see cref="AbiHostProcess.Send"/> already turns a closed pipe or a dead
/// process into a failed <see cref="HostReply"/> rather than throwing; this class's job is only to
/// translate that failed reply into the managed shape <see cref="IPlugin.Invoke"/> promises
/// (<c>JobResult { Success = false }</c>) rather than into an exception that would propagate into
/// <see cref="PluginRegistry"/> and the agent loop above it. Nothing in this class re-checks whether
/// the host is alive before sending — there is no cheaper check than sending and seeing.</para>
///
/// <para>CONSTRUCTED ONLY BY <see cref="AbiPluginLoader.Load"/>, never directly — the loader owns
/// spawning the host, running the handshake, and registering the child process; by the time this
/// type exists, all of that has already succeeded.</para>
/// </summary>
public sealed class AbiPlugin : IPlugin, IPluginGateSource
{
    private readonly AbiHostProcess _host;
    private readonly PluginManifest _manifest;

    internal AbiPlugin(AbiHostProcess host, PluginManifest manifest)
    {
        _host = host;
        _manifest = manifest;

        // SUBSCRIBED HERE, IN THE CONSTRUCTOR, not in Load or Start — AbiPluginLoader.Load
        // constructs this instance and only afterward calls this.Load(context, ct), so a submit
        // line arriving between those two calls (the host process starts polling the moment its
        // request loop is up, independent of whether cxagent has called Start yet) must already
        // have a listener, or it is dropped on the floor exactly like an unmatched reply.
        _host.PluginSubmitted += OnPluginSubmitted;
    }

    /// <summary>
    /// ALWAYS THE MANIFEST THE HANDSHAKE ALREADY VALIDATED — <see cref="AbiPluginLoader.Load"/> ran
    /// <c>describe</c> and confirmed the sidecar match before this instance existed at all, so
    /// unlike a managed plugin (whose <see cref="IPlugin.Load"/> is the FIRST time its manifest is
    /// seen), this call's only remaining job is to remember <paramref name="context"/> for
    /// <see cref="Start"/> — the ABI's own split (Abi/README.md, "describe" vs "start") means the
    /// native plugin itself has not seen a working directory or settings yet, exactly the same
    /// deferral <see cref="Start"/>'s wire call performs one step later.
    /// </summary>
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct)
    {
        _workingDirectory = context.WorkingDirectory;
        _settings = context.Settings;

        // NULL WHEN THE SIDECAR DID NOT DECLARE "client": true — IPluginContext.Client's own gate,
        // the identical one a managed plugin is held to. This shim does not re-check the
        // declaration itself; by the time Load runs, AbiPluginLoader has already matched the wire
        // manifest against the sidecar and the caller has already decided whether context.Client is
        // non-null (see PluginResolver.PluginRuntime.Client's own doc).
        _client = context.Client;
        _logger = context.Logger;
        return Task.FromResult(_manifest);
    }

    /// <summary>
    /// A poll's submit, reaching this instance across the process boundary — see
    /// <see cref="AbiHostProcess.PluginSubmitted"/> and <see cref="AbiSubmit"/>'s own doc for the
    /// wire mechanism. FIRE-AND-FORGET: the plugin gets no reply on this line (poll's own contract
    /// has no channel for one), so a refusal here — no client, or <c>wantResult:true</c> — is
    /// reported to the plugin's own logger and otherwise swallowed, the same "nowhere to throw"
    /// position <see cref="Stop"/> is already in for a dead host.
    /// </summary>
    private void OnPluginSubmitted(AbiSubmit submit)
    {
        if (_client is null)
        {
            // A PLUGIN THAT NEVER DECLARED THE CAPABILITY POLLED ITS WAY TO ONE ANYWAY — the sidecar
            // gate stops the reference from ever existing (see Load's own doc), but nothing stops a
            // native plugin's own poll() from returning a submit regardless; this is that refusal,
            // named rather than silently dropped, so a plugin author sees why nothing happened.
            LogRefusal($"plugin '{_manifest.Name}' polled a submit but never declared the client capability — refused.");
            return;
        }

        if (submit.WantResult)
        {
            // REFUSED BY NAME, NOT DOWNGRADED — see AbiSubmit's own doc for why: silently turning
            // this into wantResult:false would let a native author believe an answer is coming that
            // this contract has no channel to deliver.
            LogRefusal($"plugin '{_manifest.Name}' polled a submit with wantResult:true, which contract 3's ABI "
                + "surface refuses — an ABI plugin may only fire-and-forget.");
            return;
        }

        // FIRE-AND-FORGET ON PURPOSE: this handler runs on AbiHostProcess's own reader loop, which
        // must keep reading the next line rather than block on a turn that may run minutes — the
        // same reasoning IPluginClient.Submit's own doc gives for wantResult:false. The returned
        // Task is intentionally not awaited; a submit that the session refuses (queue full, no
        // agent configured) is reported to the plugin's own logger rather than thrown into a loop
        // with nothing to catch it.
        _ = ReportIfRefused(submit.Goal);
    }

    private async Task ReportIfRefused(string goal)
    {
        try
        {
            var result = await _client!.Submit(goal, wantResult: false);
            if (!result.Accepted)
                LogRefusal($"plugin '{_manifest.Name}' submitted '{goal}' and the session refused it: {result.Refusal}");
        }
        catch (Exception ex)
        {
            // THE SESSION CAN THROW (ObjectDisposedException once severed, chiefly) — this runs on
            // the reader loop's own background task, which nothing else awaits, so an uncaught
            // exception here would be silent rather than merely unhelpful.
            LogRefusal($"plugin '{_manifest.Name}' submitted '{goal}' and the session threw: {ex.Message}");
        }
    }

    // BEST-EFFORT, NEVER THROWS — a plugin's own logger is not this refusal-reporting path's to
    // fail over; see cxagent_plugin.h's "no exception may cross this boundary" discipline, which
    // this handler (running entirely managed-side, off the reader loop) still honours in spirit.
    private void LogRefusal(string message)
    {
        try { _logger?.Log(message); } catch (Exception) { }
    }

    /// <summary>
    /// Sends <c>start</c> with the plugin's working directory and settings — see Abi/README.md,
    /// "context": exactly <see cref="IPluginContext.WorkingDirectory"/> and
    /// <see cref="IPluginContext.Settings"/>, nothing else. THE TRANSCRIPT, THE MODEL, AND THE
    /// PERMISSION STORE NEVER CROSS — <see cref="IPluginContext"/> carries no member for any of
    /// them in the first place, so there is nothing here that could leak them even by accident.
    /// </summary>
    /// <exception cref="InvalidOperationException">The host's reply was <c>ok:false</c> — a
    /// malformed start, a dead host, or the plugin's own <c>cxagent_plugin_start</c> failing.
    /// <see cref="IPlugin.Start"/> returns <see cref="Task"/>, not a result type, so a managed
    /// plugin that fails to start already signals it by throwing; this mirrors that rather than
    /// inventing a second failure channel a managed plugin's own callers do not expect.</exception>
    public async Task Start(CancellationToken ct)
    {
        var reply = await _host.Start(_workingDirectory, _settings, ct).ConfigureAwait(false);
        if (!reply.Ok)
            throw new InvalidOperationException($"plugin '{_manifest.Name}' failed to start: {reply.Error}");
    }

    /// <summary>
    /// Sends <c>invoke</c> for <paramref name="toolName"/> and translates the reply into a
    /// <see cref="JobResult"/> — NEVER THROWS FOR A HOST-LEVEL FAILURE. A dead host, a malformed
    /// envelope, or a cancelled wait all become <c>JobResult { Success = false, ErrorMessage = ... }</c>,
    /// the same shape a managed plugin's own tool failure already takes — see
    /// <see cref="IPlugin.Invoke"/>'s own doc: "the call completed and the tool failed on its own
    /// terms" and "the call itself failed" both flow through this one return type on the managed
    /// side of <see cref="PluginRegistry"/>, exactly as Abi/README.md draws the same distinction on
    /// the wire (<c>ok</c> vs <c>result.success</c>).
    /// </summary>
    public async Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct)
    {
        var argumentsJson = AbiCodec.WriteInvokeCall(toolName, call);
        using var doc = JsonDocument.Parse(argumentsJson);
        var arguments = doc.RootElement.GetProperty("arguments").Clone();

        var reply = await _host.Invoke(toolName, arguments, ct).ConfigureAwait(false);
        if (!reply.Ok)
            return new JobResult { Success = false, ErrorMessage = reply.Error ?? $"plugin '{_manifest.Name}' call to '{toolName}' failed." };

        if (reply.Result is null)
            return new JobResult
            {
                Success = false,
                ErrorMessage = $"plugin '{_manifest.Name}' replied ok:true to invoke with no result — invoke always returns a JobResult.",
            };

        var r = reply.Result;
        var output = r.Output.ValueKind == JsonValueKind.Object
            ? r.Output.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value)
            : new Dictionary<string, object?>();

        return new JobResult
        {
            Success = r.Success,
            ExitCode = r.ExitCode,
            ErrorMessage = r.ErrorMessage,
            PermissionDenied = r.PermissionDenied,
            DecidedBy = r.DecidedBy,
            Output = output,
            LogFile = r.LogFile,
            Duration = TimeSpan.FromMilliseconds(r.DurationMs),
        };
    }

    /// <summary>
    /// How long a gate may take before the host stops waiting and asks instead. Short because this
    /// runs on the path that renders a permission prompt: a gate is meant to inspect arguments
    /// already in hand, so anything slower than this is a plugin doing something a gate should not.
    /// </summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The per-call decision, ACROSS A PROCESS BOUNDARY AND SYNCHRONOUSLY.
    ///
    /// <para>THE BLOCK IS THE POINT OF THE TIMEOUT. <see cref="Jobs.IAgentTool.Gate"/> is
    /// synchronous, so this cannot await; a managed plugin answers in nanoseconds but an ABI plugin
    /// is another process, and an unbounded wait here would hang the interface on a plugin that
    /// never replies. <see cref="GateTimeout"/> bounds it, and every way of not getting an answer —
    /// timeout, a dead host, an unparseable reply — reads the same: ASK, and offer no standing
    /// grant. A gate that cannot answer must never be able to decide "allow".</para>
    /// </summary>
    public PluginGate? Gate(string toolName, JobParameters call)
    {
        HostReply reply;
        try
        {
            var argumentsJson = AbiCodec.WriteInvokeCall(toolName, call);
            using var arguments = JsonDocument.Parse(argumentsJson);
            reply = _host.Gate(toolName, arguments.RootElement.Clone(), GateTimeout, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return Unanswered(toolName);
        }

        if (!reply.Ok) return Unanswered(toolName);

        // A GATE THAT SAID NOTHING SAID "no prompt" — the same meaning as a managed gate returning
        // null, and the reason every v2 plugin can export a gate that unconditionally returns NULL.
        return reply.Gate is null
            ? null
            : new PluginGate(reply.Gate.Display, reply.Gate.AlwaysAskable);
    }

    /// <summary>A gate that produced no usable answer. Asks, and withholds "Always": a broken gate
    /// must not be able to earn a standing grant while it is broken.</summary>
    private PluginGate Unanswered(string toolName) =>
        new($"run '{toolName}' from the '{_manifest.Name}' plugin (its permission check did not answer)",
            AlwaysAskable: false);


    /// <summary>
    /// Sends <c>stop</c> and disposes the host process regardless of whether that reply was
    /// <c>ok:true</c> — a plugin that failed its own shutdown still gets its process torn down.
    /// <see cref="PluginRegistry.UnwireAsync"/> already reaps whatever a plugin's Stop leaves behind,
    /// and disposing here (which kills the
    /// process if it has not already exited) is this loader's own half of "the ABI half of this
    /// asymmetry" — see that method's own doc — closing the process rather than leaving it to the
    /// pid-record reap alone. A DEAD HOST NEVER THROWS FROM HERE: <see cref="AbiHostProcess.Stop"/>
    /// already degrades a dead process to a failed reply, so this simply proceeds to dispose either
    /// way — a plugin whose host is already gone has nothing left to stop.
    /// </summary>
    public async Task Stop(CancellationToken ct)
    {
        // UNSUBSCRIBED BEFORE THE HOST IS TORN DOWN — belt and braces alongside SessionPluginClient's
        // own severed-flag check inside Submit: a submit line already in flight when Stop runs must
        // not call into a client the caller may sever moments later, and once this instance is
        // stopped it has nothing left to do with one anyway.
        _host.PluginSubmitted -= OnPluginSubmitted;
        await _host.Stop(ct).ConfigureAwait(false);
        await _host.DisposeAsync().ConfigureAwait(false);
    }

    // SET BY Load, READ BY Start — see Load's own doc for why this call, not construction, is
    // where they become known: ManagedPluginLoader's own IPlugin.Load contract is the first time
    // ANY plugin (managed or ABI) sees its IPluginContext, and this shim keeps that ordering rather
    // than smuggling the context in earlier through a constructor IPlugin has no parameter list for.
    private string _workingDirectory = "";
    private JsonElement _settings = JsonDocument.Parse("{}").RootElement;

    // NULL UNTIL Load RUNS, THEN FIXED — the same declared-not-universal gate IPluginContext.Client
    // documents: null for a plugin whose sidecar never asked for it, and never reassigned afterward,
    // matching a managed plugin's own one-shot capture of its context at Load.
    private IPluginClient? _client;
    private IPluginLogger? _logger;
}
