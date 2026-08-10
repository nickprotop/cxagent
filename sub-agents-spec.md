# Sub-Agents Design Spec

**Status:** the agent model is built and tested. What remains is a tool to spawn one, and a short
list of solvable problems in permissions, presentation and shared state.

This document was written before the context refactor and described much that has since been built;
it has been cut to what is still true. It was then reviewed against the code and the reference
implementations, and several of its own claims were found wrong — those corrections are marked in
place rather than quietly edited out, because the wrong version was persuasive.

---

## 0. Decisions and open questions

Everything a reader needs before reading the rest. Detail and evidence in the sections named.

### Decided

| # | Decision | Where |
|---|---|---|
| D1 | Every agent is one `Agent` class. An orchestrator is one holding a spawn tool. | §1 |
| D2 | Config flows one way: **app → parent → sub-agent**. One level, no nesting. | §1 |
| D3 | **Spawn, stop, collect.** No mid-run ask, no examine. A SCOPE decision — both references DO support resumption. | §2 |
| D4 | A sub-agent returns **the final assistant text**. No summarising round trip. | §2 |
| D5 | The tool takes a **type name**, never a model id. A model cannot pick from a catalog it has never seen. | §2 |
| D6 | Threading: `Task.Run` + marshal — but only once anything is concurrent. Step 1 runs the first child inline. | §1, §5 |
| D7 | One shared, thread-safe `TokenLedger`. Per-agent ledgers deferred until a UI needs the attribution. | §1 |
| D8 | A sub-agent's transcript is a **buffered sink**, inspectable on demand, not rendered into the parent's view. | §3 |
| D9 | **Three instruction channels, with precedence.** A type briefing (config, how to work) outranks spawn context (parent, what to know); the prompt (parent, what to do) is a user turn. | §2 |
| D10 | **Foreground first.** The tool blocks and returns the result. Background is a different tool — a registry, a notification route, a lifetime rule — and is a later step. | §2, §5 |
| D13 | **The result is an envelope from step 1: `id`, `state`, `text`.** An id retrofitted later must be threaded through every surface that already exists; adding it now costs a field. `Agent.Id` already exists. | §5 |
| D14 | **The child's id addresses its row.** Rows key on `job.Id`, minted per tool call; the spawn tool associates that with the child's `Agent.Id` so telemetry, and later background reporting and aggregation, all address the same row through one identifier. | §5 |
| D15 | **Dispatch is an `ISubAgentSpawner` consulted before `WorkerToolset`**, the MCP precedent — NOT a `WorkerTool` enum member, which would auto-offer spawn to every child and make the no-nesting exclusion false. | §5.1a |
| D16 | **`state` = `completed \| error \| cancelled`.** Cap and stuck report `completed` with their salvage text, because `Agent` has no outcome API. Stated rather than discovered: a `state` whose values are fiction is worse than none. | §5.1c |
| D17 | **The spawn branch never throws** (except cancellation). An exception mid-`foreach` orphans tool calls in the parent's context and poisons every later turn. | §5.1b |
| D11 | **Telemetry is in step 1**, not later — a child that reports nothing is a frozen row, and retrofitting it means revisiting the factory, the row and the panel. `Agent` already exposes `Id`, `Context` and four callbacks; `Job.ProgressMessage` already renders; `SessionPanel` already takes optional sections. Missing: elapsed time (a periodic tick), and the waiting state (step 3). | §2, §5 |
| D12 | **No general hook system.** A hook that can block IS the permission gate; one that cannot is telemetry, which the callbacks already give. Add named seams if a need appears. | §2 |

### Open — and these block work

| # | Question | Blocks |
|---|---|---|
| Q3 | Where does per-call requester identity come from? BOTH request-construction sites lack it — `IJobContext` hides the agent id, and the shared `McpToolset` never receives one. | Permission attribution (step 4) |
| Q4 | Does a sub-agent get its own working directory? It cannot today — cwd is process-global. | A worker in a worktree |
| Q5 | Is spawning itself permission-gated? opencode asks before spawning; we have not considered it. | §4 |
| Q6 | Shared-policy semantics: one worker's "Always" instantly widens policy for all of them. Deliberate or not? | Step 6 |

### Corrected after review — claims this document previously made and got wrong

- *"Neither reference supports mid-run interaction."* **False** — opencode resumes a child by
  `task_id` and injects context into a running one. D3 stands on its own merits, not on precedent.
- *"opencode returns the answer text, nothing more."* **False** — it wraps it in a structured
  envelope with an exit state. The no-summarising-call half is correct; the bare-text half was not.
- *"Only the plugin path needs identity threading."* **False** — both paths lack it.
- *"A null `ToolCallId` silently becomes a user turn."* **False** — it is a 400 from both APIs, and
  the append site already guards it.
- *"An agent has its own working directory."* **False** — it is process-global.
- *"A parent-written briefing is the mutable-briefing hazard."* **False** — a spawned child is
  constructed fresh, so its briefing is fixed for its life whoever wrote it (D9).
- *"`Job.ProgressMessage` is already rendered."* **False, and it was a BLOCKER** — that citation
  points at `JobBlockControl`, a control never placed in the grid. The live `InlineJobSink` reads
  `ProgressMessage` nowhere, so telemetry wired perfectly would still show a frozen row (§5.1e).
- *"The factory wires cancellation token and id."* **False** — `Agent` has no ct parameter and mints
  its own read-only `Id`. Neither is factory work.

---

## 1. What an agent is

Every agent is an `Agent` (`Core/Agent/Agent.cs`). Not a variant, not a subclass, not a mode — one class,
constructed as many times as there are agents.

**There is no such thing as a "parent agent".** An orchestrator is an agent that has been given a
spawn tool; a sub-agent is one that has not. That is the entire difference — not a different type,
not a different loop, not different context machinery.

**Configuration flows one way: app → parent → sub-agent.** The app configures the session's agent;
that agent supplies a sub-agent's briefing and sinks when it spawns one. A sub-agent reads no config
of its own and spawns nothing further, so there is exactly one level and one place any given setting
came from.

This is the whole design. Everything below follows from it.

### The kernel is out of the UI

`Agent`, `AgentHost`, `SessionCompressor`, `CompressionRun` and the two ports `IChatSink`/`IJobPanel`
live in `Core/Agent/`. **The layering is now proven by the compiler**, not by inspection: nothing in
`Core/Agent/` imports `CxAgent.UI`, so a headless consumer — a CLI, a server, a sub-agent driven by
another agent — needs no presentation layer at all.

The one coupling that blocked it is gone, and it was carrying a bug (§6).

### Self-containment is done

Built, and pinned by tests (`AgentTests`) so it cannot regress:

- **Its own context.** `context ?? new AgentContext()` — an agent constructed without one gets a
  fresh one, so a sub-agent can never append to its caller's conversation.
- **Its own system prompt**, at position 0 of its own context. Pinned against compaction by
  `PinnedHeadCount`. **Not its own working directory** — `Agent` has no cwd parameter and reads
  `Directory.GetCurrentDirectory()` fresh each prompt, so every concurrent agent shares one. A worker
  in a git worktree is currently inexpressible (§5).
- **Its own briefing** — what this agent was created to do, fixed at construction, joined to the
  system message last so it outranks the general and project prompts. Constructor-only on purpose:
  the system message is the prompt-cache prefix, and a mutable briefing would rewrite it mid-session.
- **Its own compression**, run against its own context on its own measurement.
- **Its own id**, stable for its life, keying its logs.
- **Its own sinks.** `IChatSink` and `IJobPanel` are constructor parameters, and the agent emits
  SEMANTICS through them — `AppendAssistant` for body, `AppendReasoning` for thinking — never markup.
  Colour and escaping are the sink's decisions. That is what makes a sub-agent's transcript a
  different implementation rather than a different code path.

### Two decisions, now made

- **Threading: `Task.Run`, and every sink call marshals back to the UI thread.** Today everything
  resumes on the UI thread (no `ConfigureAwait(false)`, an installed sync context), so N loops would
  multiplex against rendering. Agents move to the pool; `EnqueueOnUIThread` is the existing pattern —
  it is what the MCP panel updates already use. **The cost is a shared-state audit** — wider than the
  sinks, which all already marshal: the shared MCP client writes to a stdio pipe without a lock. It
  is a precondition of STEP 3, not of step 1.
- **The token ledger is shared and thread-safe** (`Interlocked` throughout, breach raised once via
  compare-and-swap). Whether each sub-agent gets its OWN ledger, and how per-agent and per-model
  spend are then reported, is deliberately left open — to be decided when there is a UI that needs
  the attribution.

### Cancellation is done

Escape cancels the running turn: a per-turn `CancellationTokenSource` linked to the session's. The
token reaches the provider stream, the tool loop, and `ProcessRunner`, which kills the entire process
tree. A sub-agent inherits a token the same way, so stopping one is already expressible.

---

## 2. What an orchestrator can do to a sub-agent

**Spawn, stop, collect. Nothing else.**

An earlier draft also offered *ask it something mid-run* and *examine it*. Both are cut — as a SCOPE
DECISION, on their merits.

**Correcting the earlier justification, which was wrong.** This document previously claimed neither
reference has them. Both do: opencode's task tool takes an optional `task_id` that resumes a previous
sub-agent's session *"with its previous messages and tool outputs"* (`task.ts:47-49`), and its
background mode injects further context into a still-running task. Claude Code likewise continues a
spawned agent.

The cut stands on its own reasoning: a child whose context was nobody's but its own for the whole of
its work produces a result nobody reached into, and one fan-out level with no resumption is the
smallest thing that can be proved end to end. Resumption is a feature to add deliberately later, not
an absence to justify by appeal to others.

"Examine" survives only as the UI reading a sub-agent's own context to render it (§3). That needs no
protocol — the context is already a public property.

### How a spawn joins the turn loop

**A spawn is a tool call, and nothing about the loop changes.**

```
parent turn N
  model emits: spawn_agent { briefing }
  ├─ Job { AgentId, DisplayName } → the row appears
  ├─ factory builds the child (own context, own briefing, own buffered sink)
  ├─ await child.SendAsync(task, ct) → string
  │     the child streams into ITS OWN sink, never the parent's
  ├─ messages.Add({ Role="tool", ToolCallId=call.Id, Content=result })
  └─ the for-loop continues → parent turn N+1, with the answer in view
```

The parent's context grows by **two messages** — the assistant turn carrying the spawn call, and the
tool result — whatever the child did: forty turns, twenty tool calls, its own compaction. That is the
entire point: fan-out buys context, and the parent pays a tool exchange for it.

**`ToolCallId` is load-bearing, but the failure is LOUD, not silent.** Both wires branch on it
(`AnthropicWire.cs:20`, `OpenAiWire.cs:22`); with it null the message keeps `Role="tool"` and is sent
as a role the API rejects — a 400, not a silently mis-read user turn. The append site already guards
it (`Agent.cs:644`, `ToolCallId = call.Id ?? call.Name`), so a spawn result flowing through the
ordinary tool loop cannot get this wrong. **The rule for a custom spawn path: preserve that guard.**

The model can already emit several tool calls in one turn — the system prompt asks for it
("independent tool calls can go in one turn") — so parallel fan-out is expressible today at the
protocol level. Only the execution is sequential.

### What the spawn tool carries — THREE CHANNELS, with precedence

`Agent` takes a **briefing** and a **prompt** separately, and they are not two names for one thing.
The difference is mechanical, not stylistic:

| | Briefing | Prompt |
|---|---|---|
| Lands in | the **system message**, index 0 | a **user turn** |
| Survives compaction | **yes** — `PinnedHeadCount` pins index 0 | no — summarised away with the older half |
| Fixed for | the agent's whole life | that turn |

So a long-running child eventually forgets its prompt and never forgets its briefing. That maps
exactly onto **how to work** versus **what to do**.

#### The three channels

```
system message = general prompt
               + project instructions (CXAGENT.md)
               + type briefing        ← config: HOW to work        (highest precedence)
               + spawn context        ← parent, per call: what to KNOW
user turn      = prompt               ← parent, per call: what to DO
```

Ordered so the type's briefing outranks the parent's addition — the same rule project instructions
already follow over the general prompt. `"read only"` from config then cannot be overridden by a
parent that fancies writing: **the parent contributes situational context, not authority.**

#### Why the parent gets to write anything at all

An earlier draft argued the parent should NOT, on the grounds that a parent-written briefing is "the
mutable-briefing hazard the constructor-only rule exists to prevent." **That was wrong.** A spawned
child is CONSTRUCTED FRESH for each spawn (`Agent.cs:167`), so its briefing is fixed for that child's
entire life whoever wrote it. The constructor-only rule is about not mutating a LIVING agent's
briefing; it has nothing to say about who supplies one at birth. The cache argument does not apply
either — a new child has no prefix to invalidate.

The real case FOR it: the parent knows things config cannot. *"The build is currently broken in
IndentShift.cs; ignore that file."* *"The regex approach was already tried and failed."* A fixed type
cannot express that, and it is often exactly what stops a child wasting turns.

The real case AGAINST, which the precedence rule answers: two instruction sources with no stated
winner is silent capability escalation, and the parent is a MODEL — standing instructions written by
a model, for a model, that no human reviewed. Config types are written and inspectable; a generated
briefing is neither. Hence: the parent may add context, the config decides the rules.

#### For step 1

There are no configured types yet, so the parent's spawn context IS the whole briefing. That is fine,
and it means step 2 **adds a higher-precedence layer** rather than removing a channel — the opposite
of what the earlier draft predicted.

### How the two references actually do it

Read from source, because the mechanics change what "add background later" costs.

**opencode.** A sub-agent is a full child SESSION, created with `parentID: ctx.sessionID`
(`task.ts:136-160`) — not a lightweight object. Crucially there is **ONE execution machine with two
waiting modes**: even the foreground path calls `background.wait({ id })` (`task.ts:317-335`), so
foreground IS background, awaited. That is why `background: true` is a cheap flag for them and would
not be for us — they built the async registry first and blocking is the special case.

Their result is an envelope, `<task id state>`, and the id in it IS the handle that makes `task_id`
resumption possible. Three states reach the parent: `completed`, `error`, `cancelled`. Spawning is
itself permission-gated (`ctx.ask({ permission: "task", patterns: [subagent_type] })`), and nesting
is bounded by a configurable `subagent_depth` (default 1) rather than a structural rule.

**Claude Code.** Same shape from outside: a typed subagent with its own context returning a final
report; types defined in `.claude/agents/*.md` with their own model and tool list. Background and
continuation exist but are opt-in; the default is a blocking call.

**What that tells us:** background is an ARCHITECTURE, not a flag — a job registry, an id, a
notification route back into a turn loop that has already moved on, and a lifetime rule. And the
envelope is not a style preference: the id is what makes a handle possible, so **Q2 and background
are one decision seen twice**.

### Foreground or background — FOREGROUND FIRST, and it is a real fork in the design

**Step 1 is foreground: the tool call blocks until the child finishes, and returns its answer.** That
is what makes it a tool like any other, and why nothing in the turn loop changes.

Background is a genuinely different tool, not a flag:

| | Foreground | Background |
|---|---|---|
| The tool returns | the child's answer | **a handle, immediately** |
| The parent's next turn sees | the result | "started" — and must ask later |
| Needs | nothing new | a task id, a registry of live children, a way to collect, a rule for what happens to an unfinished child when the parent's turn ends |

opencode ships both, keyed off a `background` parameter, plus `task_id` to resume — which is why its
result is an ENVELOPE carrying `id` and `state` (Q2) rather than bare text: a handle needs an id, and
a poll needs a state. **If background is wanted, Q2 stops being a preference and becomes forced.**

Deliberately deferred, because a background child raises everything a foreground one does plus
lifetime: what cancels it, what happens if the parent finishes first, and what the user sees while it
runs unattended.

### What a sub-agent reports back — THE SEAM ALREADY EXISTS

`Agent` exposes settable callbacks and public state, and a spawned child is constructed by us, so the
factory can wire them wherever it likes:

| Already there | Carries |
|---|---|
| `Id` (`Agent.cs:54`) | stable identity for its life — what a row and an aggregator key on |
| `Context` (`:118`) | `Used`, `Window`, `UsedFraction`, `TotalChars` — occupancy and limit |
| `TurnCompleted` (`:122`) | tool-call count per turn — turn counting |
| `ContextUsed` (`:132`), `ContextEstimated` (`:140`) | live occupancy |
| `ContextCompressed` (`:136`) | before/after — compaction visibility |

`AgentHost` already republishes exactly these five as events for the session agent, all marshalled to
the UI thread (`AppBootstrap.cs:200-214`). **A child's factory wires the same callbacks to a
per-child row instead**, which is what makes the §3.1 row (`⠹ 1m14s · 12.4k/208k`) buildable.

Two things NOT there and needed for the row: **elapsed time** (nothing publishes a running clock) and
**a waiting-on-permission state** (§3.1) — both land in step 3.

**A session aggregator is then a subscriber, not new plumbing** — it sums what the children already
report. Worth doing only once several children run at once; with one child the parent's own panel
already shows it.

### Hooks — what "the parent can act" would mean

Claude Code exposes lifecycle hooks (pre/post tool use, session start, and so on) that can observe
and BLOCK an action. Ours has one already, and it is the important one: **`IPermissionGate`**, which
every tool call passes through and which can refuse.

A general hook system is out of scope here, and it is worth being clear why: a hook that can block is
a permission gate by another name, and a hook that cannot is telemetry — which the callbacks above
already provide. If a specific need appears (a pre-spawn check, a post-turn assertion) it should be
added as that named thing, not as a general extension point nobody is using yet.

### Which agent a spawn asks for — NAMED TYPES, not a model id

**The tool takes a TYPE, never a model.** Checked against both references, which agree:

- opencode's `task` tool takes `description`, `prompt` and **`subagent_type`**, resolved through
  `agent.get()`; an unknown type is a hard error. The model lives on the agent DEFINITION
  (`agent.ts:45`, `Schema.optional`), not in the call.
- Claude Code is the same shape: `subagent_type` plus a prompt, with types defined in
  `.claude/agents/*.md` frontmatter carrying their own model, effort and tool list.

**Why neither puts a model id in the schema:** a model choosing from a catalog it cannot see will
invent names. `config.json`'s provider instances are ours, not the model's — it has never read them.
A type name is a small closed set that appears in the tool description, so the model picks from
something it can actually see, and a wrong pick is a clean error instead of a bad model id reaching a
provider.

**And the model is not the only thing that varies.** opencode's definition carries a prompt and
permissions alongside it. A "search" type is not merely *a small model* — it is a small model, plus
read-only tools, plus a prompt about searching. Binding those together is what makes a type mean
something.

It also fits the one-way config rule: **the app defines the types, the parent picks one, the child
gets whatever that type says.** A sub-agent still reads no config of its own.

#### The staging

1. **Hard-coded to the parent's provider.** Nothing new can go wrong; the spawn seam is proved on its
   own.
2. **Named types in config**, beside `mcp` — a name, a briefing prefix, and optionally a provider
   instance. Omitted means inherit the parent's, as opencode's optional model does.
3. **A tool subset per type**, if it earns it. Not before: capability is currently not withheld from
   anyone, and re-introducing that needs a better reason than symmetry with opencode.

#### What the factory must wire, beyond a constructor call

Found by review; each of these fails SILENTLY if the factory just calls `new Agent(...)`.

- **The context window and the compression threshold.** `context ?? new AgentContext()` gives a
  context with `Window = null` (`AgentContext.cs:69`), and with no window `IsUnderPressure` is
  **always false** (`:244`). With `compressAbove` also defaulting to null, a child **never
  auto-compresses** — it runs until the provider refuses. `AgentHost` threads both for the session
  agent (`:253`, `:356`); the factory must do the same. This is the same config-resolution layer as
  the provider gap below, and §5.1's "everything it needs now exists" omitted it.
- **The provider.** `ProviderRegistry` resolves an instance by name (`TryGet`, `InstanceNames`,
  `InstanceModels`) and `Agent` takes an `ILlmProvider`, so the seam exists — but the registry reaches
  neither `AgentHost` nor, in fact, `PluginRegistry`, which **discards it**: `_ = providers;   // kept
  in the signature` (`PluginRegistry.cs:50`). Nothing past the composition root can resolve a second
  provider today.
- **The working directory, which is PROCESS-GLOBAL.** `Agent` has no cwd parameter; it calls
  `Directory.GetCurrentDirectory()` fresh on every prompt (`Agent.cs:244`, `:1194`), and
  `ProcessRunner` defaults to the same. So "its own working directory" in §1 is false — every
  concurrent agent shares one, and a worker in a git worktree is currently inexpressible. A seam for
  this does not exist and is not on any list.

### What a sub-agent returns

**The answer text, nothing more.** `Agent.SendAsync` already returns `Task<string>` — the final
assistant text — and Claude Code and opencode both return exactly that. Settled here rather than left
open, because it IS the spawn tool's schema and the tool is the next step.

**NOT a summarising call afterwards.** This part is solid and checked: opencode takes the child's
last text part directly — there is no second LLM call — and its task prompt puts the burden on the
briefing instead: *"you should specify exactly what information the agent should return back to you
in its final and only message"*. Asking a finished child to summarise costs a round trip to compress
text already written to be read, loses the exact paths and counts a parent needs, and can fail on its
own.

**But "the answer text, nothing more" is NOT what opencode does, and this document said it was.**
opencode wraps the text in a structured envelope before returning it —
`<task id="…" state="completed|error"><task_result>…</task_result></task>` (`task.ts` `renderOutput`)
— which is the id-plus-exit-state structure this section previously rejected as *"a bigger surface
for a benefit nobody has asked for."*

Decide on merits, not on precedent: a bare string cannot distinguish "the child answered" from "the
child failed and this is its error", and the parent's model has to infer it from prose. An envelope
is cheap and makes the distinction explicit. **Recommendation: bare text for step 1, revisit the
envelope when the failure paths are real** — but the earlier claim of field support is withdrawn.

The one case that genuinely needs it already has it: a child that hits its turn cap produces a
salvaged summary and returns that. A structured result (files
touched, a digest, an exit state) is a bigger surface for a benefit nobody has asked for yet, and the
briefing can always ask for a particular shape in prose.

---

## 3. Presentation

### 3.1 The row

Each sub-agent owns one row in its orchestrator's transcript:

```
▸ worker 2 · refactor IndentShift    ⠹ 1m14s · 12.4k/208k
```

N workers, N rows, no interleaving. Useful before any drill-down exists.

- **Occupancy — solved.** `AgentContext.Used` holds the last turn's input tokens and `UsedFraction`
  divides it by the window, read straight off the sub-agent's own context.
- **Elapsed time — missing, and lands in STEP 1.** `InlineJobSink` shows duration only in terminal
  states; a running row says `"running…"`. Needs periodic `UpdateJob` ticks and a header-format
  change. A child is the first thing that runs long enough for the absence to matter.
- **A waiting-on-permission state has no substrate.** `JobState` (`Core/Models/Job.cs`) is
  Pending/Queued/Running/Paused/Succeeded/Failed/Cancelled/Skipped — no waiting member — and nothing
  reports that an agent is parked on approval. Without it, a blocked worker is indistinguishable from
  a slow one.

Row states wanted: running, waiting on permission, failed, complete.

### 3.2 The swap

Expanding a row replaces the chat view with that sub-agent's own transcript. Its own `IChatSink` and
`IJobPanel` are constructor parameters, and the agent emits kinds of text rather than styled text, so
a sub-agent's transcript is a different sink implementation — a UI composition problem, not a kernel
one.

### 3.3 Job rows belong to their own agent

A sub-agent's job rows — including its compression rows — render in **its** transcript, not its
orchestrator's. Stated explicitly because it is obviously right once written down and silently wrong
if nobody decides it: otherwise an orchestrator driving four workers fills its column with their
housekeeping.

---

## 4. Permissions — the real remaining work

**Same place, queued.** Prompts appear in the composer, one at a time; workers block until their
turn. `InteractivePermissionGate` already serialises on a `SemaphoreSlim(1, 1)`, awaits the prompt's
completion and restores the composer in a `finally`. A goal cancelled while queued resolves as a
deny rather than a hang. **This part works.**

Four things fan-out breaks:

1. **Attribution.** `PermissionRequest` is `(Kind, Display, AlwaysRule)` — no requester identity
   (`PermissionTypes.cs:25`). With N agents you cannot tell who is asking.

   **BOTH construction sites need threading — an earlier draft got this wrong.** It claimed MCP had
   shown identity could just be a field on the record, since `McpToolset` builds its own request
   (`McpToolset.cs:151`). But `McpToolset` is ONE SHARED INSTANCE per session, and
   `TryInvokeAsync(call, ct)` (`:140`) receives no requester identity — it has nothing to stamp. The
   plugin path is equally blocked: `PermissionGatedPlugin.ExecuteAsync` gets an `IJobContext`, and
   that interface exposes **no agent id** (`JobContext._agentId` is private).

   So: expose the id on `IJobContext`, and give the MCP path a per-call identity — a parameter, or a
   per-agent facade over the shared toolset. Not four signatures, but not one field either.

   This is load-bearing, not cosmetic: **prompts follow the composer, not the view.** You can be
   approving worker 3's write while looking at worker 1's transcript.

2. **The denial echo goes to the wrong transcript.** The gate writes `[red]denied: …` to the
   once-constructed `LatestChatSink` (`InteractivePermissionGate.cs:222`) — the main transcript,
   whichever worker asked.

3. **Shared policy mutation.** One worker's "Always" (`_store.Add`, `:198`) or "Trust this folder"
   (`SetTrust`, `:212`) instantly widens policy for every concurrent worker. The store is
   lock-protected, so this is a semantics decision rather than a race — but it is security-relevant
   and must be deliberate rather than inherited.

4. **`TrustQuestionControl` bypasses the semaphore.** It uses the prompt seam directly
   (`AppBootstrap.cs:570-572`, inside `AskTrustIfUnknownAsync`) without the gate's serialisation, so colliding with a worker prompt makes
   it silently no-op and leak an uncompleted await.

Queue order is arrival order.

---

## 5. The steps

Each step ends with something that WORKS and is driven live. Nothing in a later step is designed into
an earlier one. If a step fails, there is one candidate cause.

---

### STEP 1 — one child, foreground, sequential

Rewritten after a deep review found the previous version's telemetry claim rested on DEAD CODE and
its failure story missing entirely. Both are recorded below rather than quietly fixed.

#### 1a. Where the tool plugs in — DECIDED, not left open

An `ISubAgentSpawner` consulted in `InvokeAndShowAsync` **before** `WorkerToolset`, exactly as the MCP
branch already is (`Agent.cs:832-833`), returning null for names it does not own.

**Not a `WorkerTool` enum member.** `Agent.AllTools = Enum.GetValues<WorkerTool>()` (`Agent.cs:182`),
so an enum member auto-offers spawn to EVERY agent including children — which makes "depth limits
excluded" dishonest. A child constructed without a spawner structurally cannot nest, and that is what
makes the exclusion true rather than aspirational. It also avoids hand-writing a schema the class's
own doctrine forbids, and keeps `NamesFor` untouched.

Two things must be done together or a typo'd name reports "no such tool": add the name to
`alsoAvailable` (`Agent.cs:833`) AND its `ToolDefinition` to the request build (`Agent.cs:305`).

#### 1b. The failure contract — a correctness precondition, not a later concern

`InvokeAndShowAsync` has **no try/catch**, and the assistant message carrying the tool calls is
appended BEFORE they run (`Agent.cs:573-578`). An exception from a child unwinds the `foreach`
mid-turn, leaving tool calls with no matching results — the orphan the code itself documents
(`Agent.cs:615-618`) — and every later parent prompt 400s until compaction happens to rewrite the
head. It would present as a baffling, unrelated failure.

**The spawn branch catches everything except `OperationCanceledException`** and renders it as the
`state: error` envelope, upholding the never-throws contract `WorkerToolset.InvokeAsync` already
holds (`:246-249`). `McpToolset.TryInvokeAsync` has the same missing catch — a latent instance of this
bug worth fixing while here.

#### 1c. `state` — define it honestly or do not ship the field

`Agent.SendAsync` returns text on ALL THREE exits: normal, turn-cap salvage (`Agent.cs:376-381`) and
stuck detection (`:653-658`). Cap and stuck announce only through `_sink.ShowError`, which for a child
goes into the buffer — invisible to the tool.

So the derivable values are `completed | error | cancelled`, and **cap and stuck report as
`completed`**, carrying the salvage or last text. That is stated here rather than discovered later,
because a `state` field whose values are fiction is worse than no field. Adding `Agent.SendResult
{ Text, Outcome }` would fix it properly and is a real API change — deferred, and named as the cost.

#### 1d. The factory — the actual wiring, replacing a list that was wrong on its own terms

| Wire | Value | If omitted |
|---|---|---|
| provider | the parent's | — |
| plugins, `mcp` | the parent's (explicit yes for MCP) | child silently loses tools the parent has |
| ledger | **the parent's shared one** (D7) | spend and the breach warning are lost |
| `IChatSink` **and** `IJobPanel` | buffered, both | child rows leak into the parent's transcript (§3.3) |
| `logs` | yes | no child log directory — the only "inspectable afterwards" surface step 1 has |
| `maxTurns` | a real number, **never 0** | `AgentHost` maps 0 → `int.MaxValue`; `Agent` does NOT, so a child summarises at once and returns `""` — silently does nothing |
| `compressAbove` | `orchestrator.EffectiveCompressThreshold(window) ?? 40_000` (`AgentHost.cs:356`) | never compacts |
| context | `new AgentContext(contextWindow)` — `Window` is get-only, so it goes in at construction | no occupancy, `IsUnderPressure` always false, never compacts |
| `globalInstructionsDir` | yes | user-level CXAGENT.md ignored |
| briefing | the spawn `context` | — |
| the four callbacks | a per-child reporter | frozen row |
| **NOT** the session store | — | a persisted child is a permanently unfinished row that `OfferResumeAsync` offers as a crashed session next launch |
| **NOT** a spawner | — | nesting |

Dropped from the old list: *"cancellation token, id"* — neither is factory work. `Agent` has no ct
parameter (it arrives per `SendAsync`) and `Id` is minted internally and read-only.

**The factory is constructed in `AppBootstrap`**, because every value above is composition-root-only.
`Agent` and `AgentHost` each gain one optional parameter to receive it — two signature changes the
earlier version did not list.

#### 1e. Telemetry — and the blocker the previous version missed

**The renderer ignores `ProgressMessage`.** The old text cited `JobBlockControl.cs:76` as proof it was
already rendered; that control is **never placed in the grid** (`AppBootstrap.cs:434-441`: "jobs
render INLINE"). `InlineJobSink` reads `ProgressMessage` **nowhere**, and a Running row is the literal
`"running…"` (`:687`). Wiring the callbacks perfectly would still produce a frozen spinner.

So step 1 includes, as a first-class task: **`InlineJobSink` renders turns · occupancy · elapsed for
non-terminal states.** Plus a tick owned by the spawn branch and disposed in its `finally` —
`MainWindow._panelClock` cannot be borrowed, it refreshes nothing when the panel is hidden.

The row is the primary surface. **The right-panel section is demoted to "if cheap after the row
works"** — it needs a status record, a `Refresh` signature change, a `MainWindow` setter and a channel
from the factory's reporter, which is genuinely new plumbing across three layers.

#### 1f. Cancellation

Token flow verified end to end. But on Escape the OCE propagates out of the spawn branch, so the code
that closes the row (`Agent.cs:836-846`) never runs — the row spins forever while the transcript says
"Stopped." **`try/finally`: mark the Job `Cancelled` (the state exists), dispose the tick, keep the
buffer.**

#### 1g. The submission guard — a bug today that a long child makes certain

`SetSubmissionEnabled` is only ever called with `true` (`AppBootstrap.cs:215`); nothing disables it
during a turn. Press Enter while a child runs and a second `runner.SendAsync` starts a second loop on
the SAME `Agent` and the same live `Context.Messages`. Latent now; near-certain the moment a child
runs for minutes. One line of prevention, and it belongs here.

#### The rest of the scope

- the child runs **INLINE** on the existing continuation, not `Task.Run`
- the result is the envelope `{ id, state, text }` (D13), with the child's `Agent.Id`
- the child's id **addresses its row** (D14) — a closure over `job` and `_jobs` inside
  `InvokeAndShowAsync`, which is the only place both exist
- the (childId → agent, buffer, job) record needs a **session-lived owner**. Small, but honestly the
  first brick of background's registry, arriving here whether or not we name it
- "inspectable afterwards" means **the child's log directory**; the buffer is retained for step 3's
  transcript swap

Explicitly NOT in step 1: background, resumption, mid-run ask, model-initiated stop, spawn-gating,
depth limits, parallel, configured types, per-agent providers, a session aggregator.

**Done when:** a parent spawns one child in a live drive, the child does real work, its answer reaches
the parent's next turn, its row shows live turns/occupancy/elapsed, a child failure returns
`state: error` **without breaking the parent's next turn**, Escape leaves the row `Cancelled` rather
than spinning, and the child's log directory is readable.

**Headless before the drive** — most of this is testable with `MockLlmProvider`/`RecordingSink`:
parent context grows by exactly 2 messages; envelope id equals the child's `Agent.Id`; child failure
leaves the parent's turn intact; sink isolation (parent receives zero child tokens); no
`SqliteSessionStore` row under the child id; the child's tool list lacks `spawn_agent`; cancellation
marks the Job `Cancelled`. `InlineJobSink` has pure projection seams (`:511`, `:722`) so even the row
rendering is headless-testable.


### STEP 2 — named types

Types in config beside `mcp`: a name, a briefing, optionally a provider instance. The tool's schema
does NOT change — only what a type name resolves to.

Closes the provider gap: `ProviderRegistry` reaches neither `AgentHost` nor `PluginRegistry`, which
DISCARDS it (`PluginRegistry.cs:50`, `_ = providers;`).

Precedence becomes real here: the type's briefing outranks the parent's `context` (D9).

**Done when:** two types with different briefings produce visibly different child behaviour, and one
of them runs on a different configured model.

---

### STEP 3 — concurrency, and what it FORCES

Parallel spawning is not one change. It makes four currently-harmless things dangerous, and each must
land BEFORE the first two children run at once:

1. **Permission attribution** (§4.1, Q3). Prompts follow the composer, not the view: you can approve
   one child's write while looking at another's transcript. Both request-construction sites need
   per-call identity — `IJobContext` hides the agent id, and the shared `McpToolset` never receives
   one.
2. **The denial echo** (§4.2) goes to the main transcript whoever asked.
3. **A waiting-on-permission row state** (§3.1). Two children, one blocked: currently
   indistinguishable from slow.
4. **The shared-state audit** (below) — `McpClient.WriteAsync` has no lock on a shared stdio pipe.

Only then: `Task.Run` per child, sinks marshalling as they already do, and the `foreach` awaiting
several at once.

**Done when:** two children run simultaneously, each prompt names its requester, and the drive shows
no interleaved MCP corruption.

---

### Later, deliberately not now

- **Background spawning**, which is an architecture rather than a flag (§2) and FORCES Q2's envelope.
  It also reopens D3: once a child runs unattended, mid-run ask and model-initiated stop become
  necessary rather than declined.
- **Shared-policy semantics** (§4.3, Q6) and the trust-prompt bypass (§4.4).
- **Per-agent ledgers**, cost attribution, a session aggregator.
- **A per-agent working directory** (Q4) — impossible today; cwd is process-global.
- **Nesting.** opencode bounds it with a depth counter rather than a rule; we forbid it structurally
  until one level is proved.


### What the shared-state audit covers

WIDER than "the sinks", which is all an earlier draft listed. The four sink implementations were
checked and all marshal through `EnqueueOnUIThread`. What is NOT checked:
   - **`McpClient.WriteAsync` has no lock** (`McpClient.cs:341-346`): a bare `WriteLineAsync` plus
     `FlushAsync` on a shared stdio pipe. Two agents calling tools on the same server can interleave
     writes and corrupt a JSON-RPC frame. The read side is fine — replies multiplex by id.
   - **`SqliteSessionStore.SaveTurn`** runs inside `TurnCompleted` (`AgentHost.cs:381`), so it follows
     the loop onto the pool. Fresh connection per call and best-effort, so probably fine — "probably"
     is what an audit is for.
   - Verified safe, needing nothing: `TokenLedger` (`Interlocked` throughout), `LogFileManager`
     (per-path semaphores keyed by `agentId`), the static `WorkerToolset.Specs` (readonly).

---

## 6. Out of scope, and what is no longer

- **Nested spawning.** A sub-agent that can spawn is an orchestrator; nothing structurally prevents
  it, and nothing should encourage it until one fan-out level is proven.
- **Per-agent ledgers and cost attribution** — deferred deliberately (§1).

### Done since this was written

- **The kernel move.** Formerly listed here as "after sub-agents"; done now, because it turned out to
  be the cheaper order — see §1. The markup coupling it was blocking
  went with it, and so did the bug that coupling was hiding: body text reached the transcript
  unescaped while reasoning — the only path that built markup — was escaped, so the omission was
  invisible. Verified against `MarkupParser`: `we[red]ird` rendered as `weird`, and an unclosed tag
  recoloured everything after it. Both paths now escape in one place, and five tests cover it —
  including one asserting the UNESCAPED form is still swallowed, so the fix cannot quietly stop
  mattering.

- **The `AppBootstrap` extraction.** Done: 899 lines to 810. The estimate in the earlier draft was
  wrong — `WireRunner` is 92 lines, not 371. The real bulk was a 115-line `PreviewKeyPressed` lambda
  of which only the first ten had anything to do with a keystroke; the rest is now `SubmitComposer`,
  and `/mcp` is now `McpCommand`, a type taking the six pieces of state it used to capture. What is
  left in that file is what a composition root should be: what exists, and in what order.
