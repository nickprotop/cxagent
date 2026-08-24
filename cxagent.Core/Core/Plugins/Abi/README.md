# The ABI surface (Task 9a)

The `extern "C"` functions are `cxagent_plugin.h`, in this directory. This file is the JSON
vocabulary they exchange, and the reasoning behind it. Nothing here builds a host process (9b) or
a managed shim (9c) — this is the contract those two are built against, so it is written down and
locked by tests before either exists.

## The four calls, and what crosses each one

| C function | Managed equivalent | JSON in | JSON out |
| --- | --- | --- | --- |
| `cxagent_plugin_abi_version` | — (handshake only) | — | an `int32_t`, not JSON |
| `cxagent_plugin_describe` | `IPlugin.Load`'s returned manifest | — | `PluginManifest` |
| `cxagent_plugin_start` | `IPlugin.Start` | `AbiPluginContext` | `AbiResultEnvelope` (void) |
| `cxagent_plugin_invoke` | `IPlugin.Invoke` | `AbiInvokeCall` | `AbiResultEnvelope` (JobResult) |
| `cxagent_plugin_stop` | `IPlugin.Stop` | — | `AbiResultEnvelope` (void) |

`IPlugin.Load` is split across two ABI calls deliberately: `describe` returns the manifest with no
context (matching `plugin_describe` in ConsoleEx's spec, called before the plugin has seen
anything plugin-specific), and `start` is where the plugin first receives its working directory
and settings. The managed `IPlugin.Load(context, ct)` folds both into one call because a managed
plugin can safely do both in one method; a native plugin cannot describe itself meaningfully
before its own static initializers have run, but *can* before it has a working directory, so
splitting the two costs nothing and gives the host a manifest to validate before it commits to
starting the plugin at all.

## `abiVersion` — the handshake

`cxagent_plugin_abi_version()` returns a bare `int32_t`, not JSON. This is the one function
exempt from "everything is JSON," for the same reason ConsoleEx's spec states: **the version
check has to precede parsing**, so it cannot itself depend on a JSON shape the host might not
understand yet. Its signature can never change.

The current constant is `1`. **Exact equality, not a floor** — see `cxagent_plugin.h`'s own
comment. A host meeting version `2` refuses the load and names both versions in its error; it
does not attempt to read a v2 manifest with v1 assumptions.

## `describe` — the manifest

```json
{
  "abiVersion": 1,
  "name": "lsp-rust",
  "version": "1.0.0",
  "instructions": "These tools talk to a running language server...",
  "spawns": true,
  "tools": [
    {
      "name": "lsp_definition",
      "description": "Finds where the symbol at a file position is declared.",
      "inputSchema": { "type": "object", "properties": { "...": "..." } },
      "gated": false
    }
  ]
}
```

Every field but `abiVersion` mirrors `CxAgent.Core.Plugins.PluginManifest` and
`PluginToolManifest` field for field — same names, same optionality — because an ABI plugin's
manifest must be indistinguishable from a managed one to everything downstream of the loader:
`PluginRegistry`, the collision matrix, and the load-gate prompt read a `PluginManifest`, not
"a managed one" or "an ABI one." `abiVersion` is the one addition, carried in the manifest body
as well as returned by the handshake function — deliberately redundant, exactly as ConsoleEx's
spec keeps them redundant (§7.1, "On `abiVersion` appearing twice"): the handshake function is
checked first, before the manifest is trusted enough to parse, and the in-body copy lets a
mismatch between the two be caught as a manifest error with a clear message rather than silently
trusting whichever the host happened to read.

**Unknown top-level keys are refused by name, not ignored** — this already matches
`PluginManifest.Parse`'s existing behaviour (`commands`, `completions`, `providers`, `observers`
each report "this build does not service" rather than being silently dropped) and the ABI surface
changes nothing about it. A native plugin declaring a hook point v1 doesn't service is told so at
load, the same as a managed one.

## `context` — what `start` receives

```json
{
  "workingDirectory": "/home/nick/source/cxagent",
  "settings": { "server": "rust-analyzer", "args": ["--log-level", "warn"] }
}
```

Deliberately smaller than `IPluginContext`. Three of that interface's five members have no JSON
representation and are withheld on purpose, not merely because they are hard to serialize:

- **the transcript** — never offered in any form, matching `IPluginContext`'s own contract.
- **the logger** — a native plugin has no callback into the host to log through (see
  "What deliberately does not cross" below); it writes to its own stderr, which the host process
  (9b) captures and attributes to the plugin by name. No JSON shape needed.
- **the lifetime cancellation token** — see "Cancellation" below.
- **`RegisterChildProcess`** — a native plugin registers a spawned process the same way, but as an
  *outbound* message rather than an inbound context field; see "What this leaves room for."

So `context_json` carries exactly the two fields a plugin needs to start doing its own work:
`workingDirectory` (a string) and `settings` (the plugin's own settings object from config,
verbatim — passed through unparsed, exactly as `IPluginContext.Settings` is a raw `JsonElement`
today).

## `call` — what `invoke` receives

```json
{
  "toolName": "lsp_definition",
  "arguments": { "file": "src/main.rs", "line": 12, "character": 4 }
}
```

`arguments` mirrors `JobParameters.Values` — a flat-ish JSON object, the tool's own declared
input schema. Unlike ConsoleEx's closed wire-type vocabulary (§7, `i64`/`f64`/`bool`/`string`/...),
this surface does **not** introduce a typed parameter list. `JobParameters.Get<T>` already
tolerates untyped, occasionally mistyped, LLM-sourced JSON — string-encoded numbers, `0`/`1` for
bool — and a native plugin receiving the same untouched JSON object gets the same latitude a
managed plugin's `call.Get<T>("line")` already has. Introducing a second, stricter type system at
the ABI boundary would fight `JobParameters`'s own design rather than extend it. `arguments` is
always a JSON object, never `null` and never omitted — an argument-less tool receives `{}`, so a
plugin may index into it unconditionally exactly as `cxagent_plugin.h` states for `call_json`.

`toolName` is a plain string, not re-validated against the manifest by the wire format — the same
division of responsibility `IPlugin.Invoke`'s own doc states: "an unrecognised name reaching this
method is this plugin's own bug, not a name the caller must additionally validate."

## The result envelope

One shape answers `start`, `invoke`, and `stop` — deliberately one envelope for all three, because
a host reading three different shapes for "did this call succeed" is the kind of near-duplication
this design avoids elsewhere (see `PluginManifest`'s single sidecar-and-runtime shape).

```json
{ "ok": true, "result": { "success": true, "output": { "locations": [] } } }
{ "ok": true }
{ "ok": false, "error": "language server is not running." }
```

| Field | When | Rule |
| --- | --- | --- |
| `ok` | always | required boolean. Absent or non-boolean is a malformed envelope. |
| `result` | `ok: true`, from `invoke` only | a `JobResult`, below. Omitted for `start`/`stop`, whose managed equivalents return `Task`, not `Task<JobResult>`. |
| `error` | `ok: false` | required, non-empty string. |

**`ok` and `JobResult.Success` are not the same bit, and both exist for a reason.** `ok: false`
means the CALL ITSELF failed — the plugin could not produce a `JobResult` at all: a malformed
argument, an unknown tool name reaching a plugin that treats that as a hard bug, a serialization
failure. `ok: true, result: { success: false, errorMessage: "..." }` means the call completed and
the *tool* failed on its own terms — a language server timeout, a file not found — exactly the
distinction `IPlugin.Invoke`'s existing managed contract already draws by returning a `JobResult`
with `Success: false` rather than throwing. Collapsing the two into one boolean would lose
`ErrorMessage` vs. `PermissionDenied` vs. a hard ABI-level fault as one bucket, which the managed
side never had to make peace with because C# exceptions and return values were always two
channels. The `result` JSON object is:

```json
{
  "success": true,
  "exitCode": 0,
  "errorMessage": null,
  "permissionDenied": false,
  "decidedBy": null,
  "output": { "locations": [] },
  "logFile": null,
  "durationMs": 42
}
```

Field-for-field `CxAgent.Core.Models.JobResult`, with `Duration` (a `TimeSpan`) carried as
`durationMs` (an integer) — the one field with no natural JSON primitive, sent as milliseconds
because that is the unit every duration already displayed to a user in this codebase uses. Every
other field is a direct name match. `Output` is `Dictionary<string, object?>`, i.e. an arbitrary
JSON object — the same escape hatch `JobParameters`/`JobResult` already use managed-side; nothing
here narrows it to a closed type vocabulary the way ConsoleEs's `ServiceParameter.Type` does,
because `JobResult.Output` was never one.

**Unknown envelope or result fields are ignored**, not rejected — the forward-compatibility seam:
a v2 plugin may add a diagnostic field a v1 host does not read.

### Every failure mode, and what the host does

| Native returns | Host behaviour |
| --- | --- |
| `{"ok":false,"error":"..."}` | the call fails; `error` becomes the tool's `ErrorMessage` |
| `{"ok":false}` — no `error` | fails with a generated message naming the plugin and function |
| `{"ok":true,"result":{...}}` from `invoke`, valid `JobResult` shape | the `JobResult`, verbatim |
| `{"ok":true}` from `start`/`stop` | success, no result to read |
| `{"ok":true}` from `invoke` (missing `result`) | fails — `invoke` promises a `JobResult` and did not send one |
| `NULL` pointer | fails — a plugin must always return an envelope; see `cxagent_plugin.h`, "Why a plugin must never return NULL" |
| empty string, or invalid JSON | fails, quoting a bounded prefix of what was returned |
| valid JSON but not an object | fails — the envelope must be a JSON object |
| object without `ok` | fails — malformed envelope |

None of these throws a C# exception across the plugin-to-host process boundary — there is no such
boundary to throw across; the host process (9b) is what turns a failed envelope, a closed pipe, or
a dead process into a `JobResult { Success = false }` the rest of Core sees exactly like any other
tool failure. That translation is 9b's job; this surface only defines what the envelope means once
it arrives.

## Memory ownership

**One rule, stated once, applied uniformly to every string that crosses:** the side that
allocated a string frees it, using its own allocator. Concretely:

- `context_json` and `call_json` (host → plugin): host-owned, valid only for the duration of the
  call the plugin is currently inside. A plugin that needs the value after the call returns must
  copy it before returning.
- Every return value (`describe`, `start`, `invoke`, `stop`): plugin-owned. The host copies the
  UTF-8 bytes out into its own managed string and then calls `cxagent_plugin_free` on the original
  pointer — always, in a `finally`, including when the envelope failed to parse. The plugin's own
  `free()` (or whatever its runtime's deallocator is) runs inside `cxagent_plugin_free`, never the
  host's.

This is exactly ConsoleEx's §10 rule, unchanged, because there is no plugin-specific reason to
diverge: it is the only ownership rule that has no ambiguous case, which is the property an ABI
boundary needs from it.

## Cancellation

**Does not cross the boundary as a signal `cxagent_plugin_invoke` can observe mid-call.** The
managed `IPlugin.Invoke(toolName, call, context, ct)` takes a `CancellationToken`; the ABI
equivalent has no parameter for it, and `cxagent_plugin.h`'s own comment says why: the function is
synchronous C, and there is no safe way to interrupt native code already running inside it — no
unwind path exists at this frontier for a token firing mid-call, the same reason an exception
cannot cross it.

**Cancellation is instead a HOST-SIDE consequence, not a native-side interrupt.** When a call's
`CancellationToken` is cancelled while `cxagent_plugin_invoke` is still in flight:

- The host process (9b) does not attempt to signal, interrupt, or kill the native call. It stops
  *waiting* for the reply — the call is abandoned from the host's perspective — and returns a
  `JobResult { Success = false }` to the caller immediately, matching how `PluginRegistry.UnwireAsync`
  already treats an abandoned managed `Stop`: "the await here is abandoned, not cancelled."
  The native call may still be running when this happens; it is expected to finish and its
  envelope is discarded rather than delivered to a caller that has moved on.
- **A plugin that wants prompt cancellation implements it as an operation, not a signal** — the
  same pattern ConsoleEx's spec settles on (§17.4, "cancel is just an operation"): a plugin
  exposing long-running work may declare its own `_cancel`-shaped tool and poll a flag between
  steps of its own long call. Nothing in this surface prevents that; nothing in this surface
  builds it either, because it is policy the plugin author owns, not a mechanism the ABI can offer
  uniformly.

This is a real place where the managed contract does not translate across the ABI without
distortion, and it is called out here rather than quietly bent: a managed plugin CAN observe
cooperative cancellation mid-`Invoke`; a native one CANNOT be asked to, only abandoned by the
host. The asymmetry is accepted rather than closed, because closing it would mean either blocking
`plugin_invoke` behind a lock that lets the host inject a signal (reintroducing exactly the
coupling `PLUGINS.md`'s "the host does not serialize invokes" language rejects for the same
reason ConsoleEx gives) or running every native call pre-emptively on a killable OS thread
(possible, but a 9b concern about how the host manages its own process/thread pool, not a change
to this wire contract).

## Where this differs from ConsoleEx's `abi-plugin.md`, and why

Agreement, where it exists, is listed first because it is the stronger signal — two independent
designs solving the same boundary problem landing on the same shape is evidence the shape is
right, not a coincidence to explain away.

**Agrees:**
- Five fixed, name-resolved `extern "C"` exports; a version-handshake function whose own signature
  never changes, checked with exact-equality before anything else is trusted.
- One ownership rule: producer allocates, producer frees, via a dedicated `plugin_free` — no
  cross-allocator frees, ever.
- A single result envelope (`{"ok": bool, ...}`) for success and failure sharing one channel
  instead of an errno/out-parameter/exception split.
- No exception/panic may cross the boundary; every native implementation must catch it internally
  and report failure as envelope data.
- Concurrent `invoke` calls are the plugin's problem to serialize if it cannot tolerate them; the
  host takes no lock.
- No streaming, no host callback that returns a value, no partial results.

**Differs, deliberately:**

1. **Explicit lifecycle functions (`start`, `stop`) instead of a single stateless `Execute`.**
   ConsoleEx's native services have no lifecycle beyond construction — `Execute` is the only call.
   cxagent's `IPlugin` has `Load → Start → Invoke* → Stop`, and an LSP plugin's whole reason for
   existing is the state a language server process holds between calls (PLUGINS.md, "What a
   plugin is": "the plugin IS the executor its tools share"). Collapsing lifecycle into `Execute`
   calls would leave a native plugin no ABI-defined moment to start its child process, which
   `cxagent-lsp` (Task 8) needs and does today via `IPlugin.Start`.

2. **No `services[]` / operation-list manifest shape, no closed wire-type vocabulary
   (`i64`/`f64`/`bool`/`string`/`bytes`/`json`).** ConsoleEx's manifest exists to rehydrate
   `ServiceOperation`/`ServiceParameter` — typed C# metadata objects with no JSON Schema
   equivalent in that codebase. cxagent already has a JSON Schema per tool
   (`ToolDefinition.InputSchema`, a `JsonElement`) flowing to the model as part of the existing
   tool-calling contract; inventing a second, narrower type system for the ABI boundary alone
   would mean a plugin author writes the same shape twice, in two vocabularies, and the two can
   drift. `PluginToolManifest.InputSchema` — literal JSON Schema — is what crosses instead, for
   both loaders identically.

3. **No `plugin_kind` pre-load probe.** ConsoleEx's probe answers "is this a plugin at all,
   without risking `plugin_describe`" for a directory-scan of many candidate `.so` files sharing a
   folder with unrelated binaries. cxagent's plugin discovery (PLUGINS.md, "Configuration") is
   name-keyed from `config.json` — the caller already knows a named entry is meant to be a
   plugin before it ever resolves a symbol, so there is nothing a pre-`describe` probe would rule
   out that config resolution has not already settled. Symbol resolution failure
   (`cxagent_plugin_describe` unresolvable) already answers "not our file" without a sixth export.

4. **The manifest's shape mirrors `PluginManifest` exactly, not a services/operations schema.**
   Direct consequence of #2: because both loaders must produce the identical `PluginManifest`
   `PluginRegistry` consumes, the wire manifest is that record's JSON projection rather than an
   ABI-native shape translated into it after the fact.

5. **`Output`/result values are an open JSON object (`JobResult.Output`), not the closed
   scalar/array/`json`-escape-hatch vocabulary of ConsoleEx §7.** `JobResult.Output` was already an
   untyped `Dictionary<string, object?>` on the managed side before any ABI existed — there is no
   narrower type this surface could impose without also changing the managed contract it mirrors,
   which is out of this task's scope.

6. **Cancellation gets a stated asymmetry instead of an implicit absence.** ConsoleEx's spec
   doesn't carry a cancellation token on either side of `Execute` (§5.1, "no async" — a blocking
   call, full stop) because ConsoleEx services have no cancellable managed counterpart to be
   asymmetric with. `IPlugin.Invoke` DOES take a `CancellationToken`, so this surface has to say
   explicitly what ConsoleEx had no reason to: cancellation is host-side abandonment, not a
   native-observable signal. See "Cancellation" above.
