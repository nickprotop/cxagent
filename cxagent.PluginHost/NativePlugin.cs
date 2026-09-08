using System.Runtime.InteropServices;

namespace CxAgent.PluginHost;

/// <summary>
/// What loading a native library and resolving its exports produced — a live
/// <see cref="NativePlugin"/>, or a reason it never got there. Every failure here is exactly one
/// this process must refuse CLEANLY rather than guess past: a missing file, a file that is not a
/// shared library, or one that does not export all seven MANDATORY <c>cxagent_plugin_*</c> symbols.
/// The eighth, <c>cxagent_plugin_poll</c>, is optional — see <see cref="NativePlugin.HasPoll"/> —
/// and its absence never fails a load.
/// </summary>
public abstract record NativePluginLoadResult
{
    private NativePluginLoadResult() { }

    public sealed record Loaded(NativePlugin Plugin) : NativePluginLoadResult;

    public sealed record Failed(string Reason) : NativePluginLoadResult;
}

/// <summary>
/// The seven MANDATORY <c>extern "C"</c> exports cxagent_plugin.h declares, plus the one OPTIONAL
/// export (<c>cxagent_plugin_poll</c>), resolved once by name from a loaded library and called
/// through function pointers — NOT <c>[DllImport]</c>, because the path to load is a runtime
/// argument (the library named in a plugin's config entry), not a compile-time constant
/// <c>DllImport</c> requires. <see cref="NativeLibrary.GetExport"/> is the same resolution
/// <c>DllImport</c> would perform, just deferred to a path this process only learns at startup.
///
/// <para>NOTHING HERE HOLDS A JSON STRING LONGER THAN IT TAKES TO COPY IT OUT. Every method below
/// copies the plugin-owned UTF-8 bytes into a managed <see cref="string"/> and then calls
/// <see cref="Free"/> on the original pointer BEFORE RETURNING — in a <c>try/finally</c>, so a
/// parse failure on the managed side after the copy still releases the native memory. That
/// discipline is this type's whole reason to exist as its own class rather than inline calls in
/// Program.cs: every call site gets it for free instead of six copies of the same try/finally.</para>
/// </summary>
public sealed class NativePlugin : IDisposable
{
    private delegate int AbiVersionFn();
    private delegate IntPtr DescribeFn();
    private delegate IntPtr StartFn(IntPtr contextJson);
    private delegate IntPtr InvokeFn(IntPtr toolName, IntPtr callJson);
    private delegate IntPtr GateFn(IntPtr toolName, IntPtr callJson);
    private delegate IntPtr StopFn();
    private delegate void FreeFn(IntPtr ptr);
    private delegate IntPtr PollFn();
    private delegate IntPtr CommandFn(IntPtr name, IntPtr argumentsJson);

    private readonly IntPtr _handle;
    private readonly AbiVersionFn _abiVersion;
    private readonly DescribeFn _describe;
    private readonly StartFn _start;
    private readonly InvokeFn _invoke;
    private readonly GateFn _gate;
    private readonly StopFn _stop;
    private readonly FreeFn _free;
    // NULL FOR A LIBRARY BUILT BEFORE POLL EXISTED — the one export whose absence Load tolerates.
    // See HasPoll.
    private readonly PollFn? _poll;
    // NULL FOR A LIBRARY BUILT BEFORE COMMANDS EXISTED, OR ONE THAT DECLARES NONE — the second
    // export whose absence Load tolerates, for the same reason poll's does: a plugin built before
    // contract 3 added commands must still load. See HasCommand.
    private readonly CommandFn? _command;

    /// <summary>
    /// Every <c>cxagent_plugin_*</c> symbol a library must export, resolved together.
    ///
    /// <para>ONE RECORD RATHER THAN SEVEN-PLUS PARAMETERS: they are the ABI's export table, not
    /// unrelated arguments, and they are all the same delegate-shaped kind of thing. Passed
    /// positionally, transposing two that share a signature — <c>describe</c> and <c>stop</c> both
    /// take nothing and return a pointer — compiles cleanly and calls the wrong function.</para>
    /// </summary>
    private sealed record Exports(
        AbiVersionFn AbiVersion, DescribeFn Describe, StartFn Start,
        InvokeFn Invoke, GateFn Gate, StopFn Stop, FreeFn Free, PollFn? Poll, CommandFn? Command);

    private NativePlugin(IntPtr handle, Exports exports)
    {
        _handle = handle;
        _abiVersion = exports.AbiVersion;
        _describe = exports.Describe;
        _start = exports.Start;
        _invoke = exports.Invoke;
        _gate = exports.Gate;
        _stop = exports.Stop;
        _free = exports.Free;
        _poll = exports.Poll;
        _command = exports.Command;
    }

    /// <summary>
    /// Loads <paramref name="libraryPath"/> and resolves its exports: seven MANDATORY, refusing the
    /// load if any is missing, and two OPTIONAL (<c>cxagent_plugin_poll</c>, <c>cxagent_plugin_command</c>),
    /// left null rather than refused. Resolving every mandatory symbol UP FRONT, before returning a
    /// usable instance, is what turns "this .so is not a cxagent plugin" into one clean load-time
    /// failure instead of a null-pointer call the first time some unrelated tool invocation happens
    /// to reach the one export that was never actually there. Refusing an OPTIONAL export the same
    /// way would refuse every plugin built before it existed — the entire reason contract-2 plugins
    /// still load against a contract-3 host, and the same reason a plugin declaring no commands at
    /// all need not export <c>cxagent_plugin_command</c> either.
    /// </summary>
    public static NativePluginLoadResult Load(string libraryPath)
    {
        if (!File.Exists(libraryPath))
            return new NativePluginLoadResult.Failed($"no library at '{libraryPath}'.");

        IntPtr handle;
        try
        {
            handle = NativeLibrary.Load(libraryPath);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            return new NativePluginLoadResult.Failed(
                $"could not load '{libraryPath}' as a native library: {ex.Message}");
        }

        try
        {
            var abiVersion = ResolveExport<AbiVersionFn>(handle, "cxagent_plugin_abi_version");
            var describe = ResolveExport<DescribeFn>(handle, "cxagent_plugin_describe");
            var start = ResolveExport<StartFn>(handle, "cxagent_plugin_start");
            var invoke = ResolveExport<InvokeFn>(handle, "cxagent_plugin_invoke");
            var gate = ResolveExport<GateFn>(handle, "cxagent_plugin_gate");
            var stop = ResolveExport<StopFn>(handle, "cxagent_plugin_stop");
            var free = ResolveExport<FreeFn>(handle, "cxagent_plugin_free");
            var poll = ResolveOptionalExport<PollFn>(handle, "cxagent_plugin_poll");
            var command = ResolveOptionalExport<CommandFn>(handle, "cxagent_plugin_command");

            return new NativePluginLoadResult.Loaded(
                new NativePlugin(handle, new Exports(abiVersion, describe, start, invoke, gate, stop, free, poll, command)));
        }
        catch (MissingExportException ex)
        {
            // THE HANDLE IS FREED HERE, ON THIS FAILURE PATH ONLY — a successful Load hands the
            // handle to the NativePlugin it returns, whose own Dispose owns it from then on. A
            // library missing an export never produces one, so nothing else will ever free this
            // handle if this call site does not.
            NativeLibrary.Free(handle);
            return new NativePluginLoadResult.Failed(
                $"'{libraryPath}' does not export '{ex.Symbol}' — not a cxagent ABI plugin.");
        }
    }

    private static T ResolveExport<T>(IntPtr handle, string symbol) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(handle, symbol, out var address))
            throw new MissingExportException(symbol);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    /// <summary>
    /// Resolves <paramref name="symbol"/> when the library exports it, and returns null rather than
    /// throwing when it does not — the one difference from <see cref="ResolveExport{T}"/>, which is
    /// what makes an export OPTIONAL rather than MANDATORY. <see cref="NativeLibrary.TryGetExport"/>
    /// was already the API in use here; only the missing-export branch changes.
    /// </summary>
    private static T? ResolveOptionalExport<T>(IntPtr handle, string symbol) where T : Delegate =>
        NativeLibrary.TryGetExport(handle, symbol, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;

    private sealed class MissingExportException(string symbol) : Exception
    {
        public string Symbol { get; } = symbol;
    }

    /// <summary>Whether this library exported <c>cxagent_plugin_poll</c> — false for a library built
    /// before the export existed, which is not a refused load (see <see cref="Load"/>), just a
    /// plugin this host never polls.</summary>
    public bool HasPoll => _poll is not null;

    /// <summary>Whether this library exported <c>cxagent_plugin_command</c> — false for a library
    /// built before commands existed, or one that declares none of its own, which is not a refused
    /// load (see <see cref="Load"/>), just a plugin this host never asks to run one.</summary>
    public bool HasCommand => _command is not null;

    /// <summary>The ABI version this library reports — checked by the caller against
    /// <see cref="CxAgent.Core.Plugins.PluginContract.Version"/> with a range, not exact equality
    /// (see cxagent_plugin.h, "ABI HANDSHAKE") before anything else here is trusted.</summary>
    public int AbiVersion() => _abiVersion();

    /// <summary>Calls <c>cxagent_plugin_describe</c> and returns the manifest JSON, copied out of
    /// native memory before the native buffer is freed.</summary>
    public string Describe() => CallAndFree(() => _describe());

    /// <summary>Calls <c>cxagent_plugin_start</c> with <paramref name="contextJson"/> and returns the
    /// result envelope JSON.</summary>
    public string Start(string contextJson)
    {
        var contextPtr = Utf8.StringToNative(contextJson);
        try
        {
            return CallAndFree(() => _start(contextPtr));
        }
        finally
        {
            Utf8.FreeNative(contextPtr);
        }
    }

    /// <summary>
    /// Calls <c>cxagent_plugin_invoke</c> with <paramref name="toolName"/> and
    /// <paramref name="callJson"/> and returns the result envelope JSON. MAY BE CALLED
    /// CONCURRENTLY from multiple threads — cxagent_plugin.h states the plugin, not this host, owns
    /// serializing invokes it cannot tolerate concurrently, so this method takes no lock and simply
    /// forwards the call.
    /// </summary>
    /// <summary>
    /// Calls <c>cxagent_plugin_gate</c>. Returns null when the plugin returned NULL, which is its
    /// way of saying "this call needs no prompt" — the one export whose null return is an ANSWER
    /// rather than a failure, so it is not routed through CallAndFree's non-null expectation.
    /// </summary>
    public string? Gate(string toolName, string callJson)
    {
        var namePtr = Utf8.StringToNative(toolName);
        var callPtr = Utf8.StringToNative(callJson);
        try
        {
            var result = _gate(namePtr, callPtr);
            if (result == IntPtr.Zero) return null;
            try
            {
                return Utf8.NativeToString(result);
            }
            finally
            {
                // FREED THROUGH THE PLUGIN'S OWN FREE, like every other returned pointer: the
                // library allocated it and only the library knows how to release it.
                _free(result);
            }
        }
        finally
        {
            Utf8.FreeNative(namePtr);
            Utf8.FreeNative(callPtr);
        }
    }

    /// <summary>
    /// Calls <c>cxagent_plugin_poll</c>. Returns null when the plugin returned NULL, its ordinary
    /// way of saying "nothing to say right now" — like <see cref="Gate"/>, the one export besides
    /// gate whose null return is an ANSWER rather than a failure, so it is not routed through
    /// CallAndFree's non-null expectation. Throws if the library never exported <c>poll</c> at all
    /// — callers must check <see cref="HasPoll"/> first, since there is no ABI-level way to answer
    /// "the plugin has nothing to say" and "the plugin cannot be asked" with the same null.
    /// </summary>
    public string? Poll()
    {
        if (_poll is null)
            throw new InvalidOperationException(
                "this library never exported cxagent_plugin_poll — check HasPoll before calling Poll.");

        var result = _poll();
        if (result == IntPtr.Zero) return null;
        try
        {
            return Utf8.NativeToString(result);
        }
        finally
        {
            // FREED THROUGH THE PLUGIN'S OWN FREE, like every other returned pointer: the library
            // allocated it and only the library knows how to release it.
            _free(result);
        }
    }

    /// <summary>
    /// Calls <c>cxagent_plugin_command</c> with <paramref name="name"/> and
    /// <paramref name="argumentsJson"/>. Returns null when the plugin returned NULL, which is its
    /// ordinary way of saying "ran and had nothing to say" — like <see cref="Gate"/> and
    /// <see cref="Poll"/>, this export's null return is an ANSWER rather than a failure, so it is
    /// not routed through CallAndFree's non-null expectation. Throws if the library never exported
    /// <c>command</c> at all — callers must check <see cref="HasCommand"/> first, matching
    /// <see cref="Poll"/>'s own discipline: there is no ABI-level way to answer "nothing to say" and
    /// "cannot be asked" with the same null.
    /// </summary>
    public string? Command(string name, string argumentsJson)
    {
        if (_command is null)
            throw new InvalidOperationException(
                "this library never exported cxagent_plugin_command — check HasCommand before calling Command.");

        var namePtr = Utf8.StringToNative(name);
        var argsPtr = Utf8.StringToNative(argumentsJson);
        try
        {
            var result = _command(namePtr, argsPtr);
            if (result == IntPtr.Zero) return null;
            try
            {
                return Utf8.NativeToString(result);
            }
            finally
            {
                // FREED THROUGH THE PLUGIN'S OWN FREE, like every other returned pointer: the
                // library allocated it and only the library knows how to release it.
                _free(result);
            }
        }
        finally
        {
            Utf8.FreeNative(namePtr);
            Utf8.FreeNative(argsPtr);
        }
    }

    public string Invoke(string toolName, string callJson)
    {
        var namePtr = Utf8.StringToNative(toolName);
        var callPtr = Utf8.StringToNative(callJson);
        try
        {
            return CallAndFree(() => _invoke(namePtr, callPtr));
        }
        finally
        {
            Utf8.FreeNative(namePtr);
            Utf8.FreeNative(callPtr);
        }
    }

    /// <summary>Calls <c>cxagent_plugin_stop</c> and returns the result envelope JSON.</summary>
    public string Stop() => CallAndFree(() => _stop());

    /// <summary>
    /// Runs one ABI call that returns a plugin-owned string, copies the UTF-8 bytes out, and frees
    /// the native pointer — ALWAYS, including when the returned bytes are not valid UTF-8 or the
    /// copy itself throws, because <see cref="Free"/> runs in the <c>finally</c>. This is the single
    /// choke point every describe/start/invoke/stop call goes through, so "free exactly once, every
    /// path" is enforced in one place rather than trusted at four call sites.
    /// </summary>
    private string CallAndFree(Func<IntPtr> call)
    {
        var ptr = call();
        try
        {
            // A NULL POINTER IS NEVER FREED — cxagent_plugin.h's "why a plugin must never return
            // NULL" names a static sentinel as the one value cxagent_plugin_free recognises and
            // skips; a plugin that violates its own contract and returns NULL anyway must not have
            // this host call free() on address zero. The empty string this produces reaches
            // AbiCodec.ParseEnvelope, which already reports "NULL or empty" as a malformed reply.
            return ptr == IntPtr.Zero ? "" : Utf8.NativeToString(ptr);
        }
        finally
        {
            if (ptr != IntPtr.Zero) _free(ptr);
        }
    }

    /// <summary>Releases the library handle. Does not call <c>cxagent_plugin_stop</c> — that is a
    /// protocol-level call the caller makes deliberately (see Program.cs), not a side effect of
    /// disposal, so a caller that abandons a plugin without stopping it gets exactly that, visibly,
    /// rather than a silent best-effort Stop hidden in Dispose.</summary>
    public void Dispose() => NativeLibrary.Free(_handle);
}

/// <summary>
/// UTF-8 marshalling to and from native memory that this process owns on the way IN (freed by us,
/// never by the plugin — cxagent_plugin.h's ownership rule applies in both directions) and merely
/// COPIES on the way OUT (the plugin owns those bytes; see <see cref="NativePlugin.CallAndFree"/>).
/// </summary>
internal static class Utf8
{
    /// <summary>Copies a managed string into a NUL-terminated UTF-8 buffer this process allocated —
    /// host-owned, per cxagent_plugin.h: "context_json / call_json ... valid only for the duration
    /// of the call." Freed by <see cref="FreeNative"/> after the call returns, never by the plugin.</summary>
    public static IntPtr StringToNative(string value) => Marshal.StringToCoTaskMemUTF8(value);

    public static void FreeNative(IntPtr ptr) => Marshal.FreeCoTaskMem(ptr);

    /// <summary>Copies a NUL-terminated UTF-8 buffer the PLUGIN allocated into a managed string.
    /// Never frees <paramref name="ptr"/> — the caller frees it via <c>cxagent_plugin_free</c>,
    /// the plugin's own allocator, which this process's allocator must never touch.</summary>
    public static string NativeToString(IntPtr ptr) => Marshal.PtrToStringUTF8(ptr) ?? "";
}
