/*
 * cxagent ABI plugin contract — v3.
 *
 * A native plugin is a shared library (.so / .dll / .dylib) exporting eight `extern "C"`
 * functions — seven MANDATORY and one OPTIONAL (cxagent_plugin_poll, see below) — name-resolved
 * by the host process (Task 9b), not linked. This header is the authoritative declaration;
 * CxAgent.Core/Core/Plugins/Abi/*.cs mirrors it on the managed side and
 * cxagent.Core/Core/Plugins/Abi/README.md carries the JSON schemas each call exchanges.
 *
 * EVERYTHING THAT CROSSES THIS BOUNDARY IS JSON. A tool's input schema, a call's arguments, a
 * job's result, an error — one encoding both directions, so the host and the plugin agree on a
 * shape without agreeing on a struct layout. See plugins.md, "The boundary is JSON": a struct
 * ABI is frozen the moment it ships; JSON lets an old plugin omit a field the host now defaults,
 * and a new plugin send a field an old host ignores.
 *
 * NO EXCEPTION, NO C++ THROW, MAY CROSS THIS BOUNDARY. `extern "C"` has no unwind tables on the
 * other side; an exception reaching a frontier like this is undefined behaviour, not a checked
 * failure. Every function below reports failure as data — a JSON envelope with "ok": false — and
 * a plugin author who lets a panic or a throw propagate out of one of these functions has a bug,
 * not a supported way to fail a call.
 *
 * OWNERSHIP: every string is allocated by the side that produced it and freed by that side's own
 * allocator. The host's `context_json` / `call_json` arguments are host-owned, valid only for the
 * duration of the call — a plugin retaining either must copy it. Every string this library
 * RETURNS (from describe, start, invoke, stop, poll) is plugin-allocated and MUST be released by
 * the plugin's own `cxagent_plugin_free`, called by the host exactly once per returned pointer,
 * never by the plugin itself. A plugin must never return a static/const literal or a stack buffer
 * from any of these functions — the host always hands the pointer back to cxagent_plugin_free, and
 * freeing memory the plugin did not heap-allocate is undefined behaviour. See "Why a plugin must
 * never return NULL" below for the one exception (a static sentinel `cxagent_plugin_free`
 * recognises and skips) — gate and poll additionally MAY return NULL as an ordinary answer; see
 * each function's own doc.
 */

#ifndef CXAGENT_PLUGIN_H
#define CXAGENT_PLUGIN_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * HANDSHAKE. Returns the plugin contract this library was built against — currently 3.
 *
 * THE SAME NUMBER A MANAGED PLUGIN'S SIDECAR CALLS "pluginContract". One contract covers both
 * loaders; the C entry point keeps its historical name because its signature can never change.
 *
 * Checked BEFORE every other call. The host compares this against a RANGE, not exact equality: a
 * version above what this build understands is refused (it may require something never seen), and
 * a version older than PluginContract.Oldest is refused (support for it was dropped). A plugin
 * within the range may omit a capability a newer contract added — cxagent_plugin_poll among
 * them — without failing the handshake; see the OPTIONAL EXPORT note on cxagent_plugin_poll below
 * for what "may omit" means for one specific function. Its own signature can never change — this
 * is the one function both sides must agree on before agreeing on anything else.
 */
int32_t cxagent_plugin_abi_version(void);

/*
 * SELF-DESCRIPTION. Returns a UTF-8 JSON manifest (see README.md, "describe") — the same shape
 * IPlugin.Load returns managed-side, so a real plugin's manifest looks identical regardless of
 * which loader carries it.
 *
 * Called once, before cxagent_plugin_start. Freshly allocated; released via
 * cxagent_plugin_free. Never NULL — see "Why a plugin must never return NULL" below.
 */
const char* cxagent_plugin_describe(void);

/*
 * LIFECYCLE: START. `context_json` carries the plugin's working directory and its own settings
 * object (see README.md, "context") — never the transcript, the model, or the permission store;
 * plugins.md, "What a plugin is handed at Load" states those are withheld on purpose and the ABI
 * surface withholds them identically.
 *
 * Returns a UTF-8 JSON result envelope (see README.md, "the result envelope"): `{"ok":true}` on
 * success, `{"ok":false,"error":"..."}` on failure. A plugin that fails to start returns ok:false
 * rather than a nonzero exit or a thrown exception — the host has no way to observe either of
 * those from inside a call.
 *
 * `context_json` is host-owned, valid only for the duration of this call. Freshly allocated
 * return value, released via cxagent_plugin_free.
 */
const char* cxagent_plugin_start(const char* context_json);

/*
 * GATE. Decides whether ONE call needs the user's permission, and what the prompt should say.
 * Returns NULL for "no prompt", or a JSON object: {"display": "...", "alwaysAskable": true}.
 *
 * EVERY PLUGIN FROM CONTRACT 2 ON EXPORTS THIS — one of the seven MANDATORY exports — including
 * one that gates nothing, which returns NULL unconditionally: three lines, and one export table
 * for every plugin rather than two shapes a host must tell apart.
 *
 * ONLY CALLED FOR A TOOL THE MANIFEST MARKED "gated": "dynamic". A tool declaring true or false has
 * already answered, and the host does not ask twice.
 *
 * `display` IS WORDING, NOT SCOPE. The host builds the permission itself and decides what an
 * "always" grant would cover; a plugin cannot widen a prompt about its own tool into a standing
 * grant over anything else. Say what THIS call does — the file, the statement — since the arguments
 * are the reason this function exists at all.
 *
 * FAILING IS SAFE AND MEANS "ASK". Return NULL only when the call genuinely needs no prompt: a gate
 * that cannot decide should return malformed output or let the host time out, both of which the
 * host reads as "ask, and offer no standing grant". Never return NULL to signal an error — that
 * reads as "this call is fine".
 *
 * MUST BE FAST. The host calls this synchronously while deciding whether to interrupt the user, and
 * abandons a gate that takes longer than a few hundred milliseconds. Inspect the arguments; do not
 * open a connection.
 */
const char* cxagent_plugin_gate(const char* tool_name, const char* call_json);

/*
 * INVOKE. Runs one call to one of this plugin's own tools, named by `tool_name` — always a name
 * this plugin's own describe() manifest declared; an unrecognised name reaching here is this
 * plugin's own bug, exactly as IPlugin.Invoke's contract states managed-side.
 *
 * `call_json` is the tool's arguments object, always a JSON object, `{}` when the tool takes no
 * arguments — never NULL, so a plugin may parse it unconditionally with no defensive branch. Both
 * `tool_name` and `call_json` are host-owned, valid only for the duration of this call.
 *
 * NO CANCELLATION TOKEN CROSSES THIS BOUNDARY. The managed contract's CancellationToken has no
 * native representation the host can honour mid-call — cxagent_plugin_invoke is synchronous C, and
 * a signal delivered while native code is running cannot safely unwind it any more than an
 * exception can. The host observes cancellation from OUTSIDE the call: see README.md,
 * "Cancellation" for how a cancelled turn is handled without asking this function to notice.
 *
 * Returns a UTF-8 JSON result envelope shaped like CxAgent.Core.Models.JobResult (see README.md,
 * "the result envelope"). Freshly allocated; released via cxagent_plugin_free. Never NULL.
 *
 * MAY BE CALLED CONCURRENTLY, from multiple invocations in flight on the same library — and MAY BE
 * CALLED CONCURRENTLY WITH cxagent_plugin_poll (see below): a poll arriving while an invoke is
 * still running is the ordinary case, not an edge one, since nothing on this host serializes the
 * two. The host takes no lock on this path; a plugin that cannot tolerate concurrent calls must
 * say so in its manifest (a future 'concurrency' hint — no contract has one yet, so every plugin is
 * assumed reentrant) and serialize internally if it is not.
 */
const char* cxagent_plugin_invoke(const char* tool_name, const char* call_json);

/*
 * LIFECYCLE: STOP. Runs before the host process exits. A plugin's own children should already be
 * gone when this returns; the pid record (plugins.md, "Lifecycle") is the fallback for whatever
 * outlives it, not the primary mechanism.
 *
 * Returns a UTF-8 JSON result envelope, `{"ok":true}` or `{"ok":false,"error":"..."}`. Freshly
 * allocated; released via cxagent_plugin_free.
 */
const char* cxagent_plugin_stop(void);

/*
 * OPTIONAL EXPORT: POLL. Contract 3 lets a plugin originate work in its own session
 * (IPluginClient.Submit, on the managed side) — poll exists so a NATIVE plugin can reach for the
 * same ability without this host having to call into unmanaged code on ITS own initiative; the
 * host calls this periodically instead, and the plugin hands back a request only when it has one.
 *
 * THIS EXPORT IS OPTIONAL, unlike every function above it. A library built before contract 3 (or
 * one that never originates work) omits it entirely, and the host loads it exactly as it always
 * has — see "HANDSHAKE" above. Resolved by name at load time; its absence is not a load failure.
 *
 * MAY RETURN NULL, AND ORDINARILY DOES — an explicit exception to "a plugin must never return
 * NULL" (see OWNERSHIP, above): NULL here means "nothing to say right now", the state a plugin
 * spends nearly all of its time in. A plugin that allocated an empty object instead, to avoid ever
 * returning NULL, would hand the host a fresh string to parse and free on every poll for no
 * information gained.
 *
 * MAY BE CALLED CONCURRENTLY WITH cxagent_plugin_invoke — see the note on invoke, above. Returns a
 * freshly allocated UTF-8 JSON string when the plugin has something to send, released via
 * cxagent_plugin_free like every other non-NULL return.
 *
 * THE NON-NULL SHAPE, AS OF CONTRACT 3:
 *
 *   {"goal":"run the tests","wantResult":false}
 *
 * "goal" is required — what to ask the agent to do, as a user would type it. "wantResult" is
 * optional (defaults false) and, on this contract, MUST be false or omitted: a value of true is
 * REFUSED BY NAME, not silently downgraded — see WHY WANTRESULT IS REFUSED, below. There is no
 * "kind" field. The spec this contract grew from named two shapes, "Submit and Answer"; Answer was
 * the permission answerer and is cut, so submit is the only shape a plugin ever sends and nothing
 * here discriminates between two.
 *
 * WHY WANTRESULT IS REFUSED. Answering it would mean holding a call open across a turn that may
 * run for minutes, over a pipe whose only inbound channel is a poll THIS PLUGIN itself controls —
 * the answer would have to arrive on some later poll, and the plugin would need its own
 * correlation to match it back to the submit that asked for it: a second correlation layer inside
 * a protocol that already has one (see HOST-TO-PLUGIN ID CORRELATION, below), for a capability no
 * plugin has yet asked for. The field exists on the wire so a later contract can serve it without
 * changing this shape again; this contract does not walk through that door.
 *
 * FIRE-AND-FORGET, THEREFORE, IS THE WHOLE CONTRACT: a plugin that polls a submit gets no reply on
 * this channel, ever, on contract 3 — not success, not failure, not the turn's own result. A
 * plugin that needs to know what happened has nothing here to ask.
 *
 * HOST-TO-PLUGIN ID CORRELATION, AND WHY THIS EXPORT NEEDS NONE OF ITS OWN. Every call the host
 * makes INTO this library (start/invoke/gate/stop, carried over cxagent-plugin-host's own internal
 * wire protocol, HostProtocol.cs) is tagged with a positive id the host assigns and matches its own
 * reply against. A submit crossing the OTHER direction — plugin to host — is tagged with a
 * NEGATIVE id instead, assigned by the host process forwarding this call, not by this library: a
 * negative id can never collide with a positive one, so the host's reader loop tells "a reply to
 * something it asked" from "something the plugin volunteered" by sign alone, with no new envelope
 * and no correlation this library has to construct. Nothing described above is visible to a plugin
 * author — cxagent_plugin_poll's own C signature carries no id at all — it exists here only so the
 * shape of "fire-and-forget, no reply channel" is not mysterious.
 */
const char* cxagent_plugin_poll(void);

/*
 * Releases a string previously returned by cxagent_plugin_describe / _start / _invoke / _stop /
 * _poll (when poll's return was not NULL). Called by the host exactly once per returned pointer,
 * in a `finally`-equivalent — always, including when the envelope failed to parse. NEVER called by
 * the plugin on its own output; the host owns the release side of every pointer this library hands
 * back.
 */
void cxagent_plugin_free(const char* ptr);

#ifdef __cplusplus
}
#endif

#endif /* CXAGENT_PLUGIN_H */
