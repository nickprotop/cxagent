# CxAgent.Core — API reference

Every public member, with its parameters and who calls it. For the mental model — what owns what,
how a permission request reaches you — read the [README](../README.md) first; this page assumes it.

**Namespaces**

| | |
|---|---|
| `CxAgent.Core.Sessions` | `Session`, `SessionManager`, ports, observers, `WorkingMode` |
| `CxAgent.Core.Agents` | `Agent`, agent types, sub-agent spawning |
| `CxAgent.Core.Llm` | providers, `AgentConfig`, `ResolvedConfig`, the token ledger |
| `CxAgent.Core.Permissions` | the gate, the policy, the rules store |
| `CxAgent.Core.Commands` | `CommandStatus`, the command table |
| `CxAgent.Core.Storage` | `AppPaths`, the resume and usage databases |

---

# SessionManager

One per process. Owns what sessions share and opens them.

## Create

```csharp
static SessionManager Create(
    AppPaths paths,
    Func<PermissionRulesStore, IPermissionGate>? buildGate = null,
    McpToolset? mcp = null,
    ResolvedConfig? config = null)

static SessionManager Create(ProcessSetup setup)
static SessionManager Over(SharedServices shared, PermissionRulesStore? rules = null, …)
```

| Parameter | |
|---|---|
| `paths` | where config and data live. Everything below is created under it |
| `buildGate` | **a delegate that builds a gate, not a gate** — the gate needs the rules store, and the store is created inside. Null means no gating at all |
| `mcp` | connected MCP servers, or null |
| `config` | the process default model; a session may override it at `Open` |

`Over(...)` takes services somebody else built — for a host running several managers, or a test.

**Creates:** the resume database, the usage archive, the log directory manager, the permission rules
store, the command registry. **Disposes:** all of it, on `Dispose`.

## Open

```csharp
Session Open(string workingDirectory, ResolvedConfig? config, SessionPorts ports, WorkingMode? mode = null)
Session Open(string workingDirectory, SessionPorts ports, WorkingMode? mode = null)
Session Open(Session session, ResolvedConfig? config, SessionPorts ports, WorkingMode? mode = null)
```

| Parameter | |
|---|---|
| `workingDirectory` | the permission boundary, and what relative paths resolve against |
| `config` | which model. Null takes the manager's `Config` |
| `ports` | your observer and tool observer — see [SessionPorts](#sessionports) |
| `mode` | null takes `WorkingMode.Default`: fan-out delegation, always-ask edits |

The third overload **re-wires an existing session** over a new configuration, keeping the
conversation. That is what a model switch or a settings change uses.

**Creates, per session:** the `Session`, its `AgentHost` and `Agent`, the job registry (each tool
wrapped in the gate), the sub-agent spawner, the agent-type catalog.

## The rest

| Member | | Called by |
|---|---|---|
| `Commands` | the registry every session dispatches through | you, to register front-end commands |
| `Values(set, workingDirectory?)` | completion values for a command argument | a palette or tab completion |
| `Sessions` | every session this manager has open | a front end with tabs |
| `Resume(session, snapshot, rewire?)` | restore a stored conversation | `Session.ListSessions` on `/sessions resume`, or you |
| `Rewire` | the delegate `Resume` uses when none is passed | set once by your front end |
| `Close(session)` | dispose the host, release the turn scope | you, when a session ends |
| `Shared` · `Rules` · `Config` | what was built at construction | diagnostics |

---

# Session

## Running a turn

```csharp
SubmitOutcome Submit(string text, string? echo = null)
SubmitOutcome Initialise()
bool          CancelTurn()
bool          IsBusy
bool          RefuseIfBusy()
```

### `Submit(text, echo?)`

| Parameter | |
|---|---|
| `text` | what the model receives |
| `echo` | what the USER sees, when it differs. `/init` sends paragraphs of briefing and displays `"/init"` — echoing the briefing would attribute words to them they never wrote |

**Returns** a `SubmitOutcome`, synchronously:

| | Carries | Meaning |
|---|---|---|
| `Started` | `Task Turn` | a turn began. Await it, or attach a continuation |
| `Queued` | — | a turn was already running; the text went to the queue and `Pending` fired |
| `NoAgent` | — | no model is wired |

**Raises:** `UserTurnAdded` then `AssistantTurnBegan` on your observer; `TokensUpdated` and
`ContextUsedUpdated` as the turn proceeds; `TurnCompleted` at the end.

### `Initialise()`

Runs `/init`: sends a briefing about the working directory, displays `"/init"`. Same return type.

### `CancelTurn()`

Stops the provider stream, the tool loop, and any shell process (whose runner kills its whole process
tree). Says `"Stopped."` through your observer, announces `TurnCancelled`, and hands anything queued
back through `Cancelled`. **Returns false when no turn was running** — a stop arriving a moment late
is ordinary, not an error.

The session, its context and its MCP servers survive.

### `IsBusy` / `RefuseIfBusy()`

`IsBusy` is true from the moment a turn is accepted until it ends, however it ends. `RefuseIfBusy()`
is the same test but **says so through the observer** — for an operation that must decline rather
than queue.

## The queue

**`Submit` fills this for you.** These members are the mechanism; call them directly only if you are
driving the queue yourself.

```csharp
event Action<string, string>? Pending;    // (whole, justAdded)
event Action<string>?         Drained;    // the turn took it
event Action<string>?         Cancelled;  // taken back, never sent

void    Steer(string text)          // append; Submit calls this when busy
void    CancelPending()             // empty it, hand it back through Cancelled
string? PendingSteer                // what is waiting, or null
string? TakePendingSteer()          // take it exactly once; raises Drained
```

`Pending` carries **both** the whole queue and the line just added, because only that moment has
both — a subscriber holding one cannot derive the other without reading the queue back.

`Drained` and `Cancelled` are separate although both empty the queue: one means *the real message is
coming, remove the placeholder*, the other means *put it back where it can be edited*.

Events are raised **outside** the queue's lock; subscribers marshal to their own thread.

## Watching it work

```csharp
event EventHandler<int>? TokensUpdated;                    // running total
event EventHandler<int>? ContextUsedUpdated;               // measured window use
event EventHandler<int>? ContextEstimatedUpdated;          // estimate between provider calls
event EventHandler<(int Before, int After)>? ContextCompressed;
event EventHandler<int>? TurnCompleted;                    // turns the request took
event Action<SessionChangeKind>? Changed;
```

`SessionChangeKind`: `Mode`, `Model`, `Resumed`, `TurnCancelled`, `ContextCleared`.

```csharp
TokenLedger? Ledger        // spend by model and by agent, cache rates, cost. Null before wiring
(int, int)   OwnSpend      // this agent alone, excluding children
string?      SpendLabel    // "instance:model"
IReadOnlyList<string> LoadedSkills
```

**Subscribe after `Open`.** These attach to the host that exists at subscription time, and a re-wire
builds a new one.

## Commands

Each does the work, says its result through your observer, and returns a `CommandStatus`.

| Method | Parameter | Does |
|---|---|---|
| `SetMode(string)` | `"edits auto"`, `"agent single"` | parses and applies |
| `SetMode(WorkingMode)` | a mode | applies it |
| `UseFromInput(string)` | `/model`'s argument — empty lists, a name switches | parses and applies |
| `Use(string?)` | an instance name already decided on | switches |
| `Use(ActiveModel?, string?)` | a model the catalog never knew | switches |
| `ListSkills()` | — | says what skills are reachable |
| `ListSessions(string)` | `""`, `"all"`, `"resume 3"` | lists, or restores through the manager |
| `ListAgentTypes(string)` | a type name, or empty | lists types or one briefing |
| `ShowDiff(string)` | a path, or empty | says the working-tree diff |
| `SayUsage(string)` | a day count | says the usage dashboard |
| `ClearContext()` | — | empties the conversation, announces `ContextCleared` |
| `CompressNow(ct)` | a token | summarises the context; returns a `Task` or null if refused |

| `CommandStatus` | |
|---|---|
| `Reported` | it ran and said something; nothing moved |
| `Changed` | the session moved — expect the matching `SessionChangeKind` |
| `Refused` | it could not run now, and said why |
| `Unknown` | nothing here services this |

`.Handled()` → bool, for routing. `.Moved()` → whether a repaint is warranted.

## Identity

```csharp
string  WorkingDirectory     // the permission boundary
string? SessionId            // what --resume takes
bool    HasAgent             // is anything wired
WorkingMode Mode

ILlmProvider? Provider · string? InstanceName · ResolvedConfig? Resolution
PermissionPolicy? Policy · SharedServices? Services · SessionManager? Manager · JobRegistry? Executors

IReadOnlyList<CompletionValue> Values(string set)
```

The third group is what the session was wired **with**, exposed for diagnostics.

## Resume and shutdown

| Member | | Called by |
|---|---|---|
| `PendResume(snapshot)` | arm a stored conversation **before** the first wire | your `--resume` flag |
| `HasSavedTurn` | is there anything to come back to | your exit path |
| `MarkFinished()` | record that this session ended properly | your exit path |
| `CarryToNextWire()` | carry the conversation and ledger across a re-wire | a settings or provider change |

`MarkFinished` matters because reaching it is the only evidence the process was not killed
mid-session — that is what makes an unfinished row mean something.

---

# What you supply

## SessionPorts

```csharp
new SessionPorts { Observer = …, ToolObserver = … }
```

| | Required | |
|---|---|---|
| `Observer` | **yes** | `ISessionObserver` — where words go |
| `ToolObserver` | **yes** | `IToolObserver` — tool activity; pass a no-op to ignore it |
| `Ask` | no | `AskUser` — how the model asks the user something. Null when there is nobody to ask |
| `Policy` | with a gate | `PermissionPolicy` — the folder and edit mode requests are judged against. **A gate without one refuses everything** |
| `Tools` | no | `IReadOnlyList<IAgentTool>` — your own tools, offered beside the built-ins. Empty by default. See [tools.md](tools.md) |

**`ToolObserver` was called `Tools` before injected tools existed.** The observer yielded the name
because injected tools have the better claim on it: they ARE the tools, the observer only watches
them.

Every port is **per session**, which matters most for `Tools`: a tool that renders into one session's
transcript must never be handed to another.

## ISessionObserver

| Method | Called when |
|---|---|
| `UserTurnAdded(id, text)` | a prompt goes in — including one delivered from the queue |
| `AssistantTurnBegan(id)` | the model starts answering |
| `AssistantTextAppended(id, token)` | streaming body, token by token |
| `AssistantReasoningAppended(id, text)` | streaming reasoning, where the provider sends it |
| `AssistantTurnEnded(id)` | that turn's answer is complete |
| `AssistantLabelled(id, header)` | a header for the turn |
| `Said(message)` | the **session's own** words, in Core's markup dialect |
| `Failed(message)` | a turn failed |

`ChatMessageId` identifies a turn so you can stream into the right row. Ids are minted by the
session, so a parent and its children never collide.

## IToolObserver

| Method | Called when |
|---|---|
| `ToolsChanged(jobs)` | the live set changed — **announce starts here** |
| `ToolUpdated(job)` | one **finished** |
| `ToolProgressed(job)` | a progress message |
| `ToolResourcesSampled(jobId, snapshot)` | CPU and memory for a running process |
| `ToolOutputAppended(jobId, delta)` | streaming tool output |

Announcing starts from `ToolUpdated` prints nothing — a finished job is never `Running`.

---

# WorkingMode

```csharp
new WorkingMode(AgentMode Agent = FanOut, EditMode Edits = AlwaysAsk)
WorkingMode.Default
```

| `AgentMode` | |
|---|---|
| `FanOut` | may delegate — the spawn tool is offered. **Default** |
| `Single` | no spawn tool at all; the model never learns delegation exists |

| `EditMode` | |
|---|---|
| `AlwaysAsk` | every write asks. **Default** |
| `AcceptEdits` | in-boundary writes are silent, in a trusted folder |
| `Auto` | a model reviews what would otherwise ask; it can only refuse |

The two axes default in opposite directions on purpose. A permissive **edits** default is a silent
widening nobody chose; **delegation** widens nothing — a child runs under the same gate, in the same
folder — so the capable value is the default and `Single` is the opt-out.

---

# Permissions

The whole engine is in `CxAgent.Core.Permissions` — policy, trust, the boundary, stored rules, the
read-only command list, the `Auto` classifier, and the wrapper that gates every tool. **You
implement one thing: how a human is asked.**

## PermissionDecider.WithPrompt

```csharp
static PermissionDecider WithPrompt(
    PermissionRulesStore store,
    Action<string>? notice,
    Func<PermissionRequest, bool, CancellationToken, Task<PermissionChoice>> promptHook)
```

| Parameter | |
|---|---|
| `store` | handed to you by `buildGate` — do not construct one |
| `notice` | one-line notices: "auto-refused", a rule that fired. May be null |
| `promptHook` | `(request, offerTrust, ct)` → a choice. **Your dialog, your queue, your UI** |

`offerTrust` is true for a **file** request whose path is inside the working directory — the only
case where trusting the folder would actually help. It is false for shell, HTTP and MCP, and for a
path outside the boundary, because trusting the folder would not have permitted those. Do not offer
the button when it is false.

**Cancellation must resolve your prompt**, not abandon it. Core treats a cancelled request as a
refusal, and a hook that ignores the token leaves a dialog waiting forever.

## PermissionRequest

```csharp
record PermissionRequest(PermissionKind Kind, string Display, string? AlwaysRule)
```

| Member | |
|---|---|
| `Kind` | `Shell`, `FileRead`, `FileWrite`, `Http`, `Mcp` |
| `Display` | what to show — a verbatim command, or a **resolved** path |
| `AlwaysRule` | exactly what "always" would persist. **Null means this cannot be honestly generalised** — a command carrying a custom environment, a chain — so do not offer an "always" button |

## PermissionChoice

| | |
|---|---|
| `Once` | allow this, ask again next time |
| `Always` | allow and persist `AlwaysRule` for this folder |
| `Deny` | refuse; the model sees it and can adapt |
| `TrustFolder` | trust the working directory |

## What is decided before your hook

| Layer | |
|---|---|
| **Trust** | per folder, by path *and* birth time — a recreated folder inherits nothing |
| **Edit mode** | see `WorkingMode` above |
| **Stored rules** | what was answered "always" to, confined to the boundary |
| **The boundary** | the working directory, symlinks resolved |
| **Read-only verbs** | `ls`, `cat`, `grep`… run silently in a trusted folder, but only when every path they name is inside the boundary |

Everything fails toward asking.

---

# Configuration

## AgentConfig — from code

```csharp
var resolution = new AgentConfig
{
    Models =
    {
        ["local"]  = new(ProviderKind.OpenAiCompatible, "qwen3.6-35b")
                     { BaseUrl = "http://localhost:8771/v1", ContextWindow = 212_992 },
        ["claude"] = new(ProviderKind.Anthropic, "claude-sonnet-4-5")
                     { ApiKey = key, CacheControl = true },
    },
    DefaultModel = "local",
    Classifier   = "claude",
}.Resolve();
```

| `AgentConfig` | |
|---|---|
| `Models` | every model, by the name a user types at `/model` |
| `DefaultModel` | which one a session starts on. Null takes the single entry when there is exactly one |
| `Classifier` | which model reviews writes in `Auto`. **Null means Auto is not offered at all** |
| `MaxTurns` · `CompressAbove` | turn ceiling and compaction threshold; null derives both |
| `Agents` | sub-agent types, merged with the shipped ones rather than replacing them |
| `Mcp` | MCP servers, stdio or HTTP |

| `ModelConfig(Kind, Model)` | |
|---|---|
| `Kind` | `OpenAiCompatible`, `Anthropic`, `Ollama` |
| `BaseUrl` · `ApiKey` | the endpoint and its credential |
| `ContextWindow` | tokens. Null probes the endpoint on first use, then falls back to a fixed threshold |
| `MaxConcurrentAgents` | how many children may call this endpoint at once. Null is unlimited |
| `Headers` · `CacheControl` | extra headers, and prompt caching where the provider bills for it |

**Mistakes come back as errors, never exceptions** — `resolution.Errors` with `HasProvider` false.

## From a file

`config.json` is parsed by THIS LIBRARY — `ProviderConfigLoader` lives in `cxagent.Core`, and the
terminal app is just one caller. `AppPaths` decides which directory it is read from, so an embedding
application can keep its own rather than sharing cxagent's.

```csharp
var paths = new AppPaths("/etc/myapp");                   // reads /etc/myapp/config.json
var env   = new Dictionary<string, string>();             // ${VARS} in the file resolve from here

ConfigResolver.Resolve(paths, env, useMock: false)        // the whole file
ConfigResolver.ResolveInstance(paths, env, "openrouter")  // one named instance
ProviderConfigLoader.LoadAndValidate(paths, env)          // parsed settings + warnings, unresolved
```

`new AppPaths()` with no argument uses the per-user location — `$XDG_CONFIG_HOME/cxagent` when that
is set, otherwise the platform's application-data folder (`~/.config` on Linux, `%APPDATA%` on
Windows, `~/Library/Application Support` on macOS). The directory holds more than the file: the
resume database, the usage archive and the logs live beside it.

**Errors, not exceptions — at the `ConfigResolver` layer.** A missing or malformed file comes back as
`resolution.Errors` with `HasProvider` false, the same shape `AgentConfig.Resolve()` uses, so one
error path handles both. `ProviderConfigLoader.LoadAndValidate` underneath it DOES throw
`ProviderConfigException`; `Resolve` catches it for you.

**Every key, and what a valid file looks like:
[the configuration reference](https://github.com/nickprotop/cxagent/blob/master/CONFIG.md).** It is
one schema — the same file serves this library and the terminal app — so it is documented once,
there, rather than summarised here where the two copies would drift.

## Tool selection

Which tools an agent is offered, narrowed at four levels. Absent, an agent gets all twelve and
nothing changes.

```csharp
using CxAgent.Core.Jobs;

new SharedServices { ToolSelection = new ToolSelection([Tool.Inherited, Tool.Not.RunShell]) }
```

`Tool` names every built-in, so terms are compiler-checked rather than spelled: `Tool.Glob`,
`Tool.Not.RunShell` (`"-run_shell"`), `Tool.Also.Grep` (`"+grep"`), `Tool.Inherited`, `Tool.All`.

| Term | Means |
|---|---|
| `Tool.Inherited` | start from what the level above offers. Only the first one acts |
| `Tool.All` | start from everything, discarding an outer narrowing |
| `Tool.ReadFile` | a bare name is a whitelist — name every tool you want |
| `Tool.Not.RunShell` | remove one from whatever is in force |
| `Tool.Also.RunShell` | add one back that an outer level removed |

The four levels compose outward-in, each applied to what the last left:

| | Where | Scope |
|---|---|---|
| **S1** | `SharedServices.ToolSelection`, or `llmAgent.tools` in config | the agent's whole life |
| **S2** | `SessionPorts.ToolSelection` | one session |
| **S3** | `Submit(text, tools: …)` | one request |
| **S4** | `agents.<type>.tools` in config | every child of that type |

**A withheld tool is refused, not hidden.** Calling one by name returns `not available` — distinct
from `no such tool`, which is what a typo gets. One means stop; the other means try another name.

**MCP tools are never narrowed** — each server's `enabled` flag is its control, because servers
connect asynchronously and a selection resolved before a handshake would drop tools that arrived a
moment later.

**Set it once per session.** An S3 selection that varies between requests rewrites the cached prompt
prefix each time it changes — measured locally at 2,824 tokens cached (66 ms) versus a full
reprocess (773 ms). Correct either way; it is a cost, not a bug.

**[ReadOnlyAgent](https://github.com/nickprotop/cxagent/tree/master/cxagent.Core/examples/ReadOnlyAgent)** is a working example: four tools, no writes,
no shell, no sub-agents, and why a whitelist beats subtracting.

## For a test

```csharp
ResolvedConfig.ForTesting(provider, instanceName)
```

## What comes out

`ResolvedConfig` carries an `ActiveModel` (what this session talks to) and a `ProviderCatalog`
(everything configured). `catalog.Use(name)` derives a model without touching disk — which is why a
model switch costs no file read.
