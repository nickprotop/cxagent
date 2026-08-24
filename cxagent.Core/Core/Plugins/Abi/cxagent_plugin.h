/*
 * cxagent ABI plugin contract — v1.
 *
 * A native plugin is a shared library (.so / .dll / .dylib) exporting five `extern "C"`
 * functions, name-resolved by the host process (Task 9b), not linked. This header is the
 * authoritative declaration; CxAgent.Core/Core/Plugins/Abi/*.cs mirrors it on the managed side
 * and cxagent.Core/Core/Plugins/Abi/README.md carries the JSON schemas each call exchanges.
 *
 * EVERYTHING THAT CROSSES THIS BOUNDARY IS JSON. A tool's input schema, a call's arguments, a
 * job's result, an error — one encoding both directions, so the host and the plugin agree on a
 * shape without agreeing on a struct layout. See PLUGINS.md, "The boundary is JSON": a struct
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
 * RETURNS (from describe, start, invoke, stop) is plugin-allocated and MUST be released by the
 * plugin's own `cxagent_plugin_free`, called by the host exactly once per returned pointer, never
 * by the plugin itself. A plugin must never return a static/const literal or a stack buffer from
 * any of these functions — the host always hands the pointer back to cxagent_plugin_free, and
 * freeing memory the plugin did not heap-allocate is undefined behaviour. See "Why a plugin must
 * never return NULL" below for the one exception (a static sentinel `cxagent_plugin_free`
 * recognises and skips).
 */

#ifndef CXAGENT_PLUGIN_H
#define CXAGENT_PLUGIN_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * ABI HANDSHAKE. Returns the ABI version this library was built against — currently 1.
 *
 * Checked BEFORE every other call. The host compares this against the versions it understands
 * with EXACT EQUALITY, never a floor: a host built for v1 cannot know whether a v2 plugin omits a
 * field whose absence would silently change behaviour, so it refuses a mismatch cleanly rather
 * than guessing. Its own signature can never change — this is the one function both sides must
 * agree on before agreeing on anything else.
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
 * PLUGINS.md, "What a plugin is handed at Load" states those are withheld on purpose and the ABI
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
 * MAY BE CALLED CONCURRENTLY, from multiple invocations in flight on the same library. The host
 * takes no lock on this path; a plugin that cannot tolerate concurrent calls must say so in its
 * manifest (a future 'concurrency' hint — v1 has none, so v1 plugins are assumed reentrant) and
 * serialize internally if it is not.
 */
const char* cxagent_plugin_invoke(const char* tool_name, const char* call_json);

/*
 * LIFECYCLE: STOP. Runs before the host process exits. A plugin's own children should already be
 * gone when this returns; the pid record (PLUGINS.md, "Lifecycle") is the fallback for whatever
 * outlives it, not the primary mechanism.
 *
 * Returns a UTF-8 JSON result envelope, `{"ok":true}` or `{"ok":false,"error":"..."}`. Freshly
 * allocated; released via cxagent_plugin_free.
 */
const char* cxagent_plugin_stop(void);

/*
 * Releases a string previously returned by cxagent_plugin_describe / _start / _invoke / _stop.
 * Called by the host exactly once per returned pointer, in a `finally`-equivalent — always,
 * including when the envelope failed to parse. NEVER called by the plugin on its own output; the
 * host owns the release side of every pointer this library hands back.
 */
void cxagent_plugin_free(const char* ptr);

#ifdef __cplusplus
}
#endif

#endif /* CXAGENT_PLUGIN_H */
