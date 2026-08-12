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
| D24 | **A child gets a DIFFERENT system prompt**: `# The user's commands` DROPPED (it has no composer) and `# Answering` REPLACED (its text addresses a human at a terminal; a child's reader is a model). Everything else kept. One init-only property, fixed at construction. Text verbatim in §5.1h-i. **One child type in phase 1 — per-role prompts are step 2**, a lookup where the constant sits. | §5.1h-i |
| D25 | **The parent's system prompt says NOTHING about spawning.** When to spawn — and especially when NOT to — belongs in the tool description, where opencode puts it: read at the moment of choosing, not paid for on every turn of every session. | §5.1h-i |
| D32 | **Never reorder tool calls; defer the await instead.** One pass in EMITTED order — a spawn is started and not awaited, everything else runs inline, the spawn group is awaited at the end. An earlier draft partitioned and hoisted spawns to the front, which moves a child ahead of a `run_shell` meant to precede it: the child works against a tree that command had not yet changed, and nothing reports an error. Start-and-defer gets identical overlap in all 7 observed mixed turns without moving anything. | §5 STEP 3 |
| D31 | **A cancelled turn must append a result for EVERY call, including ones never started.** Escape during a sub-agent run leaves the assistant message (appended at `Agent.cs:766`, before the loop) carrying a tool call with no `tool` result — in the LIVE context, since `messages` is `_context.Messages`. That is the §1b orphan: the next request 400s, `IsOverflow` does not match, and only `/clear` recovers. A live bug today, reachable through spawns and MCP; step 3 makes it the normal outcome of every Escape. Proven by a failing test before the fix. | §5 STEP 3 prereq 0 |
| D30 | **`capped` is an outcome in its own right, not a failure and not a success.** A child that exhausts its turn cap ran out of room; that is a fact about its briefing, not about the work. The envelope already carried the word, so the row and the usage history read it back rather than deriving a two-way failed/completed guess. Filing it under "completed" hides the one run worth finding later. | §3.1, §5 STEP 2.5 |
| D29 | **Audit every prompt line against the four combinations (single/fan-out x parent/child) BEFORE step 3 adds more.** They were written one finding at a time and never read together; one of D26's three lines was ungated and reaching CHILDREN until the mode work caught it by accident. Deliverable is a table — line, home, gate, evidence it is earned. | §5 STEP 2.5 |
| D28 | **The parent must be TOLD several agents can run at once, and the sentence forbidding it DELETED** — one line in the system prompt (gated on CanSpawn), the mechanism plus "independent work" in the tool description, and the removal of *"It runs once… returns one message"* (`SubAgentSpawner.cs:58`), which is true today and false after step 3. Adding without deleting leaves two contradicting instructions, the older and more specific of which is the one a reader believes. Ships WITH the partition — either half alone is untestable. | §5 STEP 3 |
| D27 | **Concurrency is a BARRIER, not background.** N children in one assistant message, all resolved before the parent's loop resumes. Forced by the message format — an unmatched tool call 400s the session — and it is what both references ship. It also dodges the unsolved problem: with a barrier the user is still watching, so a child's permission prompt has someone to answer it. | §5 STEP 3 |
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
**a waiting-on-permission state** (§3.1). **Elapsed time shipped in step 1** — a per-call timer,
because turn boundaries alone leave a row frozen while a child sits inside one turn. Only the
permission state is still outstanding, and it is a step 3 prerequisite.

**A session aggregator is then a subscriber, not new plumbing** — and that is how it was built. The
parent subscribes to a child's `ToolCallFinished` and forwards it keeping the CHILD's agent id;
`TokenLedger` gained `SubAgentTokens` so the panel can say what workers spent against what this agent
spent. Both arrived for one child at a time and are already what N children need.

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

## 3. Presentation — **BUILT, and step 3 needs almost nothing from it**

### 3.1 The row

Each sub-agent owns one row in its orchestrator's transcript. What was sketched here:

```
▸ worker 2 · refactor IndentShift    ⠹ 1m14s · 12.4k/208k
```

What ships — collapsed while running, and expandable to standing facts above the child's recent
calls:

```
▸ ✔ Worker  explore · Analyze the repo structure  ·  done · 102.6s
```

**N WORKERS, N ROWS, AND NOTHING COORDINATES THEM.** That was an aspiration when this was written and
is now a property of the code: every path into `InlineJobSink` keys on `job.Id` and marshals through
`EnqueueOnUIThread`, so rows are independent transcript messages with no shared layout state. Three
concurrent children need no new presentation — which is why step 3 is a loop-and-locks step rather
than a UI one.

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

**WHERE THIS LANDED.** Elapsed time shipped in step 1 as planned, and the row grew further from
driving it: the header names the TYPE (it was truncated JSON, so three spawns looked alike), carries
live turns and occupancy while running, and switches to `done · 2m10s · 41,203 tokens` when finished
rather than freezing on its last tick — a stopped counter reads as a hang.

**A FIFTH STATE APPEARED THAT NOBODY LISTED: `capped`.** A child that exhausts its turn cap did not
fail and did not complete. Seen live, and the envelope already carried the word — `SendOutcome.Capped`
renders as `state="capped"` — so the row and the history read it back rather than guessing from a
`failed` boolean. Recording it as "completed" would file a wasted run under success, which is exactly
the run worth finding later.

**Waiting-on-permission is still missing**, and remains a step 3 prerequisite: with one child at a
time a blocked worker is merely slow, which is why it never bit.

### 3.2 The swap — **SUPERSEDED, and the replacement is better**

The plan was: expanding a row replaces the chat view with that sub-agent's own transcript. Still
possible — its `IChatSink` and `IJobPanel` are constructor parameters — but it was never built,
because driving the thing showed a cheaper answer to the question it was solving.

**WHAT SHIPPED INSTEAD: the row expands in place.** While running it shows standing facts above the
child's recent calls; when finished it shows the account:

```
done · 2m10s · 41,203 tokens

  type: planner
  model: qwen3.6-35b-a3b-ud-iq4_xs
  task: You are planning a new GPU backend plugin for cxgpu…
  7 turns · 2m10s
  tokens: 41,203  ↑38,900 ↓2,303
```

Three things this gets that a swap does not. **You do not leave the conversation** — the question a
running child provokes is "is it on the right track", which is answered beside the work rather than
in a different view you must navigate back from. **It survives** — a swap shows a live transcript,
this is a record that is still there tomorrow. And **the model line earns its place** precisely
because a type may name its own provider: a worker running somewhere other than the session's model
is otherwise invisible, and once the row finishes the provider is unreachable.

The swap is not ruled out — a forty-turn child has a scrollback the row deliberately truncates to the
last six calls. But it is now a convenience rather than the only way to see inside a child, which is
a different and much weaker case for building it.

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

**STATUS: steps 0, 1 and 2 are BUILT and driven. Step 3 is not.** Each section below keeps the
reasoning it was planned with AND records where the plan was wrong — the corrections are the useful
part, and deleting them would leave a document that looks like it predicted everything.

Beyond the numbered steps, and not planned here: **AgentMode** (`/mode single|fan-out`, `--mode`,
fan-out by default), per-model spend attribution, and the config `agents` block. Those came out of
using the thing.

**AND A SECOND ROUND OF THE SAME**, all of it found by driving real work rather than by planning:

| What | Why it was invisible until a real run |
|---|---|
| A worker's spend, live | The ledger is shared, and children usually run on the PARENT'S model — so the per-model breakdown had one row and suppressed itself. A whole fan-out run showed no attribution at all. |
| The spawn row's type | `DescribeCall` truncated raw JSON at 60 chars, and `type` serialises LAST, so the one field naming the worker was always the field cut. Three spawns rendered as three identical rows. |
| A child's logs | Written to a TOP-LEVEL directory keyed by the child's own id — indistinguishable from a session. `ls -t` returned a CHILD as the newest "session", which cost a wrong diagnosis before it was noticed. |
| The parent's own spend | The status bar showed `Ledger.TotalTokens` beside an occupancy figure that IS the parent's, so a fan-out session read as one agent costing four times what it did. |
| `/stats` | None of the above survived the process. What a run cost, which type earns its keep, what fills a context — all measured every turn, all discarded. |

The pattern is worth naming because it will repeat in step 3: **every one of these was correct in
single-agent mode and wrong the moment a second agent existed.** None had a failing test, because
each was a reasonable reading of a one-agent world. Concurrency is a third such world, and the same
class of bug is waiting in it.

Each step ends with something that WORKS and is driven live. Nothing in a later step is designed into
an earlier one. If a step fails, there is one candidate cause.

---

### What driving the prompts actually measured — on ONE model

Four interventions, one result. Recorded because the ratio is the finding, and because a later reader
will otherwise assume every line in these prompts earned its place.

**Read the whole section as scoped to `qwen3.6-35b-a3b` at iq4_xs, local.** That is the only model
these numbers come from, cxagent runs whatever a user configures, and none of it generalises.

| # | Change | Result |
|---|---|---|
| 1 | Sharpened the tool description: stated the BENEFIT ("keep the conclusion, not the file dumps"), changed the test from tool-call count to "do you already know the file", added "do not also do it yourself" | **no change** — 19 tool calls, 0 spawns on an open-ended question |
| 2 | Added a delegation RULE to the fan-out system prompt, phrased about where the reading lands | **no change** — 37 tool calls, 0 spawns, 212k chars burned |
| 3 | Added four `<example>` blocks — three positives and a counter-example — in opencode's shape | **WORKED.** 26 read_file → 0, 0 spawns → 1, context 212,917 → 23,091 chars. A 9x saving |
| 4 | A fifth example for the MIXED task (find X and fix the safe ones) | **no change** — still 0 spawns; context fell but across different turn counts, which one pair of runs cannot separate from variance |

**WHY 3 WORKED WHERE 1 AND 2 DID NOT**, as far as this evidence goes: a rule asserts a policy and an
example shows the SHAPE of the decision at the moment of choosing. The model does not have to infer
whether its situation is the one the rule meant.

**THE CONTROL THAT MATTERS MOST.** opencode was driven on the IDENTICAL task with the IDENTICAL model
and did not delegate either — zero Task calls, with four system-prompt nudges (including an all-caps
CRITICAL) and two worked examples. **For THIS model, the ceiling is the model rather than our
prompting.** Without that control the honest conclusion would have been "our prompts are deficient",
and the next move would have been more prose.

**EVERY MEASUREMENT IN THIS SECTION IS ONE MODEL — `qwen3.6-35b-a3b` at iq4_xs, local.** cxagent is
provider-agnostic by design and a user may run Claude, GPT, Gemini or anything OpenAI-compatible.
Nothing here licenses a claim about models in general, and a prompt tuned until it works on a 35B
local quant is not thereby tuned for a frontier model — it may be carrying scaffolding a stronger
model does not need, which costs prefix on every turn of every session.

The two references are the corrective: Claude Code and opencode both ship the delegation guidance we
were tempted to conclude was unnecessary, and they ship it for model families we have never measured.
**When a finding here disagrees with what both references do, the reference is the safer bet** — it
was tuned against models we cannot test.

**WHAT REMAINS TRUE AFTER ALL FOUR, FOR THIS MODEL:** it delegates when TOLD to and rarely on its own
judgement. A pure lookup ("where is X?") it will now delegate. A mixed task (find X, then fix it) it
does inline. Four examples was the point of diminishing return here — opencode ships two, ours
carries a counter-example and a split, and past four they stop being a pattern and become a list
nobody reads. Whether a stronger model needs any of them is untested.

**AND THE PARAMETER THAT WAS INVISIBLE UNTIL THE DESCRIPTION EARNED IT.** `context` shipped with
passing end-to-end tests and no model ever used it — it folded the fact into the prompt instead. Four
lines of prose in the tool description fixed it, and the fix was verified by driving, not asserting.
A capability nobody is told about is a capability nobody has, and a test suite cannot detect that.

---

### STEP 2.5 — REVIEW EVERY PROMPT, AND WHERE EACH ONE GOES

**D29. Before step 3 adds more prompt text, audit what is there.** The prompts were written one
finding at a time across a single long session, each addition correct in isolation and none of them
read together since. That is exactly how a prompt accumulates lines that contradict, repeat, or reach
an agent they were never meant for.

**THE MATRIX THAT HAS NEVER BEEN CHECKED.** Four combinations exist and each gets a different prompt:

| | single mode | fan-out mode |
|---|---|---|
| **parent** | no spawn tool, no sub-agent text | spawn tool, D26's three lines, four examples, the type line |
| **child** | n/a — a child is never in single mode | no spawn tool (no spawner), `# Answering` replaced, commands dropped |

Known already, and the reason this is worth a step of its own: ONE OF D26'S THREE LINES WAS UNGATED
AND WAS REACHING CHILDREN until the mode work caught it. That was found by accident while doing
something else. Nobody has since read all four combinations end to end.

**WHAT THE AUDIT MUST ANSWER, per line of every prompt:**
- which of the four combinations is it TRUE for, and is it gated to exactly those?
- does it contradict another line? (`# Answering` was REPLACED for a child rather than appended for
  exactly this reason — two answering sections would have disagreed)
- does it describe a capability the reader has? A single-mode parent told about sub-agents, or a
  child told about `/clear`, is being asked to skim its own instructions.
- is it EARNED? Three of today's prompt changes moved nothing measurable. A line that has never
  changed behaviour is a line whose removal costs nothing and whose presence costs prefix.

**AND THE SAME FOR THE TOOL DESCRIPTION**, which is generated now: the prose, the four examples, the
type catalog, and the `context`/`prompt`/`type` parameter blurbs. It says "It runs once… returns one
message", which step 3 makes false.

**THE DELIVERABLE IS A TABLE**, not an opinion: every line, its home (system prompt or tool
description), its gate (`CanSpawn`, `IsSubAgent`, always), and the evidence it is earned. Anything
with no evidence is a candidate for deletion, and deleting it is cheaper than defending it later.

---

#### THE AUDIT — DONE. Verified against RENDERED PROMPTS, not against the code

Reading the source tells you what the gates are supposed to do. The logs tell you what they did. This
was checked the second way, by grepping the system messages of real sessions — a parent in fan-out, a
child of that parent, and a parent in single mode.

| Block | Gate | single parent | fan-out parent | child |
|---|---|---|---|---|
| `# Doing the work` | always | ✅ | ✅ | ✅ |
| Delegation rule + 4 examples | `CanSpawn` | — | ✅ | — |
| `# Following conventions` | always | ✅ | ✅ | ✅ |
| `# Verifying` | always | ✅ | ✅ | ✅ |
| "a report is a claim, not a verification" | `CanSpawn` | — | ✅ | — |
| `# The user's commands` | `!IsSubAgent` | ✅ | ✅ | — |
| `# Answering` (human reader) | `!IsSubAgent` | ✅ | ✅ | — |
| `# Answering` (**replaced**, sub-agent) | `IsSubAgent` | — | — | ✅ |
| "the user cannot see a sub-agent's work" | `CanSpawn` | — | ✅ | — |
| "several agents can run at once" (D28) | `CanSpawn` | — | ✅ | — |
| `# MCP servers` | always | ✅ | ✅ | ✅ |
| `# Your task` (briefing + caller context) | child only | — | — | ✅ |

**MEASURED, not asserted.** Grepping one fan-out session's parent against its own child:

```
"sub-agent's report is a claim"   parent=1  child=0
"user cannot see a sub-agent"     parent=1  child=0
"Several agents can run at once"  parent=1  child=0
```

And a single-mode session's system message contains **zero** occurrences of `sub-agent`, `spawn`, or
`Several agents`. All four combinations hold.

**NO LEAKS FOUND — and that is a change.** D29 was written because one of D26's three lines had been
ungated and reaching children until the mode work caught it by accident. That class of defect is now
absent, and two things make it structural rather than lucky:

- **`CanSpawn` is `Mode == FanOut && _spawner is not null`**, and `SubAgentFactory` never passes a
  spawner. A child therefore cannot receive spawn text even if someone gated it wrongly — the
  condition is false for reasons that have nothing to do with the prompt author's care.
- **The child's `# Answering` REPLACES the parent's and returns early**, so everything below that
  point in the builder is unreachable for a child by construction. That is why `# The user's
  commands` — which would be actively wrong for an agent with no user — cannot reach one.

**ONE FALSE ALARM, worth recording** because it shows the audit method mattering: a first pass grepped
for `if (ctx.` and reported `# The user's commands` as ungated. It is gated — as `if (!ctx.IsSubAgent)`,
which that pattern missed. The rendered-prompt check found the truth immediately where the code read
ambiguously.

**WHAT REMAINS UNEARNED.** Three of four measured prompt interventions moved nothing (§ "What driving
the prompts actually measured"). Those lines are still there, and this audit does not delete them:
every measurement is on ONE model, and both references ship comparable guidance for model families we
cannot test. Deleting a line because a 35B local quant ignored it would be over-fitting to the one
model we happen to run. Recorded as the open question it is, not resolved.

**DO THIS BEFORE STEP 3'S PROMPT WORK (D28), NOT AFTER.** Adding parallel-agent guidance to a prompt
nobody has audited means the audit then has to disentangle new text from old, and the attribution
discipline that produced today's one usable result depends on changing one thing at a time.

**TWO BRIEFINGS ALREADY HAVE A NAMED DEFECT**, both found by driving real work rather than by
reading:

**1. `explore` has no give-up clause, and burned a whole cap for nothing.** Asked to find the
`intel_gpu_top -q` JSON schema — which is not published anywhere; it is defined in `igt-gpu-tools` C
source — a child spent **all 30 of its turns and 112 tool calls** (52 HTTP, 43 shell `curl`) hunting
it, then filled its last four turns re-reading local files that could not possibly contain an Intel
schema. The envelope said `capped`, the parent was told, nothing broke. But the useful answer existed
at turn 15 and it never gave it.

The briefing says *"Find what was asked for… say what you actually saw rather than what you expect to
be true."* Every clause of that is about a findable target. Nothing tells it what to do when the
thing does not exist, so it kept looking. **A confident negative is an answer** — "not formally
documented, defined in this C source, here is the repo" — and no cap value produces it. This is a
prompt fix, and it belongs in the audit rather than in a number.

**2. A child's wrong path propagates into the parent's work.** An `explore` report listed
`cxgpu/Gpu/Abstractions/GpuBackendPlugin.cs` in its file table while its OWN directory tree, two
sections earlier, correctly placed the file at `cxgpu/Gpu/`. The parent then read the wrong path and
got `error: Could not find file`.

The parent did not guess — **it trusted the report**, which is the correct behaviour and the reason
this matters. A receiving agent cannot tell a cited path from a plausible one, so a summary's errors
become the next agent's errors. Seven of eight sampled paths were right; the failure was a
*self-inconsistency* the child could have caught by comparing its own two sections.

Both are the same shape: the briefing describes the happy path well and says nothing about the
failure mode. The audit should ask, per briefing, **"what does this agent do when the work does not
go as described?"** — a question none of the four currently answers.

**FIXED — `explore` gains two clauses, one per defect:**

> *"If the thing asked for does not appear to exist, say so and say where you looked — a confident
> negative is an answer, and is worth more than more searching. Before you report a path, check it
> against what you actually opened."*

The first would have ended the capped run at turn 15 with a usable answer instead of turn 30 with
none. The second addresses the propagation case directly: the child's tree and its file table
disagreed, and only the child was ever in a position to notice.

**THE OTHER THREE ARE LEFT ALONE, deliberately.** `review`, `test` and `planner` have the same
happy-path shape, but neither defect has been OBSERVED in them — and this project's own evidence is
that unmeasured prompt text usually changes nothing (three of four interventions moved nothing). Two
clauses added for two witnessed failures is attribution; four more added by analogy is the prose
inflation D29 exists to prevent. Revisit when a run shows one of them failing the same way.

---

### STEP 0 — four fixes worth making whether or not sub-agents ship — **BUILT** (11e9dc7)

Found by planning against the code, not by planning sub-agents. **Three are LIVE BUGS today** (0a, 0b,
0d); the fourth (0c) is wanted by per-model ledgers independently. None needs a sub-agent to be worth
doing, and all four are preconditions of step 1 — so they come first and can be judged on their own.

**Order: 0c FIRST.** It is the only one that is pure refactor with no behaviour change, and 0a's
lifetime work sits in the same `WireRunner` body — doing the hoist first means touching that method
once. 0a, 0b and 0d are independent of each other and can land in any order. **0d is written below
between 0b and 0c** because it shares 0a's predicate, not because it runs third.

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

**Done when:** a second Enter during a turn queues instead of corrupting (0a); Escape during a
running tool leaves a `Cancelled` row (0b); `/compress` mid-turn is declined with a line saying why,
and still works when idle (0d); the ledger is constructed in `WireRunner` and an F5 provider change
STILL RESETS IT TO ZERO exactly as today (0c — that is the regression test, since SURVIVING the
rewire is the plausible accident and would silently break `Breached` and the budget); the suite is
green and a live drive shows all four.

---


### STEP 1 — one child, foreground, sequential (needs step 0) — **BUILT** (3656041, 11e07c8, 1a06b09, af2abeb, 38d633b)

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
// INSIDE the spawn branch of InvokeAndShowAsync. `result` is the tool-result string the existing
// path already appends as the Role="tool" message — the catch FILLS IT IN rather than returning.
string result;
try
{
    var send = await child.SendAsync(prompt, ct);
    result = Envelope(child.Id, send.Outcome, send.Text);
}
catch (OperationCanceledException) { job.State = JobState.Cancelled; throw; }
catch (Exception ex)               { result = Envelope(child.Id, SendOutcome.Failed, ex.Message); }
finally                            { tick.Dispose(); }
```

**The two `catch` arms are deliberately asymmetric, and the asymmetry is the whole contract.**
Cancellation RETHROWS: the turn is over, the parent will not send another request, so there is nobody
to hand a tool result to and unwinding the `foreach` is correct. Any other exception does NOT rethrow,
because the parent's next request IS still coming and an orphaned tool call bricks the session (§1b).

An earlier version of this block wrote `catch (Exception ex) { return ErrorEnvelope(ex); }`, which
reads naturally and is WRONG: an early return leaves `InvokeAndShowAsync` before the
`messages.Add(Role="tool", …)` that the envelope exists to become — producing exactly the orphan §1b
says never to produce, in the code §1b wrote to prevent it. Recorded rather than silently corrected,
because it is the second time this document has stated the contract correctly in prose and broken it
in the sample.

`job.State = JobState.Cancelled` is redundant once 0b's own `finally` lands, and is written anyway so
the spawn branch is correct when read on its own.

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
- **REPLACE `# Answering`** — not "add a block", as an earlier draft had it. The section's guidance is
  written for a human reading a terminal, and a child's reader is a model; leaving it in place and
  appending a second one gives the child two sets of answering instructions that disagree. The
  replacement text is written verbatim below.
- **keep** everything else: `<env>`, conventions, verifying, project instructions, MCP instructions —
  all as relevant to a child as to a parent

Mechanically this is one more init-only property on `SystemPromptContext`, exactly how
`McpInstructions` was added, so the cache rules are unaffected: it is fixed at construction. **Two
prompt prefixes per session rather than one** — correct, since they are different agents, and worth
saying so it is not later mistaken for cache churn.

**The parent's prompt: nothing ABOUT SPAWNING** (D25) — it does gain three lines about a parent's
obligations towards a child's work (D26, below), which is a different subject. When to spawn belongs
in the TOOL DESCRIPTION, which is where opencode puts it (`task.txt`) — a tool the model can see and a description it reads at the moment of
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

### THE THREE TEXTS, VERBATIM — phase 1, no roles

The decisions above say what each must carry. This is the text itself, so implementation is
transcription rather than re-derivation. **One child type in phase 1. Roles come later** (D5 already
takes a type name, so a per-role prompt is a lookup where this constant sits, not a redesign).

---

**1. THE PARENT'S SYSTEM PROMPT — three lines, appended into existing sections (D26).**

Into `# Doing the work`, after the "Do not commit" line:

> You are accountable for a sub-agent's work as if it were your own. If its report is thin or does
> not answer what you asked, say so or check it yourself — do not pass it on as fact.

Into `# Verifying`, after the three exit-0 bullets:

> A sub-agent's report is a claim, not a verification. If it says the build passes, the build is not
> verified until you have seen the output yourself.

Into `# Answering`, after the "Talk to the user in your reply" line:

> The user cannot see a sub-agent's work. Anything from one that they need is only in your reply.

Nothing else. No heading, no tool name, no when-to-spawn (D25).

---

**2. THE TOOL DESCRIPTION — where all spawn guidance lives (D25).**

> Run a prompt in a separate agent that has its own context, and get back what it found.
>
> Use it when finding the answer would fill this conversation with material you do not need to keep —
> searching a large codebase for where something is done, reading through many files to answer one
> question, or any open-ended hunt whose intermediate steps are noise once it is over.
>
> Do NOT use it when you already know what to read. A known file, a known symbol, or anything that is
> two or three tool calls away is faster and more reliable done yourself — a sub-agent starts with no
> knowledge of this conversation, so a task you could finish now costs a full briefing and a full run.
>
> It cannot ask you anything. It runs once, with only what you write in the prompt, and returns one
> message. Say in the prompt exactly what you want back, and what "done" means.
>
> Its work is NOT shown to the user — they see only a status row. Anything from its answer that they
> need must appear in your reply.
>
> It cannot spawn sub-agents of its own.

The last line is stated because the child genuinely cannot (below), and a model that assumes it can
delegate onwards writes prompts that instruct the child to do so.

---

**3. THE SUB-AGENT'S SYSTEM PROMPT — one type, phase 1.**

Same prompt as the parent (D24's mechanism: one init-only flag on `SystemPromptContext`) with two
differences and nothing else:

**DROP** `# The user's commands` in full. It names `/help`, `/clear`, `/compress`, `/mcp` and `/exit`
to an agent with no user and no composer.

**REPLACE** `# Answering` — its guidance is aimed at a human reading a terminal, and a child's reader
is a model:

> # Answering
>
> You are a sub-agent. Another agent gave you this task and is waiting for one message back.
>
> Your final message is the whole of what it receives — nothing else you do is visible to it. Put the
> answer there, with the specifics: file paths as file_path:line_number, names, and what you actually
> observed. A summary that omits where you looked cannot be checked or used.
>
> There is no follow-up. Nobody will answer a question, approve a plan, or ask you to continue — so do
> not ask one, do not offer next steps, and do not close by describing what you could do instead.
>
> If you could not do what was asked, say that plainly and say how far you got. A partial answer
> marked partial is useful; a confident answer that is not backed by what you saw is not.
>
> Be brief in the way a report is brief, not the way a chat message is. No preamble.

**KEEP** everything else exactly: `<env>`, `# Doing the work`, `# Following conventions`,
`# Verifying`, project instructions, MCP server instructions. All are as true for a child as for a
parent — and `# Verifying` especially, since the child is the one actually running the commands.

**The three lines from D26 are NOT added to a child's prompt.** A child cannot spawn, so all three
are about a capability it does not have.

**The child does not get the spawn tool** — the ONLY tool it is denied. D15 gets this structurally:
`ISubAgentSpawner` is consulted before `WorkerToolset` for the parent only, so the child is never
offered it rather than being offered it and refused.

---

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


### STEP 2 — named types — **BUILT** (a629c92, 8b6cb0c, fe515eb)

Types in config beside `mcp`: a name, a briefing, optionally a provider instance and a turn cap.

**What shipped, and where it differs from what this section predicted:**

- The tool's schema DID change — it gained an optional `type`. The prediction that "the schema does
  not change, only what a name resolves to" was wrong: a model cannot pass a name it has no parameter
  for.
- `general` IS ALWAYS IN THE CATALOG, supplied implicitly and overridable in config. Not planned
  here, and it earned its place twice: it collapses "no type given" and "type given" into one code
  path (`Resolve(type ?? "general")` never returns null), and it means the catalog is never empty, so
  the description never says "valid types: (none)" and an error always names something.
- A type also carries `maxTurns`. Null inherits the session ceiling, 0 is unbounded. This makes the
  CAPPED path reachable in ordinary use for the first time — until now only a deliberately low
  session ceiling reached it — which is why D13's envelope matters more than it did.
- The tool DESCRIPTION is generated from the catalog. A model cannot pick from a catalog it has never
  seen (D5), and the catalog is per-config, so a `const` could not carry it.

**The provider gap is closed**, and the fix was not the one this section implied. `PluginRegistry`'s
`_ = providers;` was a symptom; the blocker was that NOTHING exposed a per-instance context window.
`ILlmProvider` deliberately does not carry one, `ProviderRegistry` mapped name → provider only, and
`Build()` RECEIVED the windows in `settings.Providers` and dropped them. `ProviderRegistry` now
exposes `InstanceWindows`. Without it a child gets provider A with provider B's window, and
`AgentContext` returns `IsUnderPressure = false` for a MISSING window but never for a WRONG one — so
it never compacts and dies on a provider overflow. Silent, in the dangerous direction.

**Precedence is real**: briefing (config, how to work) above `callerContext` (parent, what to know)
above prompt (parent, what to do). The briefing slot had been deliberately null since `ea97fbd`
because the only legitimate author of the highest-authority text in a prompt is a human writing
config. Step 2 is that human.

**The done-when was changed before it was used.** This section asked for "two types with different
briefings produce visibly different child behaviour" — a judgement about model output, and by then
three prompt interventions had produced two nulls and one win. It was gated instead on what can be
asserted: the type's briefing appears in the child's own `context-000.log` under `# Your task`, two
types differ, a bare spawn and an explicit `general` are byte-identical, an unknown type is refused
naming the valid ones, and a type's `maxTurns` produces `state="capped"`.

**Driven live.** Asked to "use the explore agent to explore this repository", the parent made ONE
tool call — the spawn, carrying `"type":"explore"` — and the child made 42 across 9 turns. Child
context 229,870 chars; parent context 8,684. That is the tool description's promise working
literally, at a 26x ratio.

**What did NOT change: the model still does not delegate on its own judgement.** Asked the same
open-ended question with no mention of agents, it made 19 tool calls and spawned nothing, with
`explore` listed in its tool description. Measured four ways now — tool description, a system-prompt
rule, worked examples, and opencode's own four nudges on the identical task. It is the model's
ceiling, not this design's.

---

### STEP 3 — concurrency: A BARRIER, NOT BACKGROUND (decided)

**D27. Several children run at once and the parent waits for the LAST one.** One assistant message
carries N spawn calls, all N run concurrently, all N results arrive together, and the parent's loop
resumes once — linearly — with everything in hand. Not background: no child outlives the turn that
started it.

**THE MESSAGE FORMAT ALREADY REQUIRES THIS.** A tool call without its result is the orphan that 400s
a session permanently (§1b), so the parent's list is malformed from the moment A returns and B is
still running. Resuming the loop early is not a design one could choose — it produces a request the
provider rejects. The barrier is what the protocol was going to make us do anyway.

**BOTH REFERENCES SHIP EXACTLY THIS**, having had every opportunity to do otherwise. Claude Code's
Agent tool: "When you launch multiple agents for independent work, send them in a single message with
multiple tool uses so they run concurrently." opencode's task.txt: "Launch multiple agents
concurrently whenever possible… use a single message with multiple tool uses." Neither is describing
detached agents; both are describing several tool calls resolved together.

**AND IT ANSWERS THE QUESTION NOBODY HAS A GOOD ANSWER FOR.** A background child hits the permission
gate while the user is reading something else — queue it and the child stalls silently, auto-deny and
it fails oddly, interrupt and it was never background. With a barrier THE USER IS SITTING THERE
WATCHING, because their turn has not finished. The hardest problem in unattended agents is dodged by
not having unattended agents.

Cost, stated: no child survives its turn, so "start this and tell me later" is not expressible. That
is a real capability given up, and it is the one background would buy. Revisit only if a concrete
need appears — not because it sounds more advanced.

#### A BASELINE, AND WHY IT PROVES NOTHING ABOUT THE MODEL

Every assistant message logged by this installation, counted. Of 118 carrying `spawn_agent`:

| Shape | Count |
|---|---|
| `spawn_agent` alone | 111 |
| `spawn_agent, read_file, read_file` | 7 |
| **two or more spawns together** | **0** |

**DO NOT READ THIS AS "THE MODEL WILL NOT PARALLELISE."** It was read that way once and the reading
was wrong, which is why the correction is kept here rather than the mistake quietly removed.

**The model was told not to, and could not have anyway.** The tool description says, in the parent's
own words at `SubAgentSpawner.cs:58`: *"It runs once, with only what you write in the prompt, and
returns one message."* And underneath it, `Agent.cs:773` is a single sequential `foreach` — two
spawns in one message would have run one after the other regardless of what the model intended. A
zero here measures the CONSTRAINT, not the behaviour. It is the shape of what we built.

This is exactly the error §5's prompt findings warn about in the other direction: a measurement of a
system that could not do the thing is not evidence the model would not do it. **STEP 3 IS WHAT MAKES
THE MEASUREMENT POSSIBLE**, not what the measurement justifies.

**WHAT THE BASELINE IS ACTUALLY GOOD FOR — two things:**

1. **The mixed turn is real: 7 of the 118**, every one `[spawn_agent, read_file, read_file]`. That
   shape occurs today, under a sequential loop that handles it by accident, and it is the shape most
   at risk once anything in that loop runs concurrently. The partition is earned by this alone.
2. **It is the before-picture.** Re-run the same count after step 3 ships and after D28, and the
   difference is the answer to "did enabling it change anything" — a count rather than an
   impression. Recorded now precisely because a baseline taken afterwards is worthless.

Multi-call turns being routine otherwise (8, 10, 12 and 16 calls in one message all occur) says the
mechanism is available to this model. Whether it reaches for it with spawns is the open question step
3 exists to ask.

**AND THE ANSWER WILL BE ABOUT ONE MODEL.** Every log counted here is `qwen3.6-35b-a3b` at iq4_xs.
cxagent runs whatever a user configures — Claude, GPT, Gemini, any OpenAI-compatible endpoint — and a
frontier model may well emit three spawns in a message the first time it is allowed to. So the
after-count answers "did enabling it change anything ON THIS SETUP", which is a real question and a
narrow one.

**That cuts in the build direction, not against it.** A capability the local model turns out not to
use may be the capability a user's Claude reaches for immediately, and we would never see that until
it exists. Building for the model in front of us and calling it done is how a provider-agnostic app
acquires a local-model-shaped ceiling.

#### A MIXED TURN: 2 spawns and 3 tools in one message

The obvious reading of "run the children concurrently" is wrong, and it is worth stating before
anyone writes `Task.WhenAll(response.ToolCalls.Select(...))`.

A model does not emit a turn of only spawns. It emits `[spawn A, spawn B, read_file, run_shell,
search_files]` — a mixture — and today `Agent.cs:773` runs all five through ONE sequential `foreach`.
Parallelising that loop wholesale breaks four things, three of them silently:

**1. Four pieces of state are mutated per iteration**, and none is thread-safe. Re-verified against
the current file — the line numbers had drifted by roughly sixty, which is the usual fate of a
citation and the reason each row names what it is as well as where:

| Line | State | What breaks |
|---|---|---|
| `:776` | `wrote` | a lost update means the build-verification gate does not fire |
| `:789` | `seen[signature]` | plain `Dictionary` — concurrent writes corrupt it |
| `:827-828` | `_lastBuild`, `_lastTest` | two commands racing leaves the wrong verdict for the session |
| `:792` | `messages.Add` for the stuck nudge | appends to the LIVE list mid-iteration |

**1b. TWO MORE MUTATIONS THE FIRST PASS MISSED**, found by reading the loop body end to end rather
than grepping for the four already named:

| Line | State | What breaks |
|---|---|---|
| `:814-815` | `stuckOn`, `stuckTimes` | Set when a call repeats past `StuckRepeats * 2`. Two calls racing leaves whichever wrote last, so the turn reports the wrong tool as stuck — or, worse, one clears what the other set. |
| `:831` | `messages.Add` for the tool RESULT | The append every call makes. Concurrent appends to a `List<T>` are not merely out of order — `List<T>.Add` is not thread-safe at all, and a torn write loses a result entirely, which is the orphan of §1b. |

That second one is the important one and it is not a matter of ordering: **the spawn partition must
collect results and append them after the group completes, never append from inside a concurrent
task.** Point 2 below says "collect, then append in the original order" for cosmetic reasons; this
says the same thing for correctness reasons, and the correctness reason is the one that must not be
optimised away.

**2. Results must land in call order.** The `tool` messages are matched to the assistant message's
calls by `ToolCallId`, and while order is not strictly required by the wire format, an out-of-order
list is a needless divergence from what every provider sees in its own examples. Collect, then append
in the original order.

**3. Permission prompts serialise anyway.** `InteractivePermissionGate` holds a semaphore — the
composer cell can show ONE prompt at a time. Three shell commands launched concurrently produce three
prompts that queue, so the "parallelism" is a queue with extra steps, and the user answers them in an
order nobody chose.

**4. run_shell in parallel is a different proposition from read_file in parallel.** Two reads cannot
interfere. Two `dotnet build` invocations in one directory can, and the model has no idea it launched
them together.

**AN AGENT IS A TOOL — and the one place it is not is why this partition exists.**

Structurally there is no difference and that is deliberate: `spawn_agent` is a `ToolDefinition` in
the same array as `read_file`, arrives as a `ToolCall`, resolves through the same `??` chain, and
returns a string that becomes a `Role="tool"` message with a `ToolCallId`. The model cannot tell them
apart, and nothing in the loop treats them differently. That sameness is why step 1 was mostly
plumbing rather than new machinery.

The asymmetry is DURATION AND VISIBILITY. A `read_file` is milliseconds and its result is already
inline; a child is minutes and its forty-odd intermediate calls are invisible by design (D22). Same
interface, three orders of magnitude apart. If an agent were a tool in every respect the right answer
would be to parallelise all of them or none — the partition exists precisely because that one
property differs.

**AND THE PARTITION NEEDS A CLASSIFIER THE CODE DOES NOT HAVE.** Dispatch today is a TRY-CHAIN
(`Agent.cs:1213`): spawner first, then MCP, then the builtin toolset, each returning null if the call
is not theirs. Nothing decides what a call IS before invoking it — the chain finds out by trying.

A partition must classify BEFORE dispatch, and the material is there:
`ISubAgentSpawner.ToolName` exposes the name without invoking anything, so
`calls.Where(c => c.Name == _spawner.ToolName)` is the whole test. Worth stating because "partition
the calls" reads as trivial until you look for the thing that would do it, and the try-chain is
deliberately shaped the other way — it was built so a new toolset could be added with one more `??`
rather than by editing a switch. The partition does not break that; it reads one name first and
leaves the chain intact for everything else.

**SO: OVERLAP THE SPAWNS, NOT THE TOOLS.** One pass in emitted order — a spawn is STARTED and not
awaited, everything else runs inline exactly as today, and the spawn group is awaited at the end of
the turn. Results are collected and appended in call order before the loop resumes.

**Not a partition-and-hoist.** An earlier draft of this step said "partition the calls, spawns
first" — see the ordering discussion above for why that is wrong: it moves a spawn ahead of a
`run_shell` that was meant to precede it, and the child then works against a tree that command had
not yet changed. Start-and-defer gets the identical overlap in every observed turn without ever
moving a call.

That is not a compromise; it is where the benefit actually is. A child is minutes and its
intermediate work is invisible, so overlapping two saves minutes. A `read_file` is milliseconds and
its result is already inline — overlapping it saves nothing and costs the four hazards above.

**Why a spawn is the thing that gets deferred, and not the reverse.** Children are the long pole, so
a child started early has the rest of the turn's work happening inside the window it already
occupies. Deferring the TOOLS instead would have the parent doing quick work while nothing is
delegated, then waiting on children with nothing left to overlap.

Note this is about which call is *awaited late*, not about moving calls: emitted order is preserved
either way. The first draft of this step reached the same conclusion by reordering, which is where it
went wrong.

**And it matches what the model already emits.** All 7 observed mixed turns are literally
`[spawn_agent, read_file, read_file]` — the spawn first, the reads after. The partition preserves the
order the model chose rather than imposing one, which means the reordering is invisible in exactly
the case that occurs.

**BUT THAT EVIDENCE EXPIRES THE DAY THIS SHIPS, and the argument must not lean on it.** Counted
across every log: `spawn_agent` has NEVER appeared after `run_shell` in one message — but only
because the description forbids multi-spawn turns at all, so the shape was unreachable. Step 3
removes that sentence, and `[run_shell, spawn_agent]` becomes newly possible on the first turn after.

**THE MODEL DOES RELY ON WITHIN-MESSAGE ORDER.** `run_shell, run_shell` occurs 64 times and
`run_shell, run_shell, run_shell` 56 times — it batches shell commands routinely, and shell commands
depend on each other (`mkdir` then write into it; `git checkout -b` then build). Those stay
sequential under this design and are safe. What is NOT safe by inspection is hoisting a spawn past a
`run_shell` that was meant to precede it: `[run_shell "git checkout -b x", spawn_agent "work on
branch x"]` starts the child before the branch exists.

**"Results reassembled in call order" does not fix this.** It fixes what the model SEES afterwards —
the side effects already happened in the hoisted order. Reordering a `read_file` is harmless;
reordering across a `run_shell` is not, and the design does not currently distinguish them.

**AND ONE ASSUMED HAZARD IS NOT ONE.** `[read_file config.json, spawn "analyse the config I just
read"]` looks like a broken dependency and is not: a child never sees the parent's tool results in
ANY ordering, because it gets a fresh `AgentContext` (`SubAgentFactory.cs:185`) and only the prompt
text. That dependency is illusory both ways round. **The real channel is the ENVIRONMENT** —
`[run_shell "git checkout -b x", spawn "work on x"]`, or `[write_file fix.cs, spawn "run the
tests"]`, where the child starts against the pre-side-effect tree and returns a confidently wrong
answer with nothing reporting an error.

**THE FIX — DON'T HOIST AT ALL; START AND DEFER.** Walk the calls in emitted order. On a spawn,
START it and do not await. On anything else, run it inline as reached, exactly as today. Await the
spawn group at the end of the turn.

That is strictly better than partition-and-hoist:

- **Identical benefit in the observed case.** All 7 mixed turns are `[spawn, read, read]` — the
  spawn starts first either way, and the reads overlap it either way.
- **It cannot reorder side effects**, because it never moves anything. A `run_shell` that preceded a
  spawn still runs before that spawn starts.
- **It degrades instead of failing.** `[run_shell, spawn]` simply starts the child later — slower,
  correct — where hoisting would have started it against the wrong tree, silently.
- **It is easier to reason about**: one pass, emitted order, one await at the end. No partition, no
  reassembly-order question for the side effects, and the classifier is still just
  `call.Name == _spawner.ToolName`.

The results still collect and append in call order (§1b), and the barrier is unchanged: nothing
resumes the loop until every spawn has returned.

**Cancellation is per-call and Escape must reach every child.** Today one CTS covers the turn and each
tool awaits it in sequence; with two children in flight, Escape has to cancel both AND close both
rows. §1f's `try/finally` is per-invocation, so it already covers each child individually — verify it,
do not assume it.

#### IS THIS STEP STILL WHAT WE WANT? — the honest accounting

Worth asking before building it, because the evidence above weakens the case and the surrounding work
has changed what the step costs.

**WHAT IT BUYS.** Wall-clock, and only wall-clock. Two children that each take two minutes finish in
two minutes rather than four. Nothing else improves: the same tokens are spent, the same context is
saved, the same reports come back. Measured against tonight's runs — a 102s explore, a 480s capped
explore — that is minutes per session, real but not transformative.

**WHAT IT COSTS.** Smaller than when it was written, and this is the part that has moved:

| | Then | Now |
|---|---|---|
| Presentation | unknown; assumed work | **nothing** — verified per entry point |
| Telemetry, per-agent | did not exist | **built** — `Agent.Spend`, child-id attribution |
| History under concurrency | did not exist | **safe by construction** — no mutable state |
| Denial echo | listed as a prerequisite | **already true** |
| Remaining | — | one `SemaphoreSlim`, one parameter, one enum member, the partition, D28 |

**WHAT IT RISKS.** The tool loop is the most load-bearing code in the app, and three of its four
hazards fail SILENTLY — a lost `wrote` flag skips build verification, a corrupted `seen` dictionary
breaks loop detection, a raced `_lastBuild` leaves a wrong verdict for the session. None throws. None
is caught by a test that does not specifically look for it.

**THE VERDICT: build it.** The capability does not exist yet — the tool description tells the model a
spawn "runs once and returns one message", and the loop would serialise two spawns even if it tried.
Step 3 is what makes parallel delegation POSSIBLE; nothing observed so far is evidence about whether
the model would use it, because nothing so far allowed it to.

**Order within the step, and the one thing worth getting right:** the mechanism and the permission to
use it must land TOGETHER. Shipping `Task.WhenAll` while the description still says "returns one
message" leaves a capability nothing will invoke; shipping D28's wording while the loop still
serialises makes the description a lie. Either half alone is untestable.

So: prerequisites first (they are all defensive and none is observable), then the partition and D28
as one shippable unit, then the drive that answers whether the model reaches for it.

**And the measurement to make afterwards is already defined** — the same count that produced the
baseline above. `0 of 118` is the before-picture; the after-picture is the same grep on the same
logs. That is a better gate on "did this change anything" than any impression of the drive.

#### What a barrier still FORCES

Whatever the model does with it, the barrier's own constraints are fixed by the message format and do
not depend on how often several children occur:

- **No child outlives its turn.** The parent's message list is malformed the moment one tool call
  lacks its result, so the loop cannot resume early even if a design wanted it to.
- **Escape must reach every child**, not just the one the parent is awaiting — see the cancellation
  note above.
- **The user is present**, which is what makes a child's permission prompt answerable at all.

**THIS STEP HAS ALMOST NO UI IN IT, and that is a finding rather than an oversight.** A worker is
already fully presented: one row per child, named by type, with live turns, occupancy, elapsed time
and its recent calls, closing with an account of what it cost. Step 3 makes several of those exist at
once and changes nothing about any one of them.

The presentation survives N children by CONSTRUCTION. Cross-checked against the code, every entry
point, not recalled:

| Surface | Why N children is already fine |
|---|---|
| `InlineJobSink` — `SetJobs`, `UpdateJob`, `UpdateProgress`, `AppendText`, `UpdateResources` | **All five** key on `job.Id` and open with `EnqueueOnUIThread`. Rows are independent transcript messages with no shared layout state. |
| `BufferedJobPanel` (what a CHILD actually gets) | One instance per child, and already `lock`-guarded on every accessor — because the parent's tick timer reads `child.Jobs.Jobs` from another thread, which step 1 had to solve anyway. |
| The row's content | Type, model, task, turns, occupancy, elapsed, recent calls, final account. All read from the child's own state; none of it consults a sibling. |
| The session panel | Reads `Ledger.ByModel` / `SubAgentTokens` — one shared ledger, already thread-safe, already summing across children. |

Nothing serialises the rows, nothing interleaves, and no row knows another exists. **Three concurrent
children need no new presentation and no changes to the old.**

**The one exception is a state, not a view** — see prerequisite 3 below. `waiting on permission`
needs a `JobState` member and a word in the header, and it matters ONLY because several children run
at once: with one child at a time, blocked and slow are the same thing to a user, which is exactly
why it never bit.

So the work here is the loop, the locks, and the prompt. Not the screen.

**PREREQUISITE 0 — A LIVE BUG, TODAY, WITH NO CONCURRENCY INVOLVED.** Found by adversarial review of
this step and confirmed with a failing test (`CancellingAChild_LeavesNoToolCallWithoutAResult`):

**Escape during a sub-agent run poisons the session permanently.** The chain, every link verified:

1. `Agent.cs:766` appends the assistant message with its tool calls BEFORE the loop runs.
2. `messages` IS `_context.Messages` (`:383`) — the live session context, not a copy.
3. A cancelled child's `InvokeAndShowAsync` closes its row and RETHROWS (`:1286`). Its comment says
   *"there is no next request"* — true of the turn, FALSE of the session: `AppBootstrap.cs:676`
   catches the cancellation and the conversation continues.
4. Nothing repairs the context. Grepped `AgentHost`, `AppBootstrap`, `AgentContext`,
   `CompressionRun`: no filter, no synthetic result, no `RemoveAt`.

So the context keeps an assistant message whose tool call has no `tool` result — **the orphan of
§1b.** The next request 400s, `ContextOverflow.IsOverflow` does not match it, and nothing recovers
but `/clear`. One Escape, session gone.

Reachable today only where a tool actually throws `OperationCanceledException`: the spawn branch and
MCP. `run_shell` is immune because `ProcessRunner` swallows OCE — which is why this has not been hit
more often, and why it went unnoticed.

**STEP 3 TURNS IT FROM A CORNER CASE INTO THE NORMAL OUTCOME.** With N children in flight, one
Escape orphans N calls, every time. `Task.WhenAll` does not cause this — it inherits and multiplies
it.

**The fix, and it belongs before any of the below:** on cancellation, append a synthetic `cancelled`
result for EVERY call of the turn — including calls not yet started — before the exception leaves
`SendAsync`. Then the conversation is well-formed whatever happened, which is the same discipline
`§1f` already applies to a tool that throws. The alternative (drop the trailing assistant message) is
worse: it discards what the model said it was doing.

**And the spec was wrong here.** It said §1f's try/finally "already covers each child — verify it."
That is half true: ROWS close correctly per child. The CONTEXT was never covered, and the spec did
not mention it.

---

Parallel spawning is not one change. Each of these must land BEFORE two children run at once.
Verified against the code, not recalled:

1. **Permission attribution — HALF DONE.** `PermissionRequest.Requester` exists and a child's prompt
   reads *"asked for by: <description>"*; that shipped with named types. **The gap is MCP:**
   `McpToolset.TryInvokeAsync(call, ct)` takes no agent id, so a permission prompt raised by an MCP
   call cannot say who asked. One parameter, and it is the one request-construction site the
   attribution work did not reach.
2. **The denial echo** (§4.2) goes to the main transcript whoever asked — **ALREADY TRUE.** The gate
   holds a `LatestChatSink` whose `Current` is the session's transcript, and every echo
   (`InteractivePermissionGate:231, 245, 251`) goes through it. A child's denial cannot land in a
   buffered sink nobody reads, because the gate never had a per-child one.
3. **A waiting-on-permission row state** (§3.1) — **STILL OPEN, and the only outstanding UI in the
   step.** `JobState` is Pending/Queued/Running/Paused/Succeeded/Failed/Cancelled/Skipped: no waiting
   member, confirmed in `Core/Models/Job.cs`. Two children, one blocked, is currently
   indistinguishable from slow. Cheaper than when this was written — a row already carries a live
   header and body — so this is one enum member and a header word, not new plumbing.
3b. **`McpClient.Error` IS SHARED MUTABLE STATE THAT CROSS-CONTAMINATES CHILDREN.** Not previously in
   this list. The field is written by any failing or timed-out call and READ INTO ANOTHER CALL'S
   failure text: `CallToolAsync:262-264` renders `error calling '{name}': {Error}` using whatever the
   last failure on that server left behind, and `Error ??=` at `:305` makes the first error sticky
   for the life of the connection.

   Two children on one server: A times out, B's unrelated failure reports A's message, and the model
   reasons from an error that belongs to someone else's call. Silent, and the wrong-diagnosis kind of
   silent. Fix: return the error from `SendAsync` per call, or at minimum stop reading the shared
   field into another call's result.

4. **The MCP write lock — CONFIRMED MISSING.** `McpClient.WriteAsync` (`:340-345`) is a bare
   `WriteLineAsync` followed by `FlushAsync` on a shared stdio pipe, with no lock of any kind. Two
   children calling tools on one server interleave a JSON-RPC frame and corrupt it. One
   `SemaphoreSlim`. The read side is fine — replies multiplex by id.
5. **The turn is MIXED** — see the section above. Walk the calls in EMITTED ORDER, start spawns
   without awaiting, run everything else inline as reached, await the spawn group at the end. Not a
   partition-and-hoist: reordering a spawn ahead of a `run_shell` starts a child against a tree that
   command had not yet changed, and nothing reports an error when it does.

6. **A CONCURRENCY CAP — NOT PRESENT ANYWHERE, and the right value is a property of the endpoint.**
   Nothing in
   `Core/` limits parallelism: `grep -rn "SemaphoreSlim|MaxDegreeOfParallelism" cxagent/Core/` finds
   only `LogFileManager`'s per-path lock and `McpManager`'s connect lock. Neither bounds N children.

   And the provider is a **shared static `HttpClient`** (`OpenAiCompatibleProvider.cs:29`,
   `AnthropicProvider.cs:26`), so N children are N simultaneous requests to one endpoint.

   **HOW BADLY THAT LANDS DEPENDS ENTIRELY ON THE ENDPOINT, and cxagent does not know which one it
   has.** A hosted API answers concurrent requests and pushes back with 429s. A single-threaded local
   server queues them at the socket, so the children hold open connections and running timeouts while
   executing serially. Both are real deployments of this app and neither is the default case — the
   only honest statement is that the right N is a property of the endpoint, not of the design.

   So: **a cap belongs in the spawn partition** — `SemaphoreSlim(N)` around the child launch, barrier
   still collecting every result, only N running at once — and **N belongs in config**, beside the
   other endpoint-shaped values. `providers.<name>.maxConcurrentAgents`, sitting next to
   `contextWindow`, which is already there for exactly this reason: a fact about the endpoint that
   cxagent cannot discover and must be told.

   **UNCAPPED BY DEFAULT — decided.** Absent or `0` means unlimited, matching how `maxTurns` already
   reads 0 as unbounded. The reasoning: a cap chosen without evidence throttles every user to protect
   against a problem none of them may have, and the honest default for an unknown endpoint is not to
   interfere. A user who hits a limit has a key to turn; a user throttled by a guess has no idea why
   their agents are slow. The barrier remains the real invariant — no child outlives its turn.

   **What that costs belongs in CONFIG.md beside the key**, not buried here: 429s and retry
   amplification on a hosted API; socket queueing with N held connections and N running timeouts on a
   single-threaded local server; and the window arithmetic — a local server splits `n_ctx` across
   slots, so each child gets `window/N` while its `AgentContext` believes it has the whole thing, and
   `IsUnderPressure` fires far too late. `AgentContext.IsPossible`'s own doc already names that.

   **AND THE BOUND THAT ACTUALLY MATTERS IS THE BUDGET, NOT N.** `Breached` is a warning today
   (`AgentHost.cs:332`) and nothing refuses a child; `WouldBreach` (`:162-163`) has no caller in the
   spawn path. Consulting it before starting a child — and returning a refusal envelope instead of
   running it — bounds what a user actually cares about, which is cost rather than parallelism.
   Worth doing whether or not anyone ever sets a cap.

   Related and already safe: budget breach cannot double-fire.
   `TokenLedger:157` guards `Breached` with `Interlocked.CompareExchange`, so exactly one caller ever
   raises it however many children record concurrently. That was written for one child and happens to
   be exactly right for N.

**ALREADY SAFE, AND DELIBERATELY SO — the usage history writers.** `UsageHistoryStore` was added
after this list was written and is the one piece of new state two concurrent children will both
touch: a child's `ToolCallFinished` is forwarded through the parent, so N children raise it on one
handler. It holds no mutable state — a connection string, a fresh connection per call — every write
is a single append-only statement with no read-modify-write, and the connection is opened with
`busy_timeout` so two writers wait rather than throw. Nothing to add; recorded here so the next
person does not have to re-derive it.

Two figures it records are also **already per-agent rather than shared**, which matters once several
children run at once: `Agent.Spend` is a private interlocked tally (the ledger is shared and could
never say what ONE child cost), and a child's tool calls keep the CHILD's agent id when forwarded.
Both were built for the row and the panel; both happen to be what concurrency needs.

Only then: `Task.WhenAll` over the spawn partition, sinks marshalling as they already do (all four
implementations go through `EnqueueOnUIThread`), and the results reassembled in call order.

#### 6. THE PROMPTING MUST CHANGE TOO — the one easiest to forget

**D28. The parent's SYSTEM PROMPT gains one line saying several agents may run at once, and the TOOL
DESCRIPTION gains the mechanism.** Both, because they answer different questions at different
moments — the split D25 already draws.

**AND ONE SENTENCE MUST BE DELETED, not merely added to.** `SubAgentSpawner.cs:58` currently reads
*"It runs once, with only what you write in the prompt, and returns one message."* Every clause of
that was true when written and describes a limit step 3 removes. Leaving it in place while adding
"you may launch several at once" gives the model two instructions that contradict, and the older one
is the more specific — which is usually the one a reader believes.

This is the line that makes the `0 of 118` baseline uninformative about the model: it was told not to,
in the one place D25 says such things belong.

Nothing today tells the model that more than one child is possible. The tool description says the
opposite in as many words: *"It cannot ask you anything. It runs once, with only what you write in
the prompt, and returns one message"* (`SubAgentSpawner.cs:58`) — every sentence written for a single
blocking child. The system prompt's two spawn lines are singular throughout ("send a sub-agent",
"use it"). A model reading either will keep spawning one at a time however parallel the runtime
becomes, and the parallelism will be a feature nobody exercises.

**THE MECHANISM MUST BE STATED, NOT IMPLIED.** This is the part both references make explicit, and
the reason is mechanical rather than motivational: a model that wants to parallelise and does not
know HOW will emit the spawns in successive turns and wait for each. opencode: *"to do that, use a
single message with multiple tool uses."* Claude Code: *"send them in a single message with multiple
tool uses so they run concurrently."* Neither trusts the model to infer it.

Where each part goes:

| Change | Where | Why there |
|---|---|---|
| several agents can run at once, in one message | tool DESCRIPTION | read at the moment of choosing (D25) |
| "independent work" as the test for splitting | tool DESCRIPTION | a property of the tasks, weighed when picking them |
| drop "It runs once… returns one message" | tool DESCRIPTION | it becomes false |
| one line that several may run at once | SYSTEM prompt, gated on CanSpawn | an obligation about how to use the capability, beside D26's three |

**AND THE OBLIGATION THAT ONLY APPEARS WITH SEVERAL:** two children given overlapping work will edit
the same files. opencode says it outright — *"avoid working with the same files or topics it is
using"*, *"Work on non-overlapping tasks"* — and it is a CORRECTNESS rule, not an efficiency one. It
belongs in the description beside the concurrency instruction.

**MEASURE IT, AND SHIP THE CHANGES SEPARATELY.** Today's record on prompting is three interventions
for one usable result, and the difference every time was whether a change could be ATTRIBUTED. The
worked examples moved a pure search from 0 spawns to 1 with a 9x context saving because nothing else
changed in that run. Landing the description and the system-prompt line together would change two
things at once and explain neither.

**What the drive must observe:** does the model issue two spawns in ONE message, or two messages? A
unit test cannot answer that — it is a property of what the model emits, and the only instrument is
the parent's own context log, where an assistant message either carries two `spawn_agent` calls or
does not.



The machinery running two children at once does not make a model USE two. Every wording in the tool
description today is written for a single blocking child ("It runs once… and returns one message"),
and a model reading it will keep spawning one at a time however parallel the runtime becomes. Both
references treat this as prompting rather than plumbing:

- **opencode**, `task.txt` note 1: *"Launch multiple agents concurrently whenever possible, to
  maximize performance; to do that, use a single message with multiple tool uses."* Note the
  mechanism is stated — ONE message, SEVERAL tool calls — because a model that wants to parallelise
  and does not know how will issue them sequentially and wait for each.
- **opencode's `anthropic.txt`**, in the SYSTEM prompt: *"If the user specifies that they want you to
  run tools 'in parallel', you MUST send a single message with multiple tool use content blocks. For
  example, if you need to launch multiple agents in parallel, send a single message with multiple
  Task tool calls."*
- **Claude Code's** Agent tool: *"When you launch multiple agents for independent work, send them in
  a single message with multiple tool uses so they run concurrently."*

**What step 3 must add, and where:**

| Change | Where | Why there |
|---|---|---|
| how to spawn several at once — one message, several tool calls | tool DESCRIPTION | read at the moment of choosing (D25) |
| "independent work" as the test for parallelising | tool DESCRIPTION | it is a property of the tasks, weighed when picking them |
| drop "It runs once… returns one message" if background lands | tool DESCRIPTION | it becomes false |
| a `CanSpawn`-gated line about not blocking on a child | SYSTEM prompt, only if a drive shows it is needed | D26's precedent: an obligation, not spawn guidance |

**AND THE PARALLEL-SPECIFIC OBLIGATION.** With one child, "do not also do it yourself" covers the
duplication risk. With several, a new one appears that no current wording addresses: **two children
given overlapping work will edit the same files.** opencode says it outright (*"avoid working with the
same files or topics it is using"*, *"Work on non-overlapping tasks"*). That belongs in the
description beside the concurrency instruction, and it is a correctness rule rather than an
efficiency one.

**VERIFY BY DRIVING, NOT BY ASSERTING.** Whether a model reaches for a parameter, or issues two calls
in one message rather than two messages, is not answerable by a unit test — proven twice already this
project: `context` had passing end-to-end tests while no model ever used it, and the fix was four
lines of prose. Tests pin the guidance so it cannot silently vanish; a drive is what says it works.

**Done when:** two children run simultaneously, each prompt names its requester, the drive shows no
interleaved MCP corruption, AND a model asked for genuinely parallel work issues the spawns in ONE
message rather than serially.

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
