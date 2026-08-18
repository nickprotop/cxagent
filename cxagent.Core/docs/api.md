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

### Text typed while a turn runs

**`Submit` handles this for you.** Called while a turn is in flight, it queues the text and returns
`Queued` — you do not call `Steer` yourself on the ordinary path. What is queued is delivered at the
turn's **next tool barrier**, where the model can still act on it, and several lines typed in a burst
become one message rather than several.

```csharp
event Action<string, string>? Pending;    // (whole, justAdded) — something was queued
event Action<string>?         Drained;    // the turn took it; the real message is coming
event Action<string>?         Cancelled;  // taken back, never sent
```

`Pending` carries both the whole queue and the line just added, because only that moment has both: a
subscriber holding only the whole cannot tell what changed, and one holding only the increment would
have to read the queue back.

`Drained` and `Cancelled` are separate although both empty the queue, because a subscriber does
opposite things — take the placeholder down because the real message is about to appear, versus put
the text back where it can be edited.

```csharp
void    CancelPending()      // empty it, hand it back through Cancelled
string? PendingSteer         // what is waiting, or null
void    Steer(string text)   // queue directly — Submit already does this when busy
string? TakePendingSteer()   // take it, exactly once; raises Drained
```

`Steer` and `TakePendingSteer` are the mechanism `Submit` and the turn loop use. Call them directly
only if you are driving the queue yourself — for example replaying queued input into a fresh session.

Events are raised **outside** the queue's lock, and subscribers marshal to their own thread; Core has
no dispatcher.

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
string  WorkingDirectory     // the permission boundary; what relative paths resolve against
string? SessionId            // what --resume takes
bool    HasAgent             // is there anything wired to talk to
WorkingMode Mode

ILlmProvider? Provider · string? InstanceName · string? SpendLabel
ResolvedConfig? Resolution · PermissionPolicy? Policy
SharedServices? Services · SessionManager? Manager · PluginRegistry? Plugins

IReadOnlyList<CompletionValue> Values(string set)   // completions this session can answer
```

The last group is what the session was wired WITH, exposed for diagnostics. A consumer supplies them
through `SessionManager.Open`; reading them back is for a front end that wants to show what is in
force.

### Resuming and ending

```csharp
void PendResume(SessionSnapshot snapshot)   // arm a stored conversation BEFORE the first wire
bool CarryToNextWire()                      // carry this conversation and its ledger across a re-wire
bool MarkFinished()                         // record that this session ended properly
bool HasSavedTurn                           // is there anything to come back to
bool RefuseIfBusy()                         // true when a turn is running — and says so
```

`PendResume` is the one assemble-time member a consumer legitimately calls: `--resume` finds a
snapshot and arms it before the session is wired. Everything else in that family is internal.

`MarkFinished` matters because reaching it is the only evidence a process was not killed
mid-session — an unfinished row is what makes "an earlier session ended without closing" mean
something.

`CarryToNextWire` is for a front end that rebuilds its host — a settings change, a provider swap —
and wants the conversation and the spend to survive it.

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

`SharedServices.Gate` is null by default: **no gating at all**. That is an ordinary headless
arrangement — a batch runner in a container may want exactly it — but it is a choice, not something
you inherit by forgetting.

### Supplying a gate

`SessionManager.Create` takes a `buildGate` delegate rather than a gate, because the gate needs the
rules store and the store is built inside:

```csharp
var manager = SessionManager.Create(paths, buildGate: store =>
    PermissionDecider.WithPrompt(
        store,
        notice: line => Console.Error.WriteLine(line),   // "auto-refused", rule notices; may be null
        promptHook: async (request, offerTrust, ct) =>
        {
            Console.Error.WriteLine($"{request.Kind}: {request.Display}");
            if (request.AlwaysRule is { } rule)
                Console.Error.WriteLine($"  always would cover: {rule}");

            return await AskSomehow(ct);   // your UI, your dialog, your queue
        }));
```

`promptHook` is the whole seam. It is handed the request, whether offering "trust this folder" makes
sense here, and a token — and returns one of:

| `PermissionChoice` | |
|---|---|
| `Once` | allow this, ask again next time |
| `Always` | allow and persist `request.AlwaysRule` for this folder |
| `Deny` | refuse; the model sees the refusal and can adapt |
| `TrustFolder` | trust the working directory, which unlocks the silent class inside it |

**Cancellation must resolve the prompt, not abandon it.** The token is passed to your hook precisely
so a cancelled turn does not leave a dialog waiting for a click nobody will make. Core treats a
cancelled request as a refusal.

### What a request carries

```csharp
record PermissionRequest(PermissionKind Kind, string Display, string? AlwaysRule)
```

`Kind` is `Shell`, `FileRead`, `FileWrite`, `Http` or `Mcp`. `Display` is what to show — a verbatim
command, or a **resolved** path. `AlwaysRule` is exactly what "always" would persist, precomputed so
your button text and the stored rule cannot disagree; **null means this cannot be truthfully
generalised** (a shell command carrying a custom environment, a chain), so do not offer an "always"
button when it is.

### How a decision is reached

Before your hook is ever called, a request is judged by layers that only narrow:

1. **Trust** — per folder, identified by path *and* birth time, so a folder deleted and recreated is
   a different folder and inherits nothing
2. **Edit mode** — `AlwaysAsk`, `AcceptEdits`, or `Auto`
3. **Stored rules** — what was answered "always" to, confined to the boundary
4. **The boundary** — the working directory, symlinks resolved, so a link pointing out is out
5. **Read-only verbs** — a short list that cannot write however invoked (`ls`, `cat`, `grep`…),
   allowed silently in a trusted folder **only when every path argument is inside the boundary**

`Auto` adds a sixth: a model reviews what would otherwise ask. It can only **refuse** — it is
consulted after the floor has already said yes, so it can add friction and never remove it. A
timeout, a transport error, or any verdict it cannot parse all mean ask.

Every layer fails toward asking. A path that cannot be resolved is outside the boundary; a command
carrying a token nothing could classify is refused rather than allowed.

---

## Configuration

Three ways in, depending on where your settings live.

### From code — `AgentConfig`

The one an embedder usually wants: no `config.json`, no file at all.

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
    Classifier   = "claude",       // reviews writes in /mode edits auto
}.Resolve();

if (!resolution.HasProvider)
    foreach (var e in resolution.Errors) Console.Error.WriteLine(e);
```

**Mistakes come back as errors, never exceptions.** A caller is usually assembling this from its own
settings and wants to say what went wrong — a `DefaultModel` naming no entry, or a `Classifier` that
is not configured, comes back in `Errors` with `HasProvider` false.

| `AgentConfig` | |
|---|---|
| `Models` | every model, by the name a user types at `/model` |
| `DefaultModel` | which one a session starts on — null takes the single entry when there is exactly one |
| `Classifier` | which model reviews writes in `auto`; null means auto is not offered at all |
| `MaxTurns` · `CompressAbove` | turn ceiling and compaction threshold; null derives both |
| `Agents` | sub-agent types, merged with the shipped ones rather than replacing them |
| `Mcp` | MCP servers, stdio or HTTP |

| `ModelConfig(Kind, Model)` | |
|---|---|
| `Kind` | `OpenAiCompatible`, `Anthropic`, `Ollama` |
| `BaseUrl` · `ApiKey` | the endpoint and its credential |
| `ContextWindow` | tokens; null probes the endpoint on first use, and falls back to a fixed threshold |
| `MaxConcurrentAgents` | how many children may call this endpoint at once; null is unlimited |
| `Headers` · `CacheControl` | extra headers, and prompt caching where the provider supports it |

### From a file — `ConfigResolver`

```csharp
ConfigResolver.Resolve(paths, env, useMock: false)       // the config.json cxagent itself reads
ConfigResolver.ResolveInstance(paths, env, "openrouter") // one named instance
```

Useful when you want to share configuration with an installed cxagent — both examples do this so
they run against whatever provider is already set up.

### For a test — `ResolvedConfig.ForTesting`

```csharp
ResolvedConfig.ForTesting(provider, instanceName)   // one provider, nothing else configured
```

### What comes out

`ResolvedConfig` carries an `ActiveModel` (what this session talks to) and a `ProviderCatalog`
(everything configured). `catalog.Use(name)` derives a model from the catalog without touching disk —
which is what `/model` uses, so switching costs no file read.
