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
public sealed class AbiPlugin : IPlugin
{
    private readonly AbiHostProcess _host;
    private readonly PluginManifest _manifest;

    internal AbiPlugin(AbiHostProcess host, PluginManifest manifest)
    {
        _host = host;
        _manifest = manifest;
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
        return Task.FromResult(_manifest);
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
    /// Sends <c>stop</c> and disposes the host process regardless of whether that reply was
    /// <c>ok:true</c> — a plugin that failed its own shutdown still gets its process torn down,
    /// matching PLUGINS.md's "Unwire is one ordered operation": <see cref="PluginRegistry.UnwireAsync"/>
    /// already reaps whatever a plugin's Stop leaves behind, and disposing here (which kills the
    /// process if it has not already exited) is this loader's own half of "the ABI half of this
    /// asymmetry" — see that method's own doc — closing the process rather than leaving it to the
    /// pid-record reap alone. A DEAD HOST NEVER THROWS FROM HERE: <see cref="AbiHostProcess.Stop"/>
    /// already degrades a dead process to a failed reply, so this simply proceeds to dispose either
    /// way — a plugin whose host is already gone has nothing left to stop.
    /// </summary>
    public async Task Stop(CancellationToken ct)
    {
        await _host.Stop(ct).ConfigureAwait(false);
        await _host.DisposeAsync().ConfigureAwait(false);
    }

    // SET BY Load, READ BY Start — see Load's own doc for why this call, not construction, is
    // where they become known: ManagedPluginLoader's own IPlugin.Load contract is the first time
    // ANY plugin (managed or ABI) sees its IPluginContext, and this shim keeps that ordering rather
    // than smuggling the context in earlier through a constructor IPlugin has no parameter list for.
    private string _workingDirectory = "";
    private JsonElement _settings = JsonDocument.Parse("{}").RootElement;
}
