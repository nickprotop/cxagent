# CxAgent.Core

The sessions, agents and turn loop behind [cxagent](https://github.com/nickprotop/cxagent) — usable
without a terminal.

A conversation, the agent running it, tool execution, sub-agent delegation, permissions, MCP and
resumable stores. You supply where the text goes; there is no UI dependency.

```
dotnet add package CxAgent.Core
```

Targets `net10.0`. **Pre-1.0 and moving** — public signatures change between versions.

### What is in it

| | |
|---|---|
| **Turn loop** | tool calls, retries, and compaction when the context fills |
| **Twelve tools** | read, write, edit, glob, grep, shell, http, web fetch, todo, ask, delegate, skills |
| **Sub-agents** | typed workers, each with its own context and briefing |
| **Permissions** | every call passes a gate you answer; decisions persist and scope to a folder |
| **Tool selection** | [narrow what an agent is offered](docs/api.md#tool-selection) — per session, turn, or type |
| **MCP** | stdio and HTTP servers |
| **Providers** | Anthropic, OpenAI-compatible, Ollama — several at once |
| **Stores** | resume a session; a usage archive you can query |

### The documentation

| | |
|---|---|
| **[api.md](docs/api.md)** | every public type, configuration, and tool selection |
| **[tools.md](docs/tools.md)** | injecting tools of your own |
| **[examples/ToolAgent](examples/ToolAgent)** | a runnable console app with an injected tool |
| **[examples/SpectreAgent](examples/SpectreAgent)** | the same, with a rendered UI |

Below: the mental model, then the four calls an app makes.

---

## The mental model

**A session is a conversation. An agent is the loop that runs one turn of it. You supply where words
go.** Everything else follows from those three sentences.

```
        your app
           │  supplies an observer, a working directory, a model
           ▼
    SessionManager ──── owns what sessions SHARE:
           │             the resume database, the usage archive, the log directory,
           │             the permission rules store, the command registry
           │
           ├── opens ──► Session ──── one conversation
           │                │          · Submit(text) starts a turn
           │                │          · a queue for text typed while one runs
           │                │          · says everything through YOUR observer
           │                ▼
           │            AgentHost ──── owns the agent, its plugins, its MCP binding
           │                ▼
           │              Agent ────── the turn loop:
           │                             send → tool calls? → run them → send again
           │                             ▼
           │                        tools, each wrapped in a permission gate
           │                             ▼
           └────────────────────► sub-agents: an Agent with no session of its own
```

**One turn at a time per session.** `Submit` while a turn runs does not start a second one — it
queues the text, and the running turn picks it up at its next tool call.

**Nothing here writes to a console, opens a window, or ends a process.** Every word the session
produces goes through the `ISessionObserver` you hand it.

**What is actually yours to write** is small, and it is all at the edges:

| | |
|---|---|
| `ISessionObserver` | where words go — the one thing you must implement |
| `IToolObserver` | tool activity; a no-op is fine |
| a prompt hook + a `PermissionPolicy` | how a human is asked, and which folder to judge against — see [Permissions](#permissions) |
| a model | see [Configuration](#configuration) |

The conversation, the turn loop, the tools, delegation, permissions, compaction, MCP and the stores
are all in here.

---

## Who creates what

The thing that surprises people is how little you construct. `SessionManager.Create` builds the
shared machinery; `Open` builds everything per-session.

| You create | `SessionManager.Create` creates | `Open` creates, per session |
|---|---|---|
| `AppPaths` (where config and data live) | the resume database (SQLite) | the `Session` |
| an `ISessionObserver` | the usage archive (a *different* database) | its `AgentHost` and `Agent` |
| an `IToolObserver` | the log directory manager | the plugin registry, gated |
| a model — see [Configuration](#configuration) | the permission rules store | the sub-agent spawner |
| *optionally* a permission gate | the command registry | the agent-type catalog |

You never construct an `Agent`, an `AgentHost`, or a tool. `AgentHost` is not even public.

**Logs.** One directory per session, named by its id, under your config directory. A sub-agent gets
its own directory *nested inside its parent's* — so a finished child is inspectable, and `ls -t`
never shows a child above the session that spawned it.

---

## An app is four calls

```csharp
using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;

using var manager = SessionManager.Create(new AppPaths(configDir));

var session = manager.Open(
    workingDirectory,                                        // the permission boundary
    resolution,                                              // which model — see below
    new SessionPorts { Observer = sink, ToolObserver = jobs });   // where words and tool activity go

if (session.Submit("summarise this folder") is Session.SubmitOutcome.Started started)
    await started.Turn;
```

`Submit` is **synchronous**. It returns a receipt, not a task, because there are three outcomes and
they are three different things for a caller to do:

| Outcome | What happened | What you do |
|---|---|---|
| `Started(Task Turn)` | a turn began | await it, or attach a continuation and keep rendering |
| `Queued` | a turn was already running; this went to the queue | nothing — the `Pending` event already fired |
| `NoAgent` | no model is wired | leave the text in your input box |

---

## Where words go

`ISessionObserver` is the only thing you *must* implement. Eight methods:

```csharp
internal sealed class ConsoleSink : ISessionObserver
{
    public void UserTurnAdded(ChatMessageId id, string text) { }   // the prompt went in
    public void AssistantTurnBegan(ChatMessageId id) { }

    // Streaming body, token by token. Written raw: this is model output, and a bracket in it
    // is not a colour tag.
    public void AssistantTextAppended(ChatMessageId id, string token) => Console.Write(token);

    public void AssistantReasoningAppended(ChatMessageId id, string text) { }   // show it, or don't
    public void AssistantTurnEnded(ChatMessageId id) => Console.WriteLine();
    public void AssistantLabelled(ChatMessageId id, string header) { }

    // The SESSION's own words — "Stopped.", a mode change, a model switch — in Core's markup
    // dialect. Render the tags or strip them; never print them raw beside model output.
    public void Said(string message) => Console.WriteLine(Strip(message));
    public void Failed(string message) => Console.Error.WriteLine(message);

    private static string Strip(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\[/?[^\]]*\]", "");
}
```

`ChatMessageId` identifies a turn so you can stream into the right row. Ids are minted by the
session, so a parent and its children never collide.

`IToolObserver` is optional — pass a no-op if you do not want to show tool activity:

```csharp
internal sealed class ToolSink : IToolObserver
{
    private readonly HashSet<string> _announced = [];

    // ANNOUNCE FROM HERE. This fires while jobs RUN; ToolUpdated fires when one FINISHES, so
    // announcing starts from there prints nothing — a finished job is never Running.
    public void ToolsChanged(IReadOnlyList<Job> jobs)
    {
        foreach (var job in jobs)
            if (job.State is JobState.Running && _announced.Add(job.Id))
                Console.WriteLine($"  · {job.PluginType}");
    }

    public void ToolUpdated(Job job) { }
    public void ToolProgressed(Job job) { }
    public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) { }
    public void ToolOutputAppended(string jobId, string delta) { }
}
```

`BufferedChatSink` and `BufferedJobPanel` ship with the package if you only need to collect output —
they are what the tests use.

---

## Text typed while a turn runs

**`Submit` handles this.** Called mid-turn it queues the text and returns `Queued`; you do not call
`Steer` yourself. What is queued arrives at the turn's **next tool call**, where the model can still
act on it — and several lines typed in a burst become one message rather than several.

```csharp
session.Pending   += (whole, added) => ShowQueuedBlock(whole);
session.Drained   += text           => RemoveQueuedBlock();      // the turn took it
session.Cancelled += text           => PutItBackInTheInputBox(text);
```

`session.CancelTurn()` stops the turn, says "Stopped." through your observer, and hands anything
queued back through `Cancelled` — it does not decide where that text goes.

---

## Tools

The agent gets these without you registering anything: **read, write, edit, glob, grep, shell, http,
web fetch**, plus `todowrite` (a plan it keeps across turns), `ask_user` (a question back to you),
`agent` (delegation) and `skill`. Each is wrapped in a permission gate at construction, so there is
no path that runs a tool ungated.

Whether the model *uses* a tool is the model's business, not the library's.

**Offering fewer of them is yours.** A `ToolSelection` narrows what an agent is handed — at
construction, per session, or for one request:

```csharp
ToolSelection = new ToolSelection([Tool.Inherited, Tool.Not.RunShell]),
```

`Tool` names every built-in, so the terms are checked by the compiler rather than spelled: `Tool.Glob`,
`Tool.Not.RunShell`, `Tool.Also.Grep`, `Tool.Inherited`, `Tool.All`. A withheld tool is refused if
called by name, not merely hidden. **[The terms, the levels, and what selection does not reach →](docs/api.md#tool-selection)**

### Tools of your own

An embedder can add tools this library could not have anticipated — a `deploy(env)`, a
`queryOurWarehouse(sql)`, something that draws into your own UI. One interface, one line of wiring:

```csharp
Tools = [new DeployTool(), new QueryWarehouseTool()],
```

They are offered to the model beside the built-ins, dispatched behind them (so a consumer cannot
shadow `read_file`), and gated on the way through — a tool you inject cannot run ungated, because the
wrapping happens where the gate and the session's policy both are rather than being left to you to
remember.

Your tool answers one question the engine cannot: does **this call** need a human. Everything else —
asking, persisting the answer, scoping it to the folder — is already here.

**[Injecting your own tools →](docs/tools.md)**

---

## Permissions

**The engine is in here. The only thing left to you is asking a human.**

Trust per folder, the working-directory boundary, edit modes, stored rules, the read-only command
list, the `Auto` classifier, and the wrapper that gates every tool — all of it is Core, and none of
it is yours to build. What Core cannot do is put a question on a screen, so you supply one delegate:
*given this request, what does the human say?*

```csharp
buildGate: store => PermissionDecider.WithPrompt(store, notice, promptHook)
//                                                ▲       ▲       ▲
//                                    Core hands   │       │       └── YOURS: ask, somehow
//                                    you this ────┘       └── optional: one-line notices
```

**Two halves, and one without the other is silent.** `buildGate` gives the decider a way to ask;
`SessionPorts.Policy` gives it the folder and the edit mode to judge against:

```csharp
var policy = new PermissionPolicy(workingDirectory, manager.Rules!, EditMode.AlwaysAsk);

var session = manager.Open(workingDirectory, resolution,
    new SessionPorts { Observer = sink, ToolObserver = jobs, Policy = policy });
```

Omit the policy and **every request is refused** — the decider has no directory to judge a path
against and no edit mode to read, so it declines rather than guessing.

If you pass no `buildGate` at all, **nothing is gated** — every tool runs. That is a legitimate
headless arrangement (a batch job in a container), but it is a choice, not something to inherit by
forgetting.

### How a request reaches you

```
  the model calls a tool
        ▼
  PermissionGatedPlugin wraps every built-in tool
        ▼
  PermissionPolicy.RequestsFor(...) turns the call into one or more requests
        │    a shell command → one Shell request
        │    a file copy     → a FileRead AND a FileWrite, judged separately
        ▼
  the policy answers silently if it can  ──── yes ──► the tool runs, you are never asked
        │
        no
        ▼
  YOUR promptHook            ──── you ask a human, however you like
        ▼
  Once / Always / Deny / TrustFolder
```

### Supplying one

`SessionManager.Create` takes a **delegate that builds a gate**, not a gate — the gate needs the
rules store, and the store is created inside:

```csharp
var manager = SessionManager.Create(paths, buildGate: store =>
    PermissionDecider.WithPrompt(
        store,
        notice: line => Console.Error.WriteLine(line),   // "auto-refused" and rule notices; may be null
        promptHook: async (request, offerTrust, ct) =>
        {
            Console.Error.WriteLine($"{request.Kind}: {request.Display}");

            // Null means this cannot be honestly generalised — do NOT offer an "always" button.
            if (request.AlwaysRule is { } rule)
                Console.Error.WriteLine($"  \"always\" would cover: {rule}");

            return await AskAHuman(ct);
        }));
```

| Your hook returns | |
|---|---|
| `Once` | allow this, ask again next time |
| `Always` | allow and persist `request.AlwaysRule` for this folder |
| `Deny` | refuse — the model sees the refusal and can adapt |
| `TrustFolder` | trust the working directory, unlocking the silent class inside it |

**Cancellation must resolve your prompt, not abandon it.** The token exists so a cancelled turn does
not leave a dialog waiting for a click nobody will make; Core treats a cancelled request as a
refusal.

### What gets asked, and what does not

A request is judged by layers that only ever *narrow*, before your hook is reached:

| Layer | Effect |
|---|---|
| **Trust** | per folder, identified by path *and* birth time — delete and recreate a folder and it inherits nothing |
| **Edit mode** | `AlwaysAsk` (every write asks), `AcceptEdits` (in-boundary writes are silent), `Auto` |
| **Stored rules** | what a human answered "always" to, confined to the boundary |
| **The boundary** | your working directory, symlinks resolved — a link pointing out is out |
| **Read-only verbs** | `ls`, `cat`, `grep` and friends run silently in a trusted folder, but only when every path they name is inside the boundary |

`Auto` adds a model that reviews what would otherwise ask. It can only **refuse** — it runs after the
floor has already said yes, so it adds friction and never removes it. A timeout, a transport error,
or a verdict it cannot parse all mean ask.

Everything fails toward asking. An unresolvable path is outside the boundary; a command carrying a
token nothing could classify is refused rather than allowed.

---

## Commands

A session services the same commands cxagent exposes, headlessly:

```csharp
session.SetMode("edits auto");        // or SetMode(WorkingMode)
session.UseFromInput("openrouter");   // /model — parses what a user typed
session.ListSessions("all");
session.SayUsage("30");               // /stats
session.ClearContext();
session.CompressNow(ct);
```

Each does the work, **says its own result through your observer**, and returns a `CommandStatus`:
`Reported` (it said something, nothing moved), `Changed` (the session moved), `Refused` (it could
not run now, and said why), `Unknown` (nothing services this). `.Handled()` collapses that to a bool
if you are routing input.

`manager.Commands` is the registry they are seeded into. Register your own on top — last
registration wins — for anything only your front end can service.

---

## Sub-agents

A session in fan-out mode (the default) can delegate. A child is an `Agent` with **no session**: its
own context, its own log directory nested under the parent's, its own token budget. It reports back
as a tool result, and no child outlives the turn that started it.

Pass `AgentMode.Single` if your front end has nowhere to show a child's progress. Delegation is
capability rather than permission — a child runs under the same gate, in the same folder.

---

## Configuration

Three ways in, depending on where your settings live. See the
[API reference](docs/api.md#configuration) for every field.

```csharp
// From code — no config.json, no file at all.
var resolution = new AgentConfig
{
    Models =
    {
        ["local"] = new(ProviderKind.OpenAiCompatible, "qwen3.6-35b")
                    { BaseUrl = "http://localhost:8771/v1", ContextWindow = 212_992 },
    },
    DefaultModel = "local",
}.Resolve();

// From the same config.json cxagent reads — useful for sharing a setup.
ConfigResolver.Resolve(paths, env, useMock: false);

// For a test.
ResolvedConfig.ForTesting(provider, instanceName);
```

Mistakes come back in `resolution.Errors` with `HasProvider` false — **never as exceptions**, because
a caller assembling this from its own settings wants to say what went wrong.

Providers: `anthropic`, `openai-compatible`, `ollama`. Several at once, and a sub-agent type can name
a different one.

---

## Watching a session work

```csharp
session.TokensUpdated      += (_, total) => …;   // after each provider call
session.ContextUsedUpdated += (_, used)  => …;   // how full the window is
session.ContextCompressed  += (_, e)     => …;   // (Before, After)
session.TurnCompleted      += (_, turns) => …;
session.Changed            += kind       => …;   // Mode, Model, Resumed, TurnCancelled, ContextCleared

session.Ledger      // spend by model and by agent, cache rates, cost
session.IsBusy      // a turn is running
session.Mode        // delegation and edit mode
```

**Subscribe after opening the session** — these attach to the host that exists at subscription time.

---

## Reference and examples

- **[API reference →](docs/api.md)** — every public member, with parameters and who calls it
- **[Injecting your own tools →](docs/tools.md)** — the `IAgentTool` interface, the two gates, and
  what `Gate` should return
- **[SpectreAgent →](examples/SpectreAgent)** — a second front end in about a hundred lines: a
  prompt, streamed text, one line per tool
- **[ToolAgent →](examples/ToolAgent)** — injecting your own tools, and the two gates each one
  passes through

## License

MIT. See [LICENSE](https://github.com/nickprotop/cxagent/blob/master/LICENSE).
