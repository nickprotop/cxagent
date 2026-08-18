# CxAgent.Core — API reference

Every public member, grouped by the question it answers. For the shape of a working app see the
[README](../README.md); for a front end you can read end to end see
[SpectreAgent](../examples/SpectreAgent).

Namespaces: `CxAgent.Core.Sessions` (a conversation), `CxAgent.Core.Agents` (the loop that runs
turns), `CxAgent.Core.Llm` (providers and config), `CxAgent.Core.Storage` (paths and stores),
`CxAgent.Core.Permissions`, `CxAgent.Core.Commands`, `CxAgent.Core.Mcp`.

---

## SessionManager

One per process. It owns what sessions share — the stores, the log manager, the permission gate —
and the command registry they dispatch through.

### Creating one

```csharp
static SessionManager Create(AppPaths paths, …)
static SessionManager Create(ProcessSetup setup)
static SessionManager Over(SharedServices shared, PermissionRulesStore? rules = null, …)
```

`Create(paths)` builds the stores itself and is what an app wants. `Over(...)` takes services
somebody else built — for a host embedding several managers, or a test.

### Opening a session

```csharp
Session Open(string workingDirectory, ResolvedConfig? config, SessionPorts ports, WorkingMode? mode = null)
Session Open(string workingDirectory, SessionPorts ports, WorkingMode? mode = null)
Session Open(Session session, ResolvedConfig? config, SessionPorts ports, WorkingMode? mode = null)
```

The working directory is the permission boundary and what relative paths resolve against. `mode`
defaults to [`WorkingMode.Default`](#workingmode) — fan-out delegation, always-ask edits.

The third overload re-wires an existing session over a new configuration; that is what `/model` and
an F5-style settings change use, and it keeps the conversation.

### The rest

| Member | |
|---|---|
| `Commands` | the registry every session dispatches through — register your own on top, last wins |
| `Values(set, workingDirectory?)` | completion values for a command argument (`"models"`, `"sessions"`, …) |
| `Sessions` | every session this manager has open |
| `Resume(session, snapshot, rewire?)` | restore a stored conversation — arms, re-wires, retires the old row, says so |
| `Rewire` | the delegate `Resume` uses when a caller passes none; the one thing only a front end can build |
| `Close(session)` | dispose the host and release the turn scope |
| `Shared` · `Rules` · `Config` | what was built at construction |

---

## Session

### Running a turn

```csharp
SubmitOutcome Submit(string text, string? echo = null)
SubmitOutcome Initialise()                 // /init — sends a briefing, displays "/init"
bool          CancelTurn()
bool          IsBusy
```

`Submit` is **synchronous** and returns a receipt:

| `SubmitOutcome` | Meaning | What a caller does |
|---|---|---|
| `Started(Task Turn)` | a turn began | await it, or attach a continuation |
| `Queued` | one was already running | nothing — `Pending` already fired |
| `NoAgent` | nothing is wired | leave the text where it is |

`echo` is what the user SEES when it differs from what the model receives — `/init` sends
paragraphs and displays `/init`, because putting a briefing on the transcript as the user's own
words misattributes it on every later read.

`CancelTurn` unwinds the provider stream, the tool loop and any shell process (whose runner kills its
whole process tree), says "Stopped." through the observer, and hands anything queued back through
`Cancelled`. The session, its context and its MCP servers survive.

### The steer queue

Text typed while a turn runs is delivered at the turn's **next tool barrier**, where the model can
still act on it.

```csharp
void    Steer(string text)          // append — several lines become one message
void    CancelPending()             // empty it and hand it back through Cancelled
string? PendingSteer                // what is waiting
string? TakePendingSteer()          // take it, exactly once

event Action<string, string>? Pending;    // (whole, added)
event Action<string>?         Drained;    // the turn took it
event Action<string>?         Cancelled;  // taken back, never sent
```

`Pending` carries both the whole and the increment because only that moment has both. `Drained` and
`Cancelled` are separate although both empty the queue: one means "the real message is coming, take
the placeholder down", the other means "put it back where it can be edited".

Events are raised outside the lock, and subscribers marshal — Core has no dispatcher.

### Watching it work

```csharp
event EventHandler<int>? TokensUpdated;                    // total, after each provider call
event EventHandler<int>? ContextUsedUpdated;               // measured window use
event EventHandler<int>? ContextEstimatedUpdated;          // between calls
event EventHandler<(int Before, int After)>? ContextCompressed;
event EventHandler<int>? TurnCompleted;                    // turns taken
event Action<SessionChangeKind>? Changed;                  // Mode, Model, Resumed, TurnCancelled, ContextCleared

TokenLedger? Ledger        // spend by model and by agent, cache rates, cost
(int, int)   OwnSpend      // this agent alone, excluding children
string?      SpendLabel    // instance:model
IReadOnlyList<string> LoadedSkills
bool         HasSavedTurn  // is there anything to resume
```

**Subscribe after opening.** The events attach to the host that exists at subscription time, and a
re-wire builds a new one.

### Commands

Each does the work, says its own result through the observer, and returns a `CommandStatus`.

```csharp
CommandStatus SetMode(string argument)      // "edits auto", "agent single"
CommandStatus SetMode(WorkingMode mode)
CommandStatus UseFromInput(string argument) // /model — parses what a user typed
CommandStatus Use(string? instanceName)     // a name already decided on
CommandStatus Use(ActiveModel? model, string? requestedName = null)
CommandStatus ListSkills()
CommandStatus ListSessions(string arguments)
CommandStatus ListAgentTypes(string arguments)
CommandStatus ShowDiff(string arguments)
CommandStatus SayUsage(string arguments)    // /stats
CommandStatus ClearContext()
Task<SessionCompressor.CompressResult>? CompressNow(CancellationToken ct)
```

| `CommandStatus` | |
|---|---|
| `Reported` | it ran and said something; nothing moved |
| `Changed` | the session moved — expect the matching `SessionChangeKind` |
| `Refused` | it could not run now, and said why |
| `Unknown` | nothing here services this |

`.Handled()` collapses that to a bool for routing; `.Moved()` asks whether a repaint is warranted.

### Identity

```csharp
string  WorkingDirectory     // the permission boundary
string? SessionId            // what --resume takes
bool    HasAgent
WorkingMode Mode
ILlmProvider? Provider · string? InstanceName · ResolvedConfig? Resolution
PermissionPolicy? Policy · SharedServices? Services · SessionManager? Manager
IReadOnlyList<CompletionValue> Values(string set)
```

---

## What you supply

### SessionPorts

```csharp
new SessionPorts { Observer = …, Tools = … }
```

`Observer` (`ISessionObserver`) is required — where assistant text and the session's notices go.
`Tools` (`IToolObserver`) reports job activity. Both have buffered implementations in the package
(`BufferedChatSink`, `BufferedJobPanel`) if you only need to collect output.

### ISessionObserver

```csharp
void UserTurnAdded(ChatMessageId id, string text);
void AssistantTurnBegan(ChatMessageId id);
void AssistantTextAppended(ChatMessageId id, string token);      // streaming body
void AssistantReasoningAppended(ChatMessageId id, string text);  // streaming reasoning
void AssistantTurnEnded(ChatMessageId id);
void AssistantLabelled(ChatMessageId id, string header);
void Said(string message);      // the session's own notices, in Core's markup dialect
void Failed(string message);
```

`Said` carries markup like `[yellow]Stopped.[/]` — render the tags or strip them, but do not print
them raw beside model output, which may itself contain brackets.

### IToolObserver

```csharp
void ToolsChanged(IReadOnlyList<Job> jobs);   // the live set, while they RUN
void ToolUpdated(Job job);                    // one FINISHED
void ToolProgressed(Job job);
void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot);
void ToolOutputAppended(string jobId, string delta);
```

Announce starts from `ToolsChanged`. `ToolUpdated` fires on completion, so a front end announcing
from there prints nothing — a finished job is never `Running`.

---

## WorkingMode

```csharp
new WorkingMode(AgentMode Agent = FanOut, EditMode Edits = AlwaysAsk)
WorkingMode.Default
```

| `AgentMode` | |
|---|---|
| `FanOut` | may delegate to sub-agents — the default |
| `Single` | no spawn tool is offered at all |

| `EditMode` | |
|---|---|
| `AlwaysAsk` | every write asks — the default |
| `AcceptEdits` | in-boundary writes are silent, in a trusted folder |
| `Auto` | a classifier reviews what would otherwise ask; it can only refuse, never widen |

The two axes default in opposite directions on purpose. A permissive **edits** default is a silent
widening nobody chose; **delegation** widens nothing — a child runs under the same gate, in the same
folder — so the capable value is the default and `Single` is the opt-out.

---

## Permissions

`SharedServices.Gate` is null by default: **no gating at all**, which is an ordinary headless
arrangement but a choice rather than something inherited. Supply one and every shell command, file
write and network call is judged by:

1. **Trust** — per folder, identified by path *and* birth time, so a recreated folder is a new one
2. **Edit mode** — see above
3. **Stored rules** — what a user answered "always" to
4. **The boundary** — the working directory, symlinks resolved
5. **Read-only verbs** — a short list of commands that cannot write, confined to the boundary

Every layer fails toward asking. A path that cannot be resolved is outside; a command with a token
nothing could classify is refused.

---

## Configuration

```csharp
ConfigResolver.Resolve(paths, env, useMock: false)   // reads config.json
ResolvedConfig.ForTesting(provider, instanceName)     // one provider, nothing else
```

`ResolvedConfig` carries an `ActiveModel` (what this session talks to) and a `ProviderCatalog`
(everything configured). `catalog.Use(name)` derives a model from the catalog without touching disk.

Providers: `anthropic`, `openai-compatible`, `ollama`. Several may be configured at once and a
sub-agent type can name a different one.
