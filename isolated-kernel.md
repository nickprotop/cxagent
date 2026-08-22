# The isolated kernel

What has to be true for `Core/` to be a library someone else can host, and how far it already is.

This is not a rewrite. The hard direction is already done — the measurement below is the reason this
document is short.

---

## Where the boundary already holds

```
grep -rn "using CxAgent.UI|using ConsoleEx|Spectre" cxagent/Core/   →   no matches
```

**`Core/` does not know the UI exists.** No widget type, no console driver, no rendering library
crosses into the kernel. Everything the kernel says goes out through interfaces it defines itself,
and `BufferedChatSink` — 100 lines, no terminal — is the standing proof: a sub-agent runs headless
through the same `Agent` class the TUI drives.

So the question is not "how do we decouple the presentation." It is **"what does the kernel still
reach *down* into?"** — and the answer is: this app's disk layout, in four places, none of them deep.

---

## The four channels

Four kinds of traffic cross the seam. They are not interchangeable, and the reason each is what it
is comes from its timing, not from taste.

| Traffic | Mechanism | Why this one |
|---|---|---|
| **Streaming output** — tokens, reasoning, turn boundaries | `IChatSink` (8 methods), `IJobPanel` | Ordered, high-frequency, must not be dropped. An interface makes the compiler demand every case be handled; a missed event just vanishes. |
| **Telemetry** — tokens, occupancy, compaction | `event` | Fire-and-forget, lossy is fine, several consumers. These were `Action<T>` until a child's reporter *and* an aggregator both needed to listen. |
| **Permission** — *may I run this?* | `IPermissionGate`, async, returns `bool` | The only channel that is a **question**. It blocks the running turn until a human answers. Neither an event nor a hook can do that. |
| **Config** — what this session is | ctor snapshot, **+ `Apply` between turns** | See below. Today only the first half exists. |

### Why not hooks, and why not IPC

**Hooks** are events with a worse name — same delivery, same losses, less type safety.

**IPC** would be a serious mistake here, and the permission gate is why. It must block a live turn on
a human answer. Cross a process boundary and you own reconnection, timeouts, ordering, and a protocol
version — for a presentation layer that ships in the same binary. MCP pays that cost because its
servers are third-party. Ours is not.

The interfaces *are* the API. They are small, they are compiler-enforced, and one of them has already
been implemented twice.

---

## What the kernel still reaches down into

Measured, not assumed. Every claim below is a grep.

### 1. `LogFileManager` — should be an event

`Core/` holds a `LogFileManager?` in `Agent`, `SubAgentFactory` and `JobContext`. But look at the
call:

```csharp
_ = _logs.AppendAsync(agentId, $"context-{turn:D3}", "log", sb.ToString());
```

Fire-and-forget, result discarded. **That is already event semantics written as a method call.** The
type is a *file* manager — it owns a directory layout and a `PathFor` — so the kernel currently knows
that logs are files, that files live in a directory, and how that directory is arranged. None of
which is its business.

Replace with `event Action<AgentLogEntry>?`. The host writes them to disk, ships them to a log
server, or drops them.

Two things must survive the change:

- **The handler must not block.** The payload is a whole context dump; that is *why* it is
  fire-and-forget today. A synchronous subscriber doing file IO would stall a turn.
- **A throwing subscriber must not kill the turn.** Today a discarded `Task` swallows it. An event
  does not — the raise site has to.

This is the easiest real cut available and the one with the clearest payoff.

### 2. `SqliteSessionStore` — already optional, should be an interface

Four lines in `AgentHost`, three of them null-guarded:

```csharp
public void MarkSessionFinished() => _store?.MarkFinished(_agent.Id);
_store?.SaveTurn(agent.Id, Context.Messages, Ledger.InputTokens, ...);
```

The coupling is nominal — the kernel names a concrete SQLite type for two verbs. A host that wants
Postgres, or a JSON file, or nothing, cannot say so. `ISessionStore` with those two methods costs an
afternoon and the null-guards are already in place.

### 3. Config reading — the kernel opens files

```
ProviderConfig.cs:248     var path = Path.Combine(paths.ConfigDir, "config.json");
ProjectInstructions.cs:146  var text = File.ReadAllText(path)...
PermissionRulesStore.cs:81  var json = File.ReadAllText(path);
```

This is the one that is genuinely wrong rather than merely concrete. **A hosted kernel should not
read `config.json`, should not know the file is called that, and should not know it is a file.**

Note what is *not* here: `AppPaths` has **zero real references in `Core/`**. Every hit was a doc
comment. The path abstraction already stayed out; only the readers came in.

### 4. Config cannot be invalidated

`AgentHost`'s constructor takes 16 parameters. That is not the problem — **config as composition is
correct**, decided once at wire-up. The problem is that it is *permanent*.

Exactly one thing can change mid-session: `Mode`, a settable property. It works because both things
it affects — the tool list and the system message — are rebuilt every turn anyway. `/mcp reload`
re-reads config from disk, but nothing else does.

**The missing half is `Apply(snapshot)`**: the host says *"that's stale, take this."* The kernel
never reads a file, never watches one, never owns a reload. The host decides what triggers it — a
file watcher, `/mcp reload`, a settings dialog, an API call.

Two constraints, both load-bearing:

- **The quantum is a whole resolved snapshot, never a field.** Config is not independent fields.
  `AgentType` exists precisely because a provider and its context window must resolve *together*: a
  child given one provider with another's window sees `IsUnderPressure` permanently false, never
  compacts, and dies on overflow. Per-field invalidation reintroduces exactly that bug.
- **Applied between turns, never during.** Same rule `/mode` already enforces — declined mid-turn so
  a tool cannot appear or vanish under a model that is mid-request. A turn boundary is the only safe
  point, and the kernel already has one.

---

## What stays

### `ChatMessageId` stays in the kernel

```csharp
public readonly record struct ChatMessageId(long Value);
```

A wrapped `long`. No rendering, no widget. It was briefly filed as "a UI concept in `Core/`" — wrong,
and wrong in an interesting direction: **the kernel needs message identity for its own reasons, and
currently under-uses it.**

- **Compaction** replaces a span of messages. *Which* span is an identity question, answered today
  by an index into a mutable list.
- **Resume** correlates stored rows with live ones.
- **The orphan hazard** — a tool call with no matching `tool` message, which 400s the session
  permanently — is literally a broken correlation. Correlation is what ids are for.
- **A child's messages** must be attributable to the child.

An index into a `List<ChatMessage>` is an id that silently changes meaning when the list mutates:
stable-looking and not stable. If anything, identity should go *deeper* into the kernel, not out of
it.

What is genuinely display-shaped in `IChatSink` is narrower — that `AppendAssistant` streams
*tokens*. A headless host wants finished text. `BufferedChatSink` absorbs it, but it is absorbing
something it should not have been handed.

---

## The shape when done

```
KERNEL                                          HOST
Agent · AgentContext · ILlmProvider
JobRegistry · McpToolset
AgentTypeCatalog · TokenLedger

  ── streaming ──▶  IChatSink, IJobPanel        TUI · buffer · anything
  ── telemetry ──▶  events (incl. logs)         status line · files · /dev/null
  ── question ──▶   IPermissionGate  (blocks)   dialog · policy · auto-deny
  ◀── config ────   ctor + Apply(snapshot)      config.json · env · API
```

Everything else — `AppPaths`, session storage, config file loading, resume — becomes an optional
collaborator the host supplies or omits.

---

## Order of work

Cheapest and most isolating first. None of these is large; they are listed in the order that leaves
the tree working after each step.

| # | Change | Size |
|---|---|---|
| 1 | `LogFileManager` → `event Action<AgentLogEntry>?` | small |
| 2 | `SqliteSessionStore` → `ISessionStore` (2 methods) | small |
| 3 | Config snapshot type; kernel stops calling `File.ReadAllText` | medium |
| 4 | `Apply(snapshot)`, applied at turn boundaries | medium |

**Not on this list: decoupling the presentation.** That is done, and the grep at the top is the
receipt.

---

## Extraction: the namespace, and when to move it

The measurements first, because they decide the order:

```
78 files in Core/, every one already under CxAgent.Core.*
grep "using CxAgent;" in Core/            →  no matches       (no leak upward)
packages: SharpConsoleUI  →  UI only
          Microsoft.Data.Sqlite  →  ONE Core file
```

So the tree is already namespace-clean and one package away from splitting. Which means the rename
buys nothing on its own — and doing it first actively costs.

### Why the namespace is step 5, not step 1

A rename touches all 78 files and every `using` in `UI/` and the tests. Do it **before** items 1–4
and every one of those diffs is then reviewed against a moved baseline: a `git log` that says
"extracted logging to an event" is unreadable if the same commit moved the file. Worse, the rename is
the one change with **zero behavioural risk**, so spending review attention on it first is spending
it on the safest thing available.

Do it **after**, and the rename is a pure mechanical commit — no logic in it at all, reviewable by
its own diffstat.

There is also a correctness reason. Items 3 and 4 will introduce a config snapshot type and probably
an `ISessionStore`. Where those live is a *design* answer that falls out of doing the work. Naming
the package first means guessing the shape, then either living with a wrong name or renaming twice.

### The decision

**Name: `cxagent.kernel`. Lowercase. One package. A sibling of the app, not a subtree of it.**

Not `CxAgent.Core` kept as-is — "Core" is what every project calls its biggest folder; it says
"important," not "hostable," and it is the name that let config readers wander in. **Kernel** carries
the actual claim: this thing runs an agent and knows nothing about how you look at it.

**Lowercase**, matching what is already true and what people will type: `AssemblyName` is `cxagent`,
the binary is `cxagent`, and the install line is `dotnet add package cxagent.kernel`. Namespaces stay
`CxAgent.Kernel.*` per C# convention — it is the package id and assembly that go lowercase, and they
already do.

**One level up.** `CxAgent` stops being the application and becomes the family prefix, with peers
under it:

```
cxagent.kernel      CxAgent.Kernel.*     the library    (was CxAgent.Core.*)
cxagent             CxAgent.Tui.*        the TUI        (was CxAgent.UI)
cxagent.web         CxAgent.Web.*        next
```

The nesting is the whole point. `CxAgent.Core.Agent` reads as *a part of the app* — which is what it
was, and what let `File.ReadAllText("config.json")` seem reasonable inside it. `CxAgent.Kernel.Agent`
reads as *a library the app happens to use*, and makes a config reader in there look as odd as it is.
A namespace that describes the intended relationship does some of the enforcing.

This costs nothing to claim: **nothing currently sits at bare `CxAgent`.** The tree is 78
`CxAgent.Core.*`, 35 `CxAgent.UI`, 90 `CxAgent.Tests` — no squatter to evict, so the prefix is free
to become a family name.

`UI` → `Tui` is no longer optional once a second host exists: with a web front end, "the UI" is
ambiguous and the name has to say which.

Rejected: `CxAgent.Agent` (`CxAgent.Agent.Agent` is a real type path), `CxAgent.Abstractions` (this
is an implementation, not a contract set), `CxKernel` (loses the family).

### The web host is the forcing function

A second presentation is what turns items 1–4 from tidiness into requirements. Each channel meets a
real test:

| Channel | In the TUI | Over the web | Verdict |
|---|---|---|---|
| **Streaming** `IChatSink` | widget appends | SSE or WebSocket frames | **Already proven.** `BufferedChatSink` is a non-terminal implementation that works. |
| **Telemetry** events | status line | pushed to the client, or dropped | Fine. Lossy by design. |
| **Permission** `IPermissionGate` | a dialog | a round trip that may never return | **Works, and vindicates the design.** It is `async` and returns `bool`; the host crosses the wire, the kernel just awaits. Had IPC been built *into* the kernel, this would be the thing to unpick. |
| **Config** ctor + `Apply` | one `config.json`, one user | no config file, N users, per-request settings | **Item 3 becomes mandatory.** A kernel that opens `config.json` cannot serve two users with different providers. |

Two things a web host will expose that the TUI never pressed on, and both are worth naming now
rather than discovering later:

- **Identity stops being implicit.** One process, many conversations. `ChatMessageId` earns its place
  properly here, and the positional-index shortcuts in compaction become bugs rather than smells —
  see *What stays* above.
- **Lifetime is not the process.** A TUI session ends when the binary exits; a web session outlives
  any one request. `MarkSessionFinished`, resume, and cancellation all need an owner that is not
  `Main`. `ISessionStore` (item 2) is where that starts.

Neither changes the plan below. Both are reasons not to skip items 2 and 3.

### The other consumers

The web host is not the end of it. `lazydotide` and `cxlog` are the same family and want the same
kernel — which changes the risk profile, because **a shared kernel freezes its first consumer's
assumptions.** Today every assumption in `Core/` is `cxagent`'s. Items 1–4 are, precisely, the list
of places where that is true.

**`IPermissionGate` is what they would otherwise reinvent badly.** A log viewer and an IDE both have
"the agent wants to run this / touch that" moments. A blocking async question, with rules storage and
per-session grants, is more work than it looks — and it is built, and it is the right shape.

**Config stops being about a filename.** Three apps means three config layouts. Item 3 is not "stop
reading `config.json`", it is **the kernel has no opinion about where settings come from**. That is
what the snapshot type is for.

### Tools: the builtins must be subtractable

`JobRegistry` is already half of this. Adding is solved:

```csharp
public void Register(IJobExecutor executor)                  // hosts can add their own
public static JobRegistry CreateWithBuiltins(...)     // all four, or nothing
```

So lazydotide can register an editor tool today. What is missing is **subtraction** —
`CreateWithBuiltins` is all-or-nothing and there is no `Remove`. A host that wants shell but not http
must skip the factory and hand-register from the inside, which means depending on `ShellJobExecutor`,
`FileJobExecutor` and `PermissionGatedExecutor` as public API. That is the wrong thing to make public: it
turns four internal classes into a contract, and the wrapping in `PermissionGatedExecutor` — easy to
forget, silently ungated if you do — becomes the host's problem instead of the kernel's.

The fix is small and belongs with item 3, since "which tools" is config:

```csharp
CreateWithBuiltins(providers, permissions, BuiltinTools.Shell | BuiltinTools.File)
```

A flags enum, defaulting to all. Selection stays inside the kernel, so the permission wrapping stays
inside too, and no builtin type needs to become public. Hosts that want more still call `Register`;
hosts that want fewer no longer need to know how the four are constructed.

What this does **not** do is make the builtins optional to *ship*. They stay in the package.
`ProcessRunner` and the file executor assume a working directory and a filesystem — fine for cxlog,
questionable for lazydotide (which has its own notion of the open project), wrong for a multi-tenant
web host. Those hosts pass a narrower flag set, or none, and register their own. **The kernel ships
the mechanism and a default; it does not insist on the default.**

**Layout — one package, not several:**

```
CxAgent.Kernel/                     ← new csproj, netstandard2.1 or net10.0
  Agent/        AgentContext, Agent, SubAgentFactory, AgentTypes, IChatSink, IJobPanel
  Llm/          ILlmProvider, ProviderRegistry, SystemPrompt, Providers/
  Mcp/          McpClient, McpToolset, Auth/
  Jobs/         JobRegistry, Builtin/
  Permissions/  IPermissionGate, rules
  Execution/    ProcessRunner, JobContext, JobDigest
  Models/

cxagent/                            ← stays an Exe, references the above
  UI/  Storage/  Program.cs
```

Splitting further — `Kernel.Abstractions`, `Kernel.Mcp` — is premature. There is one consumer. Split
when a second one wants a subset, not before.

**`Storage/` does not come.** `AppPaths`, `SqliteSessionStore` and `LogFileManager` are this app's
disk layout. That is exactly what items 1–3 exist to sever, and it is also what drops
`Microsoft.Data.Sqlite` — leaving a kernel with **no package dependencies at all**, which is the real
prize. A kernel that drags in a SQLite driver is not a kernel anyone wants to embed.

### Steps 5–7

| # | Change | Size | Note |
|---|---|---|---|
| 5 | `CxAgent.Core.*` → `CxAgent.Kernel.*`, one mechanical commit | small | pure rename, no logic |
| 6 | New `CxAgent.Kernel.csproj`, files moved, exe references it | small | build enforces the boundary the grep currently only observes |
| 7 | `dotnet pack` | small | only worth doing once someone wants it |

Step 6 is the one that matters, and not for packaging: **a project reference makes the boundary
compiler-enforced.** Today "Core does not reference UI" is a property that happens to hold and that a
single careless `using` would break silently. After the split it cannot compile. That is worth having
whether or not a `.nupkg` is ever published.

Do 5 and 6 in that order and separately. A rename and a project split in one commit is two kinds of
noise no reviewer can separate.
