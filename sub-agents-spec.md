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
| D7 | **A ledger is GIVEN to an agent, not inherited by construction order.** Today the factory hands the child the parent's; when ledgers become per-model, the factory hands it one resolved by model and the call site does not change. Requires hoisting ledger creation out of `AgentHost` into `AppBootstrap`. | §1, §5.1d-i |
| D8 | A sub-agent's transcript is a **buffered sink**, inspectable on demand, not rendered into the parent's view. | §3 |
| D9 | **Three instruction channels, with precedence.** A type briefing (config, how to work) outranks spawn context (parent, what to know); the prompt (parent, what to do) is a user turn. | §2 |
| D10 | **Foreground first.** The tool blocks and returns the result. Background is a different tool — a registry, a notification route, a lifetime rule — and is a later step. | §2, §5 |
| D13 | **The result is an envelope from step 1: `id`, `state`, `text`.** An id retrofitted later must be threaded through every surface that already exists; adding it now costs a field. `Agent.Id` already exists. | §5 |
| D14 | **The child's id addresses its row.** Rows key on `job.Id`, minted per tool call; the spawn tool associates that with the child's `Agent.Id` so telemetry, and later background reporting and aggregation, all address the same row through one identifier. | §5 |
| D15 | **Dispatch is an `ISubAgentSpawner` consulted before `WorkerToolset`**, the MCP precedent — NOT a `WorkerTool` enum member, which would auto-offer spawn to every child and make the no-nesting exclusion false. | §5.1a |
| D16 | **`state` = `completed \| capped \| stuck \| error \| cancelled`.** Cap is structural (the reporter counts turns); stuck reads the buffered error's prefix. An earlier draft said this was invisible to the tool — it is not, and shipping `completed` for a capped run is what D13 exists to prevent. | §5.1c |
| D17 | **The spawn branch never throws** (except cancellation). An exception mid-`foreach` orphans tool calls in the parent's context and poisons every later turn. | §5.1b |
| D18a | **`/compress` is DECLINED while a turn is running**, not queued: it measures and rewrites a context that is actively changing, so running it later is a different operation and running it now corrupts the list. Not sub-agent-specific — a parent doing three tool calls is exposed today. | §5.0d |
| D18 | **Submitting during a turn QUEUES and APPENDS.** Several messages join newline-separated into one prompt at turn end; Escape stops the turn and moves the queue into the composer, above any text already there. Interrupt-and-rerun is deferred to when agents run in the background. | §5.1h |
| D20 | **`SendAsync` returns `SendResult { Text, Outcome }`** — `Completed \| Capped \| Stuck \| Failed \| Cancelled`. Three return sites; only four of ~73 call sites read the string. `state` must not rest on matching an error message's wording. | §5.1c |
| D21 | **A child gets MCP**, inherits the parent's `maxTurns`, and its registry entry is **never evicted in step 1**. | §5.1d |
| D24 | **A child gets a DIFFERENT system prompt**: the `# The user's commands` block dropped (it has no composer), and a sub-agent block added saying its final message is the whole answer and there is no follow-up. Everything else kept. One init-only property, fixed at construction. | §5.1h-i |
| D25 | **The parent's system prompt says NOTHING about spawning.** When to spawn — and especially when NOT to — belongs in the tool description, where opencode puts it: read at the moment of choosing, not paid for on every turn of every session. | §5.1h-i |
| D26 | **The parent's prompt gains THREE lines, and none is about when to spawn** — a child's report is a claim not a verification (the live-drive failure, through a layer `# Verifying` does not cover); the user cannot see its work; you are accountable for it. Appended to Verifying / Answering / Doing the work. Fixed text, unconditional, so no prefix churn. | §5.1h-i |
| D23 | **Spawning is NOT permission-gated in step 1.** opencode asks (`ctx.ask({ permission: "task" })`), but its children can spawn and run in background; ours is one foreground child using the parent's own gated tools, so every risky thing it does is already prompted — a spawn prompt would ask about the wrapper, not the risk. Revisit at step 3, where several unattended children change the answer. | §4 |
| D22 | **Nothing of the child renders live except its row.** Its reasoning and answer stay in the buffer until expanded. A five-minute child shows one line of numbers — chosen, not discovered. | §5.1e |
| D19 | **The spawn tool's `PluginType` is `llm_agent`**, a type the UI already understands at five sites — Worker author, no collapse on completion, stays expanded, own status, no output placeholdering. It was built for exactly this and is currently unused. | §5.1e-i |
| D11 | **Telemetry is in step 1**, not later — a child that reports nothing is a frozen row, and retrofitting it means revisiting the factory, the row and the panel. `Agent` already exposes `Id`, `Context` and four callbacks; `Job.ProgressMessage` already renders; `SessionPanel` already takes optional sections. Missing: elapsed time (a periodic tick), and the waiting state (step 3). | §2, §5 |
| D12 | **No general hook system.** A hook that can block IS the permission gate; one that cannot is telemetry, which the callbacks already give. Add named seams if a need appears. | §2 |

### Open — none of these block step 0 or step 1

| # | Question | Blocks |
|---|---|---|
| Q3 | Where does per-call requester identity come from? BOTH request-construction sites lack it — `IJobContext` hides the agent id, and the shared `McpToolset` never receives one. | Permission attribution (step 4) |
| Q4 | Does a sub-agent get its own working directory? It cannot today — cwd is process-global. | A worker in a worktree |
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
- *"Cap and stuck are invisible to the spawn tool."* **False** — the factory owns the buffered sink,
  and cap is derivable from the turn count with no string matching (§5.1c).
- *"Hand-writing a tool schema violates the class's doctrine."* **False** — `McpToolset` already does
  it; the doctrine is about plugin-schema drift, which a spawn tool has none of.
- *"Add a `SetSubmissionEnabled(false)` guard."* **False** — that flag is tested before command
  dispatch and would block `/exit` and `/clear` (§5.1g).
- *"The orphaned-context failure recovers when compaction rewrites the head."* **False** — it does not
  recover at all; the session is bricked until `/clear`.

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

### STEP 0 — three fixes worth making whether or not sub-agents ship

Found by planning against the code, not by planning sub-agents. Two are LIVE BUGS today; the third is
wanted by per-model ledgers independently. None of them needs a sub-agent to be worth doing, and all
three are preconditions of step 1 — so they come first and can be judged on their own.

**0a. The double-submit corruption.** Press Enter while a turn is running and `SubmitComposer`
(`AppBootstrap.cs:404-418`) starts a second `runner.SendAsync` on the SAME `Agent` — two `foreach`
loops appending to one live `Context.Messages`. Worse, `previousTurn?.Dispose()` (`:414`) disposes the
RUNNING turn's token, so the first loop throws `ObjectDisposedException` at its next cancellation
check rather than cancelling. Invisible today only because turns last seconds. Fix: queue-and-append
(§5.1h), which needs the turn to be trackable — it is fire-and-forget now.

**0b. The frozen row on a cancelled MCP call.** `InvokeAndShowAsync` (`Agent.cs:806-848`) is
straight-line with no `finally`, so an `OperationCanceledException` skips the code that closes the row.

**CORRECTED: it is NOT reproducible with `run_shell`**, which an earlier draft claimed as the
flagship evidence. `ProcessRunner` CATCHES the OCE, kills the tree and returns a result
(`ProcessRunner.cs:117-124`), and `WorkerToolset.InvokeAsync`'s `catch (Exception)` (`:246`) catches
OCE too — so built-in tools come back as strings and their rows close as **Failed**. That matches the
live drive: Escape during `sleep 400` gave exit code 137 and a finished row.

The path that genuinely escapes is `_mcp.TryInvokeAsync` (`Agent.cs:833`), which has **no catch at
all**. Cancel during a slow MCP tool call and the row spins for the rest of the session — nothing
sweeps it.

Stating this precisely matters: an implementer who tries to reproduce it with `run_shell`, fails, and
concludes the fix is unnecessary would drop it — or worse, "fix" `ProcessRunner` to rethrow and CREATE
the bug the spec describes.

Two changes, not one:
- `try/finally` in `InvokeAndShowAsync` marking the job `Cancelled` — this is also what makes the
  spawn branch safe, which is the real reason step 1 needs it
- `catch (OperationCanceledException) { throw; }` ahead of `WorkerToolset.cs:246`, or the `finally`
  guards a path nothing reaches. Today Escape becomes a tool result the model reasons about
  ("error: The operation was canceled") and keeps looping from.

**0d. `/compress` during a running turn — the same corruption as 0a, and 0a makes it MORE likely.**
`/compress` is dispatched at `AppBootstrap.cs:377`, inside the command block and therefore BEFORE any
guard at `:404` — deliberately, since §1g argues commands must keep working. But `CompressNowAsync`
calls `CompressionRun.RunAsync(Context, …)`, which replaces `Context.Messages` wholesale while the
agent's `foreach` is appending tool results to that same list. Best case the results are lost; likely
case is a torn `List<T>` and an `InvalidOperationException` mid-request.

Live today — and after 0a lands, `/compress` becomes one of the few things a user CAN press during a
long run, so the guard makes it more probable rather than less.

**Fix: decline it while a turn is running**, with a line saying why — *"a turn is running; press
Escape to stop it first."*

**Not only when a sub-agent is running.** The corruption needs no child: a parent doing three
`read_file` calls is exposed today. The condition is the same "is a turn running" predicate 0a needs
and the Escape handler already uses (`AppBootstrap.cs:481`) — ONE definition, three consumers.

**Declining rather than queueing, and this one is not arbitrary.** For an ordinary prompt, queueing is
right (§1h) because the text is still valid when the turn ends. `/compress` is different: it is a
measurement-and-rewrite of a context that is actively changing, so running it later is a different
operation from the one the user asked for, and running it now cannot work at all. There is also
nothing to lose — the automatic route already compresses on measured pressure, so declining costs the
user a keystroke, not a compaction.

**0c. Hoist the ledger.** `AgentHost` creates it in its own constructor (`:249`), which means it can
only ever be THE SESSION'S ONE LEDGER — the assumption per-model attribution has to break. Moving
creation to `AppBootstrap` makes "which ledger does this agent get?" a question with an answer, and
hands the composition root the `_contextWindow` and `OrchestratorSettings` that a factory would
otherwise have no way to reach.

**Done when:** a second Enter during a turn queues instead of corrupting; Escape during `run_shell`
leaves a `Cancelled` row; the suite is green and a live drive shows both.

---


### STEP 1 — one child, foreground, sequential (needs step 0)

**What this is FOR, stated plainly so it is not judged as a feature.** One foreground blocking child
buys a user very little that a second session does not already give them — and the "second context"
benefit is smaller than it sounds: the parent blocks for the whole run and then absorbs the child's
full answer as a tool result, so what it actually saves is only the child's INTERMEDIATE tool
results. Real on "read 40 files and tell me X"; near-nil otherwise.

Step 1 is **seam validation for step 3**, plus two independent bug fixes on the way. Everything it
touches — the spawner, the buffered sink pair, the factory, the envelope, the per-child reporter — is
load-bearing for concurrency and is currently ASSERTED to work rather than exercised. Three review
passes found roughly a dozen errors in this document, several in the fixes for earlier ones; the only
thing that reliably kills that class of error is running code.

**If step 3 is not going to be built, step 1 is not worth it.** Do step 0 and stop.

Rewritten after a deep review found the previous version's telemetry claim rested on DEAD CODE and
its failure story missing entirely. Both are recorded below rather than quietly fixed.

#### 1a. Where the tool plugs in — DECIDED, not left open

An `ISubAgentSpawner` consulted in `InvokeAndShowAsync` **before** `WorkerToolset`, exactly as the MCP
branch already is (`Agent.cs:832-833`), returning null for names it does not own.

**Not a `WorkerTool` enum member.** `Agent.AllTools = Enum.GetValues<WorkerTool>()` (`Agent.cs:182`),
so an enum member auto-offers spawn to EVERY agent including children — which makes "depth limits
excluded" dishonest. A child constructed without a spawner structurally cannot nest, and that is what
makes the exclusion true rather than aspirational. **The schema is hand-built, and that is normal here.** `ToolDefinition` is
`(Name, Description, JsonElement InputSchema)` and `McpToolset.Definitions()` already constructs them
directly (`McpToolset.cs:127`). `WorkerToolset.BuildDefinition` exists to stop a tool's advertised
params drifting from a plugin's `JobSchema`; a spawn tool has neither, so the doctrine does not apply.
An earlier draft called this a hazard — it is not, and the warning would have sent an implementer
hunting for a generator that cannot exist.

**Two required sites:** the dispatch arm (`Agent.cs:832`) and the `ToolDefinition` in the request
build (`Agent.cs:305`). Adding the name to `alsoAvailable` (`:833`) is a third, cosmetic one — it
only feeds the "no such tool. Available:" string (`WorkerToolset.cs:182-184`), nothing else.

#### 1b. The failure contract — a correctness precondition, not a later concern

`InvokeAndShowAsync` has **no try/catch**, and the assistant message carrying the tool calls is
appended BEFORE they run (`Agent.cs:573-578`). An exception from a child unwinds the `foreach`
mid-turn, leaving tool calls with no matching results — the orphan the code itself documents
(`Agent.cs:615-618`). **The session is then permanently bricked** — worse than the earlier draft said.
An orphan 400 is not a length error, so `ContextOverflow.IsOverflow` does not match and the recovery
path at `Agent.cs:410` never runs; compaction only fires on measured pressure, which a small
orphaned context never reaches. Every later prompt shows the provider's 400 and does nothing, with no
automatic recovery — only `/clear`. And it presents on the turn AFTER the failure, which is what makes
it hard to diagnose.

**The spawn branch catches everything except `OperationCanceledException`** and renders it as the
`state: error` envelope, upholding the never-throws contract `WorkerToolset.InvokeAsync` already
holds (`:246-249`). `McpToolset.TryInvokeAsync` has the same missing catch — a latent instance of this
bug worth fixing while here.

#### 1c. `state` — define it honestly or do not ship the field

`Agent.SendAsync` returns text on ALL THREE exits: normal, turn-cap salvage (`Agent.cs:376-381`) and
stuck detection (`:653-658`). Cap and stuck announce only through `_sink.ShowError`, which for a child
goes into the buffer — invisible to the tool.

**Correcting an earlier draft: this information is NOT invisible.** The factory constructs the
buffered sink, so the spawn branch holds it and can read what `ShowError` captured. Better, cap is
structural: the per-child reporter already counts `TurnCompleted`, so `turns >= maxTurns` IS the cap,
with no string matching.

**But cap is NOT derivable from the turn count alone**, as an earlier draft claimed. `_maxTurns` is
private with no accessor, so the reporter cannot read it — the FACTORY knows the value it passed and
must hand it over. Worse, the counter reads exactly `maxTurns` both when the cap fires AND when a run
finishes naturally on its last turn, so `count >= maxTurns` gives a false `capped`. The salvage turn
does not raise `TurnCompleted` either.

**DECIDED: `Agent.SendAsync` returns `SendResult { string Text, SendOutcome Outcome }`.** `state` is
the one field the parent's model acts on, and it must not rest on matching the wording of a
human-facing error message.

Cost, measured rather than guessed: three `return` sites (`Agent.cs:390`, `:576`, `:667`), and of ~73
call sites only **four** actually read the returned string — the rest await and discard. So the churn
is small and mechanical.

`SendOutcome`: `Completed | Capped | Stuck | Failed | Cancelled`. The kernel knows all five at the
point it returns; nothing downstream has to infer them.

What must NOT happen is shipping `completed` for a run that hit its cap — the parent then acts on a
salvage summary as though it were a finished answer, which is exactly what D13 exists to prevent.

#### 1d. The factory — the actual wiring, replacing a list that was wrong on its own terms

| Wire | Value | If omitted |
|---|---|---|
| provider | the parent's | — |
| plugins, `mcp` | the parent's — **MCP yes, decided** | a search-type child that cannot reach the docs server is crippled for the obvious use case. It is also the first thing that makes step 3's shared-state audit non-optional: `McpClient.WriteAsync` is unlocked, which is fine while sequential and not after |
| ledger | **the parent's shared one** (D7) | spend and the breach warning are lost |
| `IChatSink` **and** `IJobPanel` | buffered, both | child rows leak into the parent's transcript (§3.3) |
| `logs` | yes | no child log directory — the only "inspectable afterwards" surface step 1 has |
| `maxTurns` | **inherit the parent's ceiling** (500 today); 0 now means unbounded | FIXED IN THE AGENT rather than left as a factory rule: `_maxTurns = maxTurns <= 0 ? int.MaxValue : maxTurns`. It used to fire the cap on iteration ZERO, making a real paid provider call and returning a plausible summary of a run that never happened. **Inherited rather than given a smaller number of its own**: a figure invented here is the same mistake as the old `MaxWorkerTurns: 10`, which capped mid-work and returned a salvage summary the caller read as an answer. |
| `compressAbove` | `_orchestrator.EffectiveCompressThreshold(_contextWindow) ?? OrchestratorSettings.DefaultCompressThreshold` (`AgentHost.cs:356`) — **the constant, never the literal 40000**, or it desynchronises | never compacts |
| context | `new AgentContext(contextWindow)` — `Window` is get-only, so it goes in at construction | no occupancy, `IsUnderPressure` always false, never compacts |
| `globalInstructionsDir` | yes | user-level CXAGENT.md ignored |
| briefing | the spawn `context` | — |
| the four callbacks | a per-child reporter | frozen row |
| **NOT** the session store | — | a persisted child is a permanently unfinished row that `OfferResumeAsync` offers as a crashed session next launch |
| **NOT** a spawner | — | nesting |

Dropped from the old list: *"cancellation token, id"* — neither is factory work. `Agent` has no ct
parameter (it arrives per `SendAsync`) and `Id` is minted internally and read-only.

#### 1d-i. The construction-order blocker — the hoist is STEP 0c

**`AgentHost` CREATES the ledger in its own constructor** (`AgentHost.cs:249`), before `BuildAgent()`.
So a factory built in `AppBootstrap` and passed INTO `AgentHost` cannot close over the parent's
ledger — it does not exist yet.

**Ledger creation moves into `WireRunner`, immediately above the `new AgentHost(...)` call** — not to
the top of `AppBootstrap`. The distinction matters: `WireRunner` re-runs on every F5 provider change,
and today that RESETS the ledger to zero. Hoisting to the top would make it survive the rewire, which
sounds better but changes user-visible behaviour and breaks two things quietly — `Breached` fires once
per ledger, so a surviving one can never warn again; and the budget comes from the NEW provider's
settings, which a surviving ledger never adopts. Constructing it in `WireRunner` preserves today's
semantics exactly and still satisfies D7, because the composition root owns it and can hand a
different one to a factory.

`Breached` stays subscribed in `AgentHost` (`:257`) — it owns the sink, and moving it out would mean
re-subscribing on every rewire against a ledger that outlives the host, accumulating handlers.

`AgentHost` gains a defaulted `TokenLedger? ledger = null` parameter, so the ~10 test construction
sites are untouched. Not for tidiness: a ledger created
inside `AgentHost`'s constructor can only ever be THE SESSION'S ONE LEDGER, and that is precisely the
assumption per-model attribution has to break. Owning it at the composition root is what makes "which
ledger does this agent get?" a question with an answer. `Func<TokenLedger>` would also unblock the
ordering, but it defers the lifetime question rather than answering it.

The same move solves two more gaps: `_contextWindow` and the `OrchestratorSettings` are both private
on `AgentHost` and both already available in `AppBootstrap` at composition time.

**One consequence to decide WITH per-model, not now:** `TokenLedger.Breached` fires once ever and the
status bar reads one total. With several ledgers, "the session total" becomes a sum and the breach
becomes "which budget?".

**Signature changes: three, not one.** `Agent` gains the spawner parameter (it must be a field —
`InvokeAndShowAsync` is an instance method), `AgentHost` gains it too, AND `BuildAgent()` must forward
it.

**The child must be built directly as an `Agent`, never via `AgentHost`.** That single rule is what
makes two rows above true at once: no store row (only `AgentHost`'s `TurnCompleted` calls
`SaveTurn`), and the four callbacks free for the child's reporter.

#### 1e. Telemetry — and the blocker the previous version missed

**The renderer ignores `ProgressMessage`.** The old text cited `JobBlockControl.cs:76` as proof it was
already rendered; that control is **never placed in the grid** (`AppBootstrap.cs:434-441`: "jobs
render INLINE"). `InlineJobSink` reads `ProgressMessage` **nowhere**, and a Running row is the literal
`"running…"` (`:687`). Wiring the callbacks perfectly would still produce a frozen spinner.

**And it is `CompactHeader`, not `StatusText`.** A running row always takes the compact branch
(`InlineJobSink.cs:134`) which calls `ClearStatus` (`:145`) — so `StatusText`'s `"running…"` is never
on screen. Someone changing `StatusText` would test it through its seam, see nothing, and lose an
afternoon. The change goes in `CompactHeader`'s non-terminal branch (`:534`), escaping the text as
`name` already is.

**The tick must NOT go through `UpdateJob`.** That method force-expands the row (`SetExpanded(id,
true)`, `:169`) and blanks its body (`UpdateMessage(id, "")`, `:211`) on every call — once a second,
for minutes, a row that re-opens under a user who collapsed it and erases anything step 3 might stream
into it. Add a header-only path instead: `UpdateProgress(job)` → store and `SetHeader`. Roughly eight
lines, no side effects, and `UpdateJob` stays for real transitions.

The tick is owned by the spawn branch and disposed in its `finally` — `MainWindow._panelClock` cannot
be borrowed, it refreshes nothing when the panel is hidden.

**Nothing else of the child renders live.** Its reasoning and its answer stay in the buffer until
someone expands the row (step 3's swap). Interleaving a child's stream into the parent's transcript is
what makes fan-out illegible, and the row plus the tick is what stops it looking frozen. The
consequence, chosen rather than discovered: **a five-minute child shows one line of numbers.**

The row is the primary surface. **The right-panel section is demoted to "if cheap after the row
works"** — it needs a status record, a `Refresh` signature change, a `MainWindow` setter and a channel
from the factory's reporter, which is genuinely new plumbing across three layers.

#### 1e-i. The row must be a WORKER, or it hides the answer

`ToolPluginType` (`Agent.cs:1174-1183`) maps unknown names to `"file"`, so `spawn_agent` would be
labelled a file operation — and worse, `InlineJobSink.IsCompactRow` treats anything that is not
`"llm_agent"` as compact, so **the row collapses the moment the child finishes**, hiding the answer
behind an `expand…`. The sink's own comment says this is deliberate for tools and wrong for workers:
*"COLLAPSING it at the finish line snatches away the thing the user was reading."*

One line: `"spawn_agent" => "llm_agent"` in that switch — and it is not a workaround. **`llm_agent`
is already a first-class type in the UI**, tested at five sites: `AuthorFor` (`:463`) gives the row a
**Worker** author instead of "Tool"; `IsCompactRow` (`:757`) keeps it out of the compact branch;
`keepOpen` (`:270`) leaves it EXPANDED when it finishes; `StatusText` (`:596`) returns null so the
worker's own content shows; and `JobDigest` (`:142`) does not placeholder-out its bulk output. The
concept was built for exactly this and is currently unused, so a spawn tool is not adopting a label —
it is the thing the label was for.

#### 1f. Cancellation — the `try/finally` is STEP 0b

Token flow verified end to end. But on Escape the OCE propagates out of the spawn branch, so the code
that closes the row (`Agent.cs:836-846`) never runs — the row spins forever while the transcript says
"Stopped." **ONE construct, not two.** §1b's catch and this `finally` are the same `try` around the spawn
dispatch INSIDE `InvokeAndShowAsync` — written as two separate wrappings, the likely outcome is a
catch that swallows the error but leaves the row `Running`, or a `finally` that closes the row while
the exception still escapes the `foreach` and bricks the session (§1b):

```csharp
try            { … await child.SendAsync(…) … }
catch (OperationCanceledException) { job.State = JobState.Cancelled; throw; }
catch (Exception ex)               { return ErrorEnvelope(ex); }
finally        { tick.Dispose(); }
```

The `try/finally` also fixes this for EVERY tool call, not just spawn — a cancelled `run_shell` leaves
the same frozen row today.

#### 1g. The submission guard — MOVED TO STEP 0a, kept here for context

`SetSubmissionEnabled` is only ever called with `true` (`AppBootstrap.cs:215`); nothing disables it
during a turn. Press Enter while a child runs and a second `runner.SendAsync` starts a second loop on
the SAME `Agent` and the same live `Context.Messages`.

**And it is worse than two loops:** the second submit does
`Interlocked.Exchange(ref turnCts, …); previousTurn?.Dispose()` (`:412-414`), **disposing the token
the first run is still using** — so the first loop throws `ObjectDisposedException` on its next
cancellation check.

**`SetSubmissionEnabled(false)` is the WRONG lever**, and an earlier draft proposed it. That flag is
tested at `SubmitComposer`'s FIRST line (`:327`), before command dispatch — so it would also disable
`/exit`, `/clear`, `/mcp`, `/help` and `/compress`. The user could not quit while a child ran, and the
composer would claim "no provider", which is a lie.

**Guard the model dispatch only**, immediately before `:404`, reusing the predicate the Escape handler
already trusts (`:481`) so "a turn is running" has one definition:

```csharp
if (turnCts is { IsCancellationRequested: false })
{
    // commands still work — /exit and /clear are exactly what someone wants here
    return;   // with a system line saying Escape stops the run
}
```

**Not one line: a guard plus a lifetime.** `turnCts` is never nulled on completion, so the predicate
latches true after the first turn and would block everything. The fire-and-forget `SendAsync` needs a
continuation that clears it.

**And a guard needs a BEHAVIOUR** — rejecting the keystroke is not enough. See §1h.

#### 1h-i. The two system prompts — MISSED BY ALL FOUR REVIEW PASSES

A child would today receive **the session system prompt unchanged**, which is wrong in a specific way
and silent about the thing that matters most.

**What is wrong for a child:** the `# The user's commands` block (`SystemPrompt.cs:133-141`) tells it
about `/help`, `/clear`, `/compress`, `/mcp`, `/exit`. **A child has no user and no composer.** It is
being told to suggest commands to someone who will never see them. The "Answering" guidance is aimed
at a human reader too, where a child's reader is the parent's MODEL.

**What is missing:** nothing tells it that it IS a sub-agent — that its final message is the entire
answer, that nobody will ask a follow-up, and that offering next steps is therefore useless. A child
that thinks it is in a conversation writes *"let me know if you'd like me to check the other files"*,
and the parent receives a question nobody can answer.

**The child's prompt:**
- **drop** the commands section — the one block that is actively wrong
- **add** a sub-agent block, in the same position and spirit as the briefing: *your final message is
  the whole of what your caller receives; there will be no follow-up; do not ask questions or offer
  next steps*
- **keep** everything else: `<env>`, conventions, verifying, project instructions, MCP instructions —
  all as relevant to a child as to a parent

Mechanically this is one more init-only property on `SystemPromptContext`, exactly how
`McpInstructions` was added, so the cache rules are unaffected: it is fixed at construction. **Two
prompt prefixes per session rather than one** — correct, since they are different agents, and worth
saying so it is not later mistaken for cache churn.

**The parent's prompt: nothing.** When to spawn belongs in the TOOL DESCRIPTION, which is where
opencode puts it (`task.txt`) — a tool the model can see and a description it reads at the moment of
choosing. Putting it in the system prompt would spend prefix on every turn of every session, including
the ones with no spawning in them, and would describe a capability the model can already see in its
tool list.

What the description must carry, following opencode's shape:
- **when NOT to use it** — reading a known file, grepping a known symbol, anything in 2-3 files. This
  is most of their text, and it is the part that stops a model delegating work it should just do
- **that the result is not visible to the user**, so the parent must report it
- **that the prompt should say exactly what to return**, since the briefing is what shapes the answer
  (D9) and there is no follow-up

**BUT "NOTHING" IS TOO STRONG — AND D25 STILL STANDS.** D25 is about *spawn guidance*, and that
belongs in the description. What the parent's prompt is missing is different: **three lines that are
correct for a lone agent and become WRONG the moment it has a child.** None of them says when to
spawn. They are a parent's obligations towards work it did not do itself, they cost three sentences,
and each has a named failure without it.

**D26. Three lines, appended to the sections that already own the topic — no new heading.**

- **Under `# Verifying`** — *"A sub-agent's report is a claim, not a verification. If it says the
  build passes, the build has not been verified until you have seen the output yourself."*

  Without this the entire `# Verifying` section is DEAD on the delegated path. Every rule in it — "a
  command that exits 0 has not verified anything", "confirm the count", "confirm your filter matched"
  — is written about output *the model read*. A child's summary is neither the output nor the model's
  own reading of it, so a parent can satisfy every line of that section while verifying nothing.
  **This is the live-drive failure exactly** — the one the section was written to fix
  (`SystemPrompt.cs:114`, reporting a pass over a file that did not compile) — reintroduced through a
  layer the text does not reach. Most valuable of the three, and the one this codebase has already
  paid for once.

- **Under `# Answering`** — *"The user cannot see a sub-agent's work. Anything from one that they
  need is only in your reply."*

  The tool description says this too, and the duplication is deliberate: the description is read when
  CHOOSING to spawn, this applies when ANSWERING, often many turns and several tool calls later.
  Different moment, different section. D22 is what makes it true — nothing of the child renders but
  its row — so the parent is the only channel there is.

- **Under `# Doing the work`** — *"You are accountable for a sub-agent's work as if it were your own.
  If its report is thin or does not answer what you asked, say so or check yourself — do not pass it
  on as fact."*

  Stops the parent laundering a child's uncertainty into its own confident prose. Without it a hedged
  child answer becomes a flat parental assertion, and the user cannot tell which part of the reply
  nobody actually stands behind.

**Rejected for the parent's prompt, deliberately:** how to phrase a briefing (tool description, and
D9's schema), how many to run at once (step 1 is one), and anything naming the tool — the model reads
its own tool list, and a prompt that names a tool goes stale when the tool is renamed.

**Cache cost, stated.** Fixed text, no runtime input: it costs prefix length once and never churns.
**Appended UNCONDITIONALLY**, not gated on "has spawned" — for the same reason `/mcp` is listed for
users with no servers (`SystemPrompt.cs:135-138`). Gating would remove the guidance from the exact
turn that needs it, the FIRST spawn, and would change the prefix mid-session, which is the churn this
whole design exists to avoid.

#### 1h. Submitting while a turn runs — QUEUE AND APPEND

The double-submit bug (§1g) needs a behaviour, not just a guard. Decided:

**A message typed during a running turn is QUEUED, shown with a `[queued]` title, and submitted as
one prompt when the turn ends.** Several queued messages are APPENDED, newline-separated, into a
single prompt — not replaced, because two messages are usually one thought completed (a correction and
its qualifier), and replacing would silently discard half of what someone said with no way to tell
which half survived. Claude Code behaves this way, and it is the reason a mid-turn correction there
does not get lost.

Newline between them rather than a space: they were separate thoughts, and the break is structure a
model reads.

**ESCAPE STOPS THE TURN AND MOVES THE QUEUE INTO THE COMPOSER** — it does not discard it. The text was
never sent, so cancelling a run must not eat what someone typed. If the composer already holds text,
the queued messages go ABOVE it, preserving the order they were written in. The user then sees exactly
what they said, editable, and decides.

**Why queue rather than interrupt.** Interrupt-and-rerun — cancel the turn, fold the new message in,
start again — is the better long-term behaviour and is what Claude Code does. It is deferred because
it needs the turn to unwind cleanly mid-tool-call, which means the §1f orphan guard AND a way to await
a cancellation that is currently fire-and-forget. It becomes worth building when agents run in the
background and long tool calls are routine. Queue-and-append is the small correct version of the same
intent, and Escape-then-type already covers redirection today.

**What it needs, and it is genuinely small:**

- the running turn must be TRACKABLE — `_ = runner.SendAsync(...)` (`AppBootstrap.cs:418`) is
  fire-and-forget, so nothing knows when a turn ends
- a `List<string>` for the queue, joined and cleared on submit
- the `[queued]` title on the rendered message
- Escape moves the queue into the composer, above any existing text

**What it does NOT need, which is why it is preferred:** no cancellation mid-flight, so no orphaned
tool calls, no unwinding, and `previousTurn?.Dispose()` is never called on a live token — the three
hazards §1g identified all disappear rather than being handled.

#### The rest of the scope

- the child runs **INLINE** on the existing continuation, not `Task.Run`
- the result is the envelope `{ id, state, text }` (D13), with the child's `Agent.Id`
- the child's id **addresses its row** (D14) — a closure over `job` and `_jobs` inside
  `InvokeAndShowAsync`, which is the only place both exist
- the (childId → agent, buffer, job) record has a **session-lived owner** and, **decided, NO eviction
  in step 1**: entries live for the session. Inspection after the fact is a stated done-when, and one
  child's context is nothing. Stated as a decision rather than left as an omission, because a registry
  with no removal RULE is the wrong first brick for background — where eviction becomes real.
- "inspectable afterwards" means **the child's log directory**; the buffer is retained for step 3's
  transcript swap
- **the four callbacks are now EVENTS** (`Agent.cs:122-140`). They were settable `Action<T>`
  properties, where `TurnCompleted = x` then `= y` lost x with no warning — and a child's telemetry
  reporter plus a session aggregator are exactly two consumers of one signal. `AgentHost` subscribes
  with `+=` now instead of assigning in an object initialiser
- the envelope's `text` needs **no further stripping** — `SendAsync` already returns
  `ModelOutput.StripReasoning(...)` on every exit
- `ChatMessageId`s cannot collide: each sink mints its own and nothing compares them across sinks
- **the shared ledger's `Breached` fires into the PARENT's transcript** (`AgentHost.cs`), and only
  once ever. A child consuming the budget means the parent never hears about its own later overspend.
  Arguably right — it is the session's budget — but decide it rather than inherit it

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
