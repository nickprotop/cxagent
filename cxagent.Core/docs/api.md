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

**CALL IT MORE THAN ONCE AND YOU HAVE MORE THAN ONE SESSION.** A manager holds them all — the TUI
opens a tab per session, and an embedder can hold as many as it wants, on the same folder or on
different ones. `Sessions` enumerates them; `Close(Session)` answers false rather than throwing when
one is mid-turn.

**WHAT IS PER SESSION, AND WHAT IS NOT.** Each session owns its history, its `Agent`, its working
directory, its plugin registry and its `PermissionPolicy` — so a permission answered in one is not
answered in another, and a plugin unwired in one keeps serving the rest. What is shared is the
process: one gate, one config, one set of stores. **A gate serving several sessions is the part that
bites**: a `PermissionRequest` must carry the policy of the session it belongs to, or it is judged
against another session's root. Every path Core owns stamps it; a caller reaching the gate directly
has to stamp it too.

**AND A SESSION'S OWN ID IS NOT ITS AGENT'S.** `Session.Id` is minted at construction and survives a
re-wire; the agent id does not. Anything you file per session — permissions, history rows, child
processes — keys on `Session.Id`, or a model switch silently orphans it.

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
| `Started` | `Task Turn`, `Task<string?> Result`, `Task<SendOutcome> Outcome` | a turn began. Await `Turn`, or attach a continuation. `Result` is the final answer's text; `Outcome` says how the turn ended — `Completed`, `Capped` at the turn limit, `Failed`, `Cancelled` — which the text alone cannot tell you |
| `Queued` | — | a turn was already running; the text went to the queue and `Pending` fired |
| `NoAgent` | — | no model is wired |
| `Aside` | `Task? Answer` | the session is in btw mode and the text went to the side agent — null when it was still answering and refused it |

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

`SessionChangeKind`: `Mode`, `Model`, `Resumed`, `TurnCancelled`, `ContextCleared`, `Aside`.

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
| `ListCommands(string)` | a command name, or empty | lists custom commands, or shows one file |
| `ListSessions(string)` | `""`, `"all"`, `"resume 3"` | lists, or restores through the manager |
| `ListAgentTypes(string)` | a type name, or empty | lists types or one briefing |
| `ShowDiff(string)` | a path, or empty | says the working-tree diff |
| `SayUsage(string)` | a day count | says the usage dashboard |
| `ClearContext()` | — | empties the conversation, announces `ContextCleared` |
| `CompressNow(ct)` | a token | summarises the context; returns a `Task` or null if refused |
| `Btw(string)` | a question, or empty | forks the agent and enters btw mode — see [Side questions](#side-questions) |

| `CommandStatus` | |
|---|---|
| `Reported` | it ran and said something; nothing moved |
| `Changed` | the session moved — expect the matching `SessionChangeKind` |
| `Refused` | it could not run now, and said why |
| `Unknown` | nothing here services this |

`.Handled()` → bool, for routing. `.Moved()` → whether a repaint is warranted.

## Custom commands

**`Submit` does this for you.** A user's `/name` that the registry does not know is looked up among
command files and skills (`.cxagent/commands`, the config dir's `commands`, the skill folders — see
CONFIG.md) and run as a turn whose first step is expanding the file. A built-in always wins, and only
the user's own text expands: a delivery or a plugin's goal starting with `/` stays text.

```csharp
InvocableCatalogResult Catalog()               // every command file and skill, scanned now
Invocable? FindTyped(string line)              // what this /line runs; null for a built-in or no match
SubmitOutcome RunCommand(TypedCommand command) // run it — or hold it if a turn is running

string?               HeldCommand              // the command waiting for the turn to end
event Action<string?>? HeldChanged;            // the slot changed — read HeldCommand, not the argument
```

`TypedCommand(Line, Entry) { Attachment }` — `Attachment` goes under the expansion, never into
`$ARGUMENTS`; a headless front end puts piped input there.

**Expansion runs inside the turn**, so the session is busy from the moment it starts: the row shows
what was typed, then shell lines and `@file` reads ask through your permission hook exactly as the
model's own calls do, then the model gets the expanded text. `CancelTurn()` during expansion sends
nothing and hands the typed line back through `Cancelled`.

**A command submitted mid-turn is held**, one at a time, and runs when the turn ends. A cancel hands
back one thing — steered text first, then the line being expanded, then the held command — and names
anything else as not run.

## Side questions

`/btw` asks a side agent about the conversation without adding to it. The side agent starts from what
the main agent last sent, can run only `read_file`, `glob` and `grep` — through the session's own
gated executors, so your permission hook is asked exactly as it would be for the main agent — and
reports to an observer of its own, never to `ISessionObserver`.

```csharp
IAsideObserver? AsideObserver { get; set; }   // where asides draw; null draws nowhere
bool InAside                                  // btw mode is on
bool AsideAnswering                           // an answer is streaming

CommandStatus Btw(string question)   // fork and enter btw mode; asks at once when non-empty
SubmitOutcome SendAside(string text) // what Submit calls in btw mode
void          StopAside()            // stop the answer; stays in btw mode
void          LeaveAside()           // stop it and leave; the thread is forgotten
```

**In btw mode `Submit` gives the user's text to the aside — all of it.** A leading `/` is text, not a
command, and comes back as `SubmitOutcome.Aside`. A delivery or a plugin's goal still reaches the
main agent. Entering and leaving raise `Changed(SessionChangeKind.Aside)`.

`Btw` refuses until something has been sent (a resumed session counts what it restored). Clearing
the context, replacing the host and closing the session all leave btw mode.

```csharp
public interface IAsideObserver
{
    void Asked(int aside, string question);      // the first call for an aside opens its block
    void Waiting(int aside, bool waiting);       // true until the first chunk of a request
    void AnswerAppended(int aside, string text); // visible text, reasoning stripped
    void LookedUp(int aside, string row);        // one row per tool call, run or refused
    void Finished(int aside, AsideEnd end, string? detail);   // Answered, Capped, Stopped, Failed
}
```

Calls arrive on a pool thread; marshal to your own. Spend is recorded as worker spend, and each
answer writes a run row typed `aside`.

## Plugins

Tools loaded from a DLL at run time, rather than compiled into your app. `Session` owns the two
lifecycle calls; everything else is in `CxAgent.Core.Plugins`.

| Method | Does |
|---|---|
| `LoadPluginFrom(assemblyPath, ct?, settings?)` | the way to load from disk: asks the gate **before any of the plugin's code runs** (from its sidecar, against a hash of its load set), then loads, refuses a colliding tool name, starts it and offers its tools. `CommandStatus.Changed` on success |
| `ApproveLoadAsync(assemblyPath, loadSetDirectory, ct?)` | the question alone, for a host that loads with a context of its own: true when approved (always, with no gate). Call it before the loader, then `LoadPlugin` |
| `LoadPlugin(plugin, manifest, loadSetDirectory, ct?)` | registers an instance already loaded: refuses a colliding tool name, starts it, offers its tools. **Under a gate it takes only a load set `ApproveLoadAsync` approved before loading, with the hash it had then** — anything else has already run its code and is refused |
| `Plugins` | the session's `PluginRegistry` — `CurrentTools()`, `LoadedPluginNames`, `UnwireAsync(name, ct)` |

**`loadSetDirectory` is hashed, not the file.** `PluginIdentity.HashLoadSet` covers the whole
directory, so a plugin's approval survives nothing changing and lapses the moment anything does.
**No link in it is followed:** a link inside the folder is hashed by the text it holds, and a link
pointing outside it — or a loop of links — means the load set cannot be identified and is refused.
`PluginIdentity.InspectLoadSet` returns the hash or the reason (`LoadSetIdentity`); `HashLoadSet` throws
`LoadSetRefusedException` with it. `PluginIdentity.ListLoadSet` lists the folder the same way — files, and
links as links — for a host that removes a plugin and must not follow a link to delete what it points at.

**A load with no gate wired does not ask.** A headless host loading a plugin has already decided to
run it, and Core does not invent a prompt with nobody to answer it. Wire `SessionPorts.Policy` and a
gate to get the question.

**A failed `Start` unwires.** Tools are registered before the plugin starts, so a backend that never
comes up would otherwise leave tools answering "not running" with nothing explaining why.

### Loading one from disk

| | |
|---|---|
| `ManagedPluginLoader.Load(assemblyPath, context, ct)` | reads the sidecar, loads the assembly, checks the manifest against the sidecar. Returns `Loaded` or `Failed(reason)` |
| `PluginManifest.Parse(json)` | the sidecar, without loading anything — what a host shows before asking |
| `IPluginContext` | what the plugin is handed: `WorkingDirectory`, `Settings`, `Logger`, `Lifetime`, `RegisterChildProcess(pid)`. **Never the transcript, the model or the permission store** |
| `ChildProcessStore` | records what a plugin spawned and reaps it at the next startup. `ReapOrphans(log)` at construction; `ReapPlugin(name, log)` at unwire |

An out-of-process path exists for plugins that cannot be loaded managed — C, Rust, Go — under
`CxAgent.Core.Plugins.Abi`, with the same `IPlugin` at the end of it.

**[Writing a plugin →](https://github.com/nickprotop/cxagent/blob/master/cxagent.Core/sdk/docs/plugins.md)**

## Identity

```csharp
string  WorkingDirectory     // the permission boundary
string? SessionId            // what --resume takes
bool    HasAgent             // is anything wired
WorkingMode Mode

ILlmProvider? Provider · string? InstanceName · ResolvedConfig? Resolution
PermissionPolicy? Policy · SharedServices? Services · SessionManager? Manager · JobRegistry? Executors

IReadOnlyList<CompletionValue> Values(string set)
IReadOnlyList<string> OfferedToolNames()
```

The third group is what the session was wired **with**, exposed for diagnostics. `OfferedToolNames()`
is what the next turn would offer the model, built the same way the request builds it — empty
before the first wire.

## Resume and shutdown

| Member | | Called by |
|---|---|---|
| `PendResume(snapshot)` | arm a stored conversation **before** the first wire | your `--resume` flag |
| `HasSavedTurn` | is there anything to come back to | your exit path |
| `MarkFinished()` | record that this session ended properly | your exit path |
| `CarryToNextWire()` | carry the conversation and ledger across a re-wire | a settings or provider change |

`MarkFinished` matters because reaching it is the only evidence the process was not killed
mid-session — that is what makes an unfinished row mean something.

## Changing config while sessions run

`CxAgent.Core.Config` edits `config.json` and the manager applies the result to running sessions.

```csharp
// Edits on the document — every key they do not own is kept, unknown ones included.
ProviderEdits.Add / Update / Rename / Remove / SetDefault / SetReviewer(root, …)   // InstanceFields
McpEdits.Add / Update / Rename / Remove / SetEnabled(root, …)                     // McpServerFields
AgentEdits.SetOverride / ResetBuiltin / AddType / UpdateType / RenameType / RemoveType(root, …)

// The one validating writer: read, edit, judge with the loader, write atomically at 0600.
ConfigWrite ConfigFile.TryApply(ConfigTarget target, Action<JsonObject> edit)
ProviderSettings ProviderConfigLoader.Parse(string json, env, string? configDir = null)

// Live: every session adopts the new catalog; the ones a change touches move.
manager.ApplyConfig(ResolvedConfig fresh, ConfigChange change, CacheChoice choice = ApplyNow)
IReadOnlyList<Session> manager.BreaksCache(fresh, change)   // the tabs to ask about
manager.Post      = run => ui.Enqueue(run);                 // where a rebind runs
manager.RebindOne = (session, config) => { Rewire(session, config); return true; };
```

**`ConfigChange` says what the edit touched**: `Instances` edited, `Renamed` old → new, `Everyone` when
the reviewer, the default's limit or an agent type changed, and `ModelChanged` — the instances whose model
or endpoint changed, the only edits a tab's prompt cache does not survive. Everything else is applied in
place: the provider object swapped as `/model` swaps it, the reviewer set on the session's policy, the
sub-agent limit and agent types refreshed — no new host. A session on a `ModelChanged` instance moves as
`CacheChoice` says: `ApplyNow`, `CompressThenApply`, `AtNextClearOrCompression` (`Session.ConfigPending`
until then) or `NewTabsOnly`.

**A move happens on your thread, and only when idle.** `Post` runs the step; inside it the session is
asked again whether it is busy — a turn may have started since — and the conversation is carried and
re-wired together. A busy session waits for its turn to end. A session that cannot move says why and
keeps the new catalog.

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
| `AssistantThinkingAppended(id, text)` | streaming thinking — the provider's own channel or inline `<think>` text |
| `AssistantThinkingEnded(id, summary)` | that run of thinking is over: duration, words, hidden, redacted |
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

## For a front end with nobody to ask

A host that runs unattended — cxagent's own `-p` is one — answers the prompt hook itself. Two
members exist for it:

| Member | |
|---|---|
| `PermissionPolicy.TrustOverride` | init-only: treat the folder as trusted **for this policy object only**. Nothing is written to `permissions.json`, so a script never changes the user's own setup |
| `PermissionPolicy.IsProtectedWrite(request)` | true for a file-tool write with a `.git`, `.vscode`, `.claude`, `.idea`, `.github`, `.husky`, `.devcontainer` or `.cxagent` folder, or an `.envrc`, anywhere on its path — where what lands runs later. A hook that approves everything else should still refuse these. It reads requests, not shell text |

Answer `Once` or `Deny` from such a hook, never `Always` or `TrustFolder`: both persist.

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
| `MaxTurns` · `CompressAbove` | turn ceiling and compaction threshold; null derives both. A set ceiling binds the session's own agent and its sub-agents; unset, the session's agent has no cap and sub-agents keep a built-in 300 |
| `Agents` | sub-agent types, merged with the shipped ones rather than replacing them |
| `Mcp` | MCP servers, stdio or HTTP |

| `ModelConfig(Kind, Model)` | |
|---|---|
| `Kind` | `OpenAiCompatible`, `Anthropic`, `Ollama` |
| `BaseUrl` · `ApiKey` | the endpoint and its credential |
| `ContextWindow` | tokens. Null probes the endpoint on first use, then falls back to a fixed threshold |
| `MaxConcurrentAgents` | how many children may call this endpoint at once. Null is unlimited |
| `Headers` · `CacheControl` | extra headers, and prompt caching where the provider bills for it |
| `Thinking` · `ThinkingDisplay` | the level a new session starts at, and whether a Claude model's thinking is summarized or omitted — see `/think` |
| `Reasoning` | marks an `api.openai.com` model as one that reasons, so it offers `minimal` to `high` |

**The same settings, the same model.** A `ModelConfig` builds its provider the way a `config.json` entry does, so every key the file documents behaves identically here.

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
