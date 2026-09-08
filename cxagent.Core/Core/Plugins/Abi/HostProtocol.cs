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

        /// <summary>One per-call permission decision — see <c>cxagent_plugin_gate</c>.</summary>
        Gate,
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
/// <param name="Id">The request this answers — replies may arrive out of order, so a caller matches
/// on this rather than on arrival.</param>
/// <param name="Ok">Whether the CALL crossed the boundary, which is a different question from
/// whether the work succeeded — see <see cref="AbiJobResult"/> for the second.</param>
/// <param name="Result">The tool's own result, for an invoke; null for every other kind.</param>
/// <param name="Error">Why the call could not be made, when <paramref name="Ok"/> is false.</param>
/// <param name="Gate">A gate reply's payload — null for every other request kind, and null from a
/// gate that decided this call needs no prompt.</param>
public sealed record HostReply(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("result")] AbiJobResult? Result,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("gate")] AbiGate? Gate = null);

/// <summary>
/// What <c>cxagent_plugin_gate</c> returns, mirroring <see cref="PluginGate"/>. Carries WORDING
/// ONLY: the permission's kind and its "always" rule are built host-side, so a plugin cannot widen
/// a prompt about its own tool into a grant over anything else.
/// </summary>
public sealed record AbiGate(
    [property: JsonPropertyName("display")] string Display,
    [property: JsonPropertyName("alwaysAskable")] bool AlwaysAskable = true);

/// <summary>
/// The ONE PLUGIN-ORIGINATED SHAPE — what a non-null <c>cxagent_plugin_poll</c> return means,
/// forwarded by the host process as an ordinary <see cref="HostProtocol"/> line with a NEGATIVE
/// <see cref="HostRequest.Id"/> (see cxagent_plugin.h's own "OPTIONAL EXPORT: POLL"). The spec that
/// named this contract talked about "Submit and Answer"; Answer was the permission answerer, which
/// is cut from contract 3, so this is the only shape a plugin ever sends — there is no <c>kind</c>
/// enum here because there is nothing to discriminate between yet.
///
/// <para><see cref="WantResult"/> IS CARRIED BUT REFUSED. The shape has room for a plugin to ask for
/// its turn's text because a later contract may serve that; THIS contract does not, because
/// answering it would mean holding a call open across a turn that may run minutes, over a pipe
/// whose only inbound channel is a poll the plugin itself controls — the plugin would need its own
/// correlation to match a later poll's answer back to the submit that asked for it, a second
/// correlation layer inside a protocol that already has one, for a capability no plugin has yet
/// asked for. <see cref="AbiPlugin"/> refuses <c>wantResult:true</c> by name rather than silently
/// downgrading it to false, so a native author sees why their result never came back instead of
/// guessing.</para>
/// </summary>
/// <param name="Id">NEGATIVE, always — the whole correlation trick <see cref="HostProtocol"/>'s own
/// doc names: a host-assigned <see cref="HostRequest.Id"/> only ever counts up from 1
/// (<see cref="AbiHostProcess.Send"/>), so a negative id can never collide with one outstanding, and
/// <see cref="AbiHostProcess"/>'s reader loop tells "a reply to something I asked" from "something
/// the plugin volunteered" by sign alone. Assigned by the host process (<c>cxagent.PluginHost</c>'s
/// own poll loop), not by <see cref="AbiHostProcess"/> — unlike every <see cref="HostRequest"/>,
/// this line originates on the OTHER side of the pipe.</param>
/// <param name="Goal">What to ask the agent to do, as a user would type it — forwarded verbatim to
/// <see cref="IPluginClient.Submit"/>.</param>
/// <param name="WantResult">Always refused when true — see this record's own doc.</param>
public sealed record AbiSubmit(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("goal")] string Goal,
    [property: JsonPropertyName("wantResult")] bool WantResult = false);

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
