using System.Text.Json.Serialization;

namespace CxAgent.Core.Plugins.Abi;

/// <summary>
/// The wire protocol between <c>cxagent-plugin-host</c> and <see cref="AbiPlugin"/>, its managed
/// parent — NEWLINE-FRAMED JSON on stdin/stdout, one line in, one line out, the same shape
/// <see cref="CxAgent.Core.Mcp.McpClient"/> already uses to talk to an MCP server over a pipe. This
/// is a SEPARATE vocabulary from the ABI JSON in <c>Abi/README.md</c> — that JSON is what the host
/// process exchanges with the native library it loads; this one is what it exchanges with its own
/// parent, and exists because the parent needs to say WHICH of the four ABI calls to make and
/// attach a request id, neither of which the ABI envelope itself carries.
///
/// <para>LIVES IN CORE, NOT THE HOST PROJECT, even though the host process is the only thing that
/// implements the server side of it — <see cref="AbiHostProcess"/> (the client side, in
/// <see cref="AbiPlugin"/>) needs these same types, and Core cannot reference
/// <c>cxagent.PluginHost</c> without inverting that project's own dependency on Core. One
/// vocabulary, referenced from both processes, is the alternative to two copies drifting apart.</para>
///
/// <para>ONE LINE OF STARTUP OUTPUT PRECEDES ANY REQUEST: a <see cref="HostReady"/> or
/// <see cref="HostStartupFailure"/> line, written once, before the host process reads its first
/// request. The parent must read that line before sending anything — see <see cref="AbiHostProcess.Launch"/>.</para>
/// </summary>
public static class HostProtocol
{
    /// <summary>Request kinds the host understands. <see cref="Start"/>/<see cref="Invoke"/>/
    /// <see cref="Stop"/> map 1:1 onto the three post-handshake ABI calls; the ABI handshake and
    /// describe already ran before <see cref="HostReady"/> was written, so nothing here re-asks for
    /// them.</summary>
    public enum RequestKind
    {
        Start,
        Invoke,
        Stop,
    }
}

/// <summary>
/// One line the parent sends. <see cref="ToolName"/>/<see cref="Arguments"/> are used only for
/// <see cref="HostProtocol.RequestKind.Invoke"/> and ignored otherwise — a single shape for all
/// three requests rather than three near-identical ones, matching how <c>AbiResultEnvelope</c>
/// itself is one shape for start/invoke/stop replies.
/// </summary>
public sealed record HostRequest(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("kind")] HostProtocol.RequestKind Kind,
    [property: JsonPropertyName("toolName")] string? ToolName,
    [property: JsonPropertyName("arguments")] System.Text.Json.JsonElement? Arguments);

/// <summary>
/// The reply to one <see cref="HostRequest"/>, correlated by <see cref="Id"/> — replies MAY arrive
/// out of order, because <c>cxagent_plugin_invoke</c> may run concurrently (cxagent_plugin.h,
/// "MAY BE CALLED CONCURRENTLY") and the host does not serialize invokes onto one at a time. Carries
/// the SAME <c>ok</c>/<c>result</c>/<c>error</c> shape as <c>AbiResultEnvelope</c> deliberately: the
/// parent already knows how to read that shape, and translating it into a second one here would be
/// exactly the kind of near-duplicate shape the ABI's own single-envelope design avoids.
/// </summary>
public sealed record HostReply(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("result")] AbiJobResult? Result,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>
/// The one line the host process writes before reading any request — the library loaded, the ABI
/// version handshake passed, and <c>cxagent_plugin_describe</c> returned a manifest that parsed.
/// <see cref="Manifest"/> is the SAME <see cref="PluginManifest"/> a managed plugin's <c>Load</c>
/// returns, so <see cref="AbiPlugin"/> can hand it to <c>PluginRegistry</c> unchanged — see
/// Abi/README.md, "describe": "a real plugin's manifest looks identical regardless of which loader
/// carries it."
/// </summary>
public sealed record HostReady(
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("manifest")] PluginManifest? Manifest);

/// <summary>
/// Written INSTEAD OF <see cref="HostReady"/> when the library could not be loaded, the ABI version
/// handshake failed, or <c>cxagent_plugin_describe</c> returned something <c>AbiCodec.ParseManifest</c>
/// refused — every case in cxagent_plugin.h and Abi/README.md that must fail CLEANLY rather than
/// guess at an unfamiliar shape. The host process exits immediately after writing this line; there
/// is nothing left for it to serve requests with.
/// </summary>
public sealed record HostStartupFailure(
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("error")] string Error);
