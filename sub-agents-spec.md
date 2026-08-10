# Sub-Agents Design Spec

**Status:** the agent model is built and tested. What remains is a tool to spawn one, and four
solvable problems in permissions and presentation.

This document was written before the context refactor and described much that has since been built.
It has been cut to what is still true and still undecided; the history lives in git.

---

## 1. What an agent is

Every agent is an `Agent` (`UI/Agent.cs`). Not a variant, not a subclass, not a mode — one class,
constructed as many times as there are agents.

**There is no such thing as a "parent agent".** An orchestrator is an agent that has been given a
spawn tool; a sub-agent is one that has not. That is the entire difference — not a different type,
not a different loop, not different context machinery.

This is the whole design. Everything below follows from it.

### Self-containment is done

Built, and pinned by tests (`AgentTests`) so it cannot regress:

- **Its own context.** `context ?? new AgentContext()` — an agent constructed without one gets a
  fresh one, so a sub-agent can never append to its caller's conversation.
- **Its own system prompt**, at position 0 of its own context, built from its own working directory
  and platform. Pinned against compaction by `PinnedHeadCount`.
- **Its own briefing** — what this agent was created to do, fixed at construction, joined to the
  system message last so it outranks the general and project prompts. Constructor-only on purpose:
  the system message is the prompt-cache prefix, and a mutable briefing would rewrite it mid-session.
- **Its own compression**, run against its own context on its own measurement.
- **Its own id**, stable for its life, keying its logs.

### Two decisions, now made

- **Threading: `Task.Run`, and every sink call marshals back to the UI thread.** Today everything
  resumes on the UI thread (no `ConfigureAwait(false)`, an installed sync context), so N loops would
  multiplex against rendering. Agents move to the pool; `EnqueueOnUIThread` is the existing pattern —
  it is what the MCP panel updates already use. **The cost is a one-time audit of every sink** for
  thread-safety, since each currently relies on UI-thread affinity.
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

An earlier draft also offered *ask it something mid-run* and *examine it*. Both are cut, and not
merely for cost: **neither Claude Code nor opencode has them.** Both are spawn-and-collect — a task
in, a result out, no reaching into a running agent. That is what makes the result trustworthy: the
sub-agent's context was nobody's but its own for the whole of its work.

"Examine" survives only as the UI reading a sub-agent's own context to render it (§3). That needs no
protocol — the context is already a public property.

### What a sub-agent returns — DECIDE THIS FIRST

The old draft said "collect its summary" and never defined it. It shapes the tool schema, so it is
the first thing to settle.

`Agent.SendAsync` already returns `Task<string>` — the final assistant text. Claude Code and opencode
both return exactly that. **Proposal: the answer text, nothing more.** A structured result (files
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
- **Elapsed time — missing.** `InlineJobSink` shows duration only in terminal states; a running row
  says `"running…"`. Needs periodic `UpdateJob` ticks and a header-format change.
- **A waiting-on-permission state has no substrate.** `JobState` (`Core/Models/Job.cs`) is
  Pending/Queued/Running/Paused/Succeeded/Failed/Cancelled/Skipped — no waiting member — and nothing
  reports that an agent is parked on approval. Without it, a blocked worker is indistinguishable from
  a slow one.

Row states wanted: running, waiting on permission, failed, complete.

### 3.2 The swap

Expanding a row replaces the chat view with that sub-agent's own transcript. Its own `IChatSink` and
`IJobPanel` are constructor parameters already, so this is a UI composition problem, not a kernel
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

   **Smaller than the old draft claimed.** It estimated "signature changes across at least four
   types, or an `AsyncLocal`". MCP has since shown otherwise: `McpToolset` builds its own
   `PermissionRequest` and calls the gate directly (`McpToolset.cs:151`). So identity can be a field
   on the record, set by whoever constructs it; only the `PermissionGatedPlugin` path — which builds
   requests via the static `PermissionPolicy.RequestsFor` — needs threading.

   This is load-bearing, not cosmetic: **prompts follow the composer, not the view.** You can be
   approving worker 3's write while looking at worker 1's transcript.

2. **The denial echo goes to the wrong transcript.** The gate writes `[red]denied: …` to the
   once-constructed `LatestChatSink` (`InteractivePermissionGate.cs:221`) — the main transcript,
   whichever worker asked.

3. **Shared policy mutation.** One worker's "Always" (`_store.Add`, `:197`) or "Trust this folder"
   (`SetTrust`, `:211`) instantly widens policy for every concurrent worker. The store is
   lock-protected, so this is a semantics decision rather than a race — but it is security-relevant
   and must be deliberate rather than inherited.

4. **`TrustQuestionControl` bypasses the semaphore.** It uses the prompt seam directly
   (`AppBootstrap.cs:642`) without the gate's serialisation, so colliding with a worker prompt makes
   it silently no-op and leak an uncompleted await.

Queue order is arrival order.

---

## 5. What is actually left

1. **Decide the return value** (§2) — shapes the tool schema, so it comes first.
2. **A spawn tool**, and a factory to construct an agent with a briefing and its own sinks.
3. **Permission attribution** (§4.1) plus the denial-echo fix (§4.2).
4. **A waiting row state** (§3.1) — needs a gate→UI notification channel that does not exist.
5. **Decide shared-policy semantics** (§4.3) and fix the trust-prompt bypass (§4.4).
6. **The sink thread-safety audit**, once agents run on the pool.

Everything else this document used to call open is built: per-agent context, system prompt, briefing,
compression, id, cancellation, and a thread-safe ledger.

---

## 6. Out of scope

- **Nested spawning.** A sub-agent that can spawn is an orchestrator; nothing structurally prevents
  it, and nothing should encourage it until one fan-out level is proven.
- **Per-agent ledgers and cost attribution** — deferred deliberately (§1).
- **Kernel/presentation separation.** `Agent` and `AgentHost` live in `UI/` and should not; that move
  is worth doing, but AFTER sub-agents, which are the first real consumer of the kernel as a service
  and will say what shape it needs.
