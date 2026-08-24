using System.Text.Json;
using CxAgent.Core.Plugins;
using CxAgent.Core.Plugins.Abi;
using CxAgent.PluginHost;

// THE BLAST WALL. This process exists so a native library that segfaults, hangs, or corrupts its
// own heap takes only ITSELF down, not the cxagent process that loaded it — see Task 9's own brief.
// Everything below either succeeds and writes a JSON line, or fails and writes a JSON line saying
// so; nothing propagates an unhandled exception back to whatever spawned this process, because an
// unhandled exception here is exactly the failure mode this process is the wall against.
//
// ARGV[0] IS THE NATIVE LIBRARY PATH — the one thing this process is configured with. No session
// state, no config, no permission decisions cross into this process; PLUGINS.md and Task 9's brief
// both say those stay in Core deliberately, and a host that started making them would become a
// second Core.

if (args.Length != 1)
{
    await WriteLine(new HostStartupFailure(false, "usage: cxagent-plugin-host <native-library-path>"));
    return 1;
}

var libraryPath = args[0];

NativePluginLoadResult loadResult;
try
{
    loadResult = NativePlugin.Load(libraryPath);
}
catch (Exception ex)
{
    // NativePlugin.Load already turns the expected native-loader exceptions into a Failed result;
    // this catches anything else (a P/Invoke marshalling fault, for one) so a surprise here still
    // degrades to a clean startup failure rather than an unhandled exception with no JSON line at
    // all — the one shape the parent's read loop cannot recover from.
    await WriteLine(new HostStartupFailure(false, $"loading '{libraryPath}' threw: {ex.Message}"));
    return 1;
}

if (loadResult is not NativePluginLoadResult.Loaded { Plugin: var plugin })
{
    var reason = ((NativePluginLoadResult.Failed)loadResult).Reason;
    await WriteLine(new HostStartupFailure(false, reason));
    return 1;
}

using var _ = plugin;

// THE HANDSHAKE, BEFORE ANYTHING ELSE IS TRUSTED — cxagent_plugin.h, "ABI HANDSHAKE": exact
// equality against the version this build understands, never a floor. A mismatch is reported by
// name on both sides and refused; this process never attempts to read a manifest shape it was not
// built to understand.
int reportedVersion;
try
{
    reportedVersion = plugin.AbiVersion();
}
catch (Exception ex)
{
    // A crash inside cxagent_plugin_abi_version — the ABI's own comment forbids a native panic or
    // exception crossing this boundary, so a plugin that violates that contract on its very first
    // call degrades to a clean startup failure rather than taking this process down with it. There
    // is no P/Invoke-marshalled exception path for a genuine native crash (that terminates the
    // process outright, see the crash test), but a managed-side fault translating the return value
    // still must not propagate.
    await WriteLine(new HostStartupFailure(false, $"cxagent_plugin_abi_version threw: {ex.Message}"));
    return 1;
}

var versionCheck = AbiCodec.CheckVersion(reportedVersion);
if (!versionCheck.IsSuccess)
{
    await WriteLine(new HostStartupFailure(false, versionCheck.Error!));
    return 1;
}

string describeJson;
try
{
    describeJson = plugin.Describe();
}
catch (Exception ex)
{
    await WriteLine(new HostStartupFailure(false, $"cxagent_plugin_describe threw: {ex.Message}"));
    return 1;
}

var manifestResult = AbiCodec.ParseManifest(describeJson);
if (!manifestResult.IsSuccess)
{
    await WriteLine(new HostStartupFailure(false, manifestResult.Error!));
    return 1;
}

var manifest = AbiCodec.ToPluginManifest(manifestResult.Value);
await WriteLine(new HostReady(true, manifest));

// THE REQUEST LOOP. One line in, one line out — see HostProtocol's own doc for why this vocabulary
// exists separately from the ABI JSON. Each request runs on its own Task so a slow or concurrent
// cxagent_plugin_invoke (the ABI explicitly allows concurrent invokes) does not block a reply to a
// request that arrived after it; nothing here awaits a request before reading the next line.
var stdin = Console.In;
var pending = new List<Task>();

while (await stdin.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    HostRequest request;
    try
    {
        request = JsonSerializer.Deserialize<HostRequest>(line)
            ?? throw new JsonException("request line parsed to null");
    }
    catch (JsonException)
    {
        // A REQUEST LINE THIS PROCESS CANNOT PARSE HAS NO ID TO REPLY TO. There is nothing
        // correlatable to send back, so the only honest response is to drop the line — the same
        // choice AbiCodec makes for a plugin's malformed envelope, just one level up: malformed
        // input at a boundary is reported where it CAN be attributed, not invented an id for.
        continue;
    }

    pending.Add(HandleRequest(plugin, request));
    // FINISHED TASKS ARE PRUNED OPPORTUNISTICALLY rather than awaited individually — this loop's
    // job is reading the next line promptly, not bookkeeping a list that only matters at shutdown.
    pending.RemoveAll(t => t.IsCompleted);
}

// STDIN CLOSED — the parent exited or the pipe broke. Every in-flight request still gets a chance
// to finish and write its reply before this process exits; a request whose plugin call never
// returns is exactly what the parent's own cancellation-by-abandonment (Abi/README.md,
// "Cancellation") is for, not something this loop should wait on indefinitely.
await Task.WhenAll(pending);
return 0;

// ---- local functions ----

static async Task HandleRequest(NativePlugin plugin, HostRequest request)
{
    HostReply reply;
    try
    {
        reply = request.Kind switch
        {
            HostProtocol.RequestKind.Start => RunVoidCall(request.Id, () =>
                plugin.Start(AbiCodec.WriteContext(
                    WorkingDirectoryOf(request), SettingsOf(request)))),

            HostProtocol.RequestKind.Invoke => RunInvokeCall(request, plugin),

            HostProtocol.RequestKind.Stop => RunVoidCall(request.Id, plugin.Stop),

            _ => new HostReply(request.Id, false, null, $"unknown request kind '{request.Kind}'."),
        };
    }
    catch (Exception ex)
    {
        // A NATIVE CALL THAT THREW ON THE MANAGED SIDE OF THE MARSHAL (never a genuine native crash
        // — that takes this whole process down, which is the crash test's own point) still must not
        // leave this request unanswered. The parent is waiting on this id.
        reply = new HostReply(request.Id, false, null, $"host call failed: {ex.Message}");
    }

    await WriteLine(reply);
}

static HostReply RunVoidCall(long id, Func<string> call)
{
    var envelopeResult = AbiCodec.ParseEnvelope(call());
    if (!envelopeResult.IsSuccess)
        return new HostReply(id, false, null, envelopeResult.Error);

    var voidResult = AbiCodec.ToVoidResult(envelopeResult.Value);
    return voidResult.IsSuccess
        ? new HostReply(id, true, null, null)
        : new HostReply(id, false, null, voidResult.Error);
}

static HostReply RunInvokeCall(HostRequest request, NativePlugin plugin)
{
    if (string.IsNullOrEmpty(request.ToolName))
        return new HostReply(request.Id, false, null, "invoke request is missing 'toolName'.");

    var argsJson = AbiCodec.WriteInvokeCall(request.ToolName, request.Arguments ?? default);
    // WriteInvokeCall(string, JsonElement) already writes the full { toolName, arguments } object —
    // the same helper the ABI codec offers a caller sending JobParameters. Its own output is what
    // plugin.Invoke needs to send, so this pulls the tool name and arguments back apart the way the
    // wire call expects them (two separate parameters, not one object).
    using var doc = JsonDocument.Parse(argsJson);
    var toolName = doc.RootElement.GetProperty("toolName").GetString()!;
    var arguments = doc.RootElement.GetProperty("arguments").GetRawText();

    var envelopeResult = AbiCodec.ParseEnvelope(plugin.Invoke(toolName, arguments));
    if (!envelopeResult.IsSuccess)
        return new HostReply(request.Id, false, null, envelopeResult.Error);

    var invokeResult = AbiCodec.ToInvokeResult(envelopeResult.Value);
    if (!invokeResult.IsSuccess)
        return new HostReply(request.Id, false, null, invokeResult.Error);

    var job = invokeResult.Value;
    var outputJson = JsonSerializer.SerializeToElement(job.Output);
    var abiResult = new AbiJobResult(job.Success, job.ExitCode, job.ErrorMessage, job.PermissionDenied,
        job.DecidedBy, outputJson, job.LogFile, (long)job.Duration.TotalMilliseconds);
    return new HostReply(request.Id, true, abiResult, null);
}

static string WorkingDirectoryOf(HostRequest request) =>
    request.Arguments?.TryGetProperty("workingDirectory", out var wd) == true && wd.ValueKind == JsonValueKind.String
        ? wd.GetString()!
        : "";

static JsonElement SettingsOf(HostRequest request) =>
    request.Arguments?.TryGetProperty("settings", out var s) == true
        ? s
        : JsonDocument.Parse("{}").RootElement;

static Task WriteLine<T>(T value) => OutputWriter.WriteLine(value);

// A SINGLE WRITE CHOKE POINT, SERIALIZED — its own class because a top-level program's statements
// cannot declare a field, and this lock must outlive any one statement. Console.Out is shared by
// every concurrent HandleRequest task and by the startup lines above; without a lock, two replies
// racing to write could interleave their bytes mid-line and hand the parent a line neither JSON
// parser can read. A SemaphoreSlim rather than `lock` because the write is async
// (Console.Out.WriteLineAsync) and a lock cannot be held across an await.
file static class OutputWriter
{
    private static readonly System.Threading.SemaphoreSlim Gate = new(1, 1);

    public static async Task WriteLine<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        await Gate.WaitAsync();
        try
        {
            await Console.Out.WriteLineAsync(json);
            await Console.Out.FlushAsync();
        }
        finally
        {
            Gate.Release();
        }
    }
}
