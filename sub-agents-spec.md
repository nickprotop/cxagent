# Sub-Agents Design Spec

**Status:** design. The *agent* side is now built and driven — see §1. What remains unbuilt is the
presentation (§2) and the orchestrator. Supersedes the existing fan-out code, which is obsolete.

**Revised** after the context refactor (commit `77734f9`), which turned several of this document's
assumptions into facts and disproved two of its stated blockers.

**Goal:** every agent is a full, self-contained single agent — its own conversation, context,
compression and job rows. Sub-agents report a summary and expose hooks so an orchestrator can drive
them. Each appears as one row in the main transcript; expanding a row replaces the chat view with
that agent's own transcript.

---

## 1. What an agent is

Every agent is a `SingleAgentLoop`. Not a variant, not a subclass, not a mode — one class,
constructed as many times as there are agents.

**There is no such thing as a "parent agent".** An orchestrator is an agent that has been given the
`llm-agent` tool; a sub-agent is one that has not. That is the entire difference — not a different
type, not a different loop, not different context machinery. Every agent is isolated and
self-contained by the same construction, and an orchestrator's own context, compaction and job rows
work exactly as any other agent's do.

This is the whole design. Everything below follows from it.

### What an orchestrator can do to a sub-agent

Because a sub-agent is an object with a life of its own rather than a function call, the orchestrator
can hold it and drive it:

- **spawn** it with a task
- **ask** it something mid-run, on demand
- **examine** it — its context, its occupancy, what it is doing now
- **stop** it
- **collect** its summary when it finishes

The tool surface for these is out of scope here (§6.4); what matters for this document is that they
are all reachable, because the sub-agent is a live `SingleAgentLoop` and not a completed `Task`.

`SingleAgentLoop` has no static state and no singleton reach-through. Its UI touchpoints all go
through two injected interfaces:

| Interface | Members used |
|---|---|
| `IChatSink` | `BeginAssistantTurn`, `EndAssistantTurn`, `AppendAssistant`, `ShowError` |
| `IJobPanel` | `SetJobs`, `UpdateJob` |

Both are constructor parameters. A sub-agent is therefore created by passing different
implementations of these two interfaces.

### What is now actually true

This section previously described what *would* be true if a sub-agent were constructed. Since the
context refactor it describes what is:

- **Its own conversation and its own occupancy.** `AgentContext` (`Core/Llm/AgentContext.cs`) owns
  one growing message list for an agent's whole life, plus its window and its last measured
  occupancy. It is an optional constructor parameter on `SingleAgentLoop`; **omitting it gives that
  agent a fresh one of its own**, which is the correct default — an agent that is not handed a
  context still HAS one rather than borrowing the caller's list.
- **Its own compression.** `compressAbove` is a constructor parameter, and the per-turn check runs
  against the agent's own context. This is now the ONLY automatic route: the between-goals check was
  deleted, because both read the same measurement and so both fired on it (two rows on a live drive,
  24.5s and 26.1s, the second summarising a context whose older half was already a summary).
- **Its own compaction.** `SessionCompressor` summarises the older half and keeps the newer verbatim,
  splitting so a kept tool result always keeps its call. Summarisation is the ONLY tier: a cheaper
  tool-output pruner was built, measured (−18% instantly, against −24% for a ~25-second call) and
  removed — only Cline ever shipped content dedup and it is gone from their HEAD, opencode ships its
  pruner off by default, and the two agents that prune hard (Claude Code, Antigravity) persist to
  disk first, which we cannot. Summarisation READS what it discards; that is the property worth
  having until real usage says otherwise.
- **Continuity across tasks**, which is what "self-contained" actually requires. Verified live: after
  reading a file in goal 1, goal 2 answered from context with no tool call.

### The token ledger is the one thing still shared

`GoalRunner` constructs one `TokenLedger` and one `AgentContext` (`GoalRunner.cs:373-374`). The
context is now per-agent by design; the **ledger is not**, and it is wired app-wide:
`RecordWorkerUsage` pumps worker usage in from DAG executor threads, `JobDiagnoser` and `/compress`
write to it, and the status bar and session panel read it.

`TokenLedger.Record` does `_input += … ; _output += … ; _total += …` with no `lock` or `Interlocked`
(`TokenLedger.cs:41-51`), and it is **already multi-writer today**. Giving each sub-agent its own is
a re-plumbing job across that wiring, and it must decide what the session-level readouts then show.
Either way the race wants fixing on its own merits, independently of fan-out.

---

## 2. Presentation

### 2.1 The row (first increment)

Each sub-agent owns one row in its orchestrator's transcript:

```
▸ worker 2 · refactor IndentShift    ⠹ 1m14s · 12.4k/208k
```

The row is useful on its own, before any drill-down exists, and it is what makes the shape of a
fan-out legible: N workers, N rows, no interleaving.

**The ctx figure now exists; the other two parts of the row do not.**

1. **Occupancy — SOLVED.** `AgentContext.Used` holds the last turn's `Usage.InputTokens` and
   `UsedFraction` divides it by the window, so `12.4k/208k` is a property read off the sub-agent's
   own context. The loop publishes it via `ContextUsed`, added for exactly this. (The status bar had
   the defect this item described — dividing the *cumulative* ledger total by the window, which read
   107% of a window that was not close to full and could never fall after a compression — and was
   fixed at the same time.)

2. **Elapsed time — still missing.** `InlineJobSink.CompactHeader` (`:539-554`) shows duration only
   from `job.Result.Duration` in terminal states; the running `StatusText` is literally `"running…"`
   (`:704`). A live clock needs periodic `UpdateJob` ticks and a header-format change.

3. **A waiting-on-permission state has no substrate.** `JobState` (`Core/Models/Job.cs:3-13`) has no
   waiting member, and nothing reports that a worker is parked: the gate exposes no event and
   `PermissionGatedPlugin.ExecuteAsync` (`:40-59`) awaits it without telling `IJobContext`. This
   needs a gate→UI notification channel that does not exist.

Row states wanted: running, waiting on permission (§3), failed, complete.

### 2.2 The swap (second increment)

Expanding a row replaces the chat view with that sub-agent's own `ChatTranscriptControl`:

```
_mainGrid.ReplaceControl(Chat, worker.Transcript)   // in
_mainGrid.ReplaceControl(worker.Transcript, Chat)   // Esc, out
```

The same *mechanism* as the permission prompt's composer swap — but a different grid and a separate
seam, so everything that makes the prompt swap safe must be **replicated, not inherited**:

- `ReplaceControl` throws `ArgumentException` from inside the render loop if the old control is not
  currently placed (`GridControl.cs:389-391`). That is why `_activePrompt` bookkeeping exists; the
  swap needs its own equivalent tracking which control occupies the cell, covering worker A→worker B
  swaps and double-expands.
- Focus must be moved in by hand (`MainWindow.cs:752-766`) and restored on the way out
  (`FocusComposer`, `:956`).
- **The trigger is undesigned.** These rows are `CollapsiblePanel`s whose expand affordance already
  means "show the body in place", and `InlineJobSink` calls `ClearActions(id)` on every `UpdateJob`
  (`:278`), so a footer action button would be wiped on each transition.

**Off-screen sub-agents keep running.** `GridControl.RemoveControl`
(`GridControl.cs:334-350`) unlinks the control and sets `Container = null`. It does **not** dispose.
A swapped-out `ChatTranscriptControl` is an ordinary live object: its message list keeps filling and
its scroll position survives, so swapping back shows everything that arrived while away. Only
painting pauses — a control with no container has nothing to invalidate into, which is the desired
behaviour (an off-screen sub-agent costs no render).

`Invalidate` is `Container?.Invalidate(...)` (`BaseControl.cs:226`), so appends to a detached
transcript are safe. Two exceptions matter:

- **Spinners die permanently.** `SpinnerControl` registers with the `AnimationManager` by walking
  `GetParentWindow()` at `StartAnimation` time (`SpinnerControl.cs:201-215`). A spinner created while
  the transcript is detached finds no window and never registers — and reattaching the *transcript*
  does not touch the spinner's own `Container` (its parent is the `CollapsiblePanel`), so nothing
  re-registers it. Result: a frozen glyph after swap-in. Needs an explicit re-registration path.
- **"Costs no render" conflates paint with work.** `ChatTranscriptControl.Append` calls `RenderBody`
  — a full re-render of the accumulated body — on *every token* (`:519-526`), attached or not. N
  streaming workers means N full-body markdown re-renders per token. Painting pauses; the layout
  work does not. This interacts directly with §5.1.

Reattachment behaviour (scroll position, auto-scroll) needs a live test, not a static check.

**Why a real transcript rather than streamed text.** `IJobPanel.AppendText(jobId, delta)` can stream
prose into a job row's body, and that was the cheaper option considered. It was rejected because a
sub-agent's tool calls would degrade into plain text — losing headers, spinners, expand affordances
and the red failed-row treatment. Giving the sub-agent a transcript of its own keeps every tool row
a real tool row. `ChatTranscriptControl` is flat (a message list, not a tree), which is correct
*inside* a sub-agent's own view; nesting is expressed by the swap, not by the transcript.

### 2.3 Status bar

While swapped into a sub-agent, the status bar must show which view is active and how to leave it —
e.g. `worker 2 · Esc to return`. Without this the swap has no visible exit.

---

## 3. Permissions

**Same place, queued.** Permission prompts continue to appear in the composer, one at a time, and
workers block until their turn.

**This already works.** `InteractivePermissionGate` serialises on a `SemaphoreSlim(1, 1)`
(`_oneAtATime`, awaited at line 126, released at line 165), awaits the prompt control's `Completion`
and restores the composer in a `finally`. Callers already block rather than fail, and a goal
cancelled while still queued resolves as a deny rather than a hang.

`MainWindow.ShowPermissionPrompt`'s `if (_activePrompt is not null) return;` is **not** the gate — it
is a defence-in-depth guard behind it, documented as such at the call site. It does not need to
become a queue.

### What fan-out does require

- **Attribution — larger than a line of content.** `PermissionRequest` is `(Kind, Display,
  AlwaysRule)` with no requester identity (`PermissionTypes.cs:15`), and no identity channel reaches
  the gate: requests are built by static `PermissionPolicy.RequestsFor(typeName, parameters)` inside
  the one shared `PermissionGatedPlugin` serving every loop through the one shared `PluginRegistry`.
  Naming the worker means threading identity through `WorkerToolset.InvokeAsync` → plugin → request,
  or an `AsyncLocal` — signature changes across at least four types.
- **The denial echo goes to the wrong transcript.** The gate writes `[red]denied: …`
  (`InteractivePermissionGate.cs:221`) to the once-constructed `LatestChatSink`
  (`AppBootstrap.cs:65-67, 149`) — the main transcript, whichever worker asked.
- **Shared policy mutation.** One worker's "Always" — or "Trust this folder" — instantly widens
  policy for every concurrent worker (`Apply` → `_store.Add`/`SetTrust`; `IsSilentlyAllowed` reads
  the live store). `PermissionRulesStore` is lock-protected, so this is a semantics decision rather
  than a race, but it is security-relevant and must be deliberate.
- **`TrustQuestionControl` bypasses the semaphore.** It uses the prompt seam directly
  (`AppBootstrap.cs:678-705`) without the gate's serialisation, so colliding with a worker prompt
  makes it silently no-op and leak an uncompleted await.
- **A waiting row state.** A worker blocked on approval shows `⏸ waiting` (or queued behind another)
  so the user knows to look. Without it, a blocked worker is indistinguishable from a slow one.
- **Prompts follow the composer, not the view.** The composer is shared chrome, so a prompt appears
  wherever the user currently is — including while swapped into a *different* worker. This is why
  attribution is load-bearing rather than cosmetic.

Queue order is arrival order.

---

## 4. Job rows belong to their own agent

A sub-agent gets its own `IJobPanel` alongside its own `IChatSink`. Its job rows — including its
compression rows — render in **its** transcript, not its orchestrator's.

Stated explicitly because it is obviously right once written down and silently wrong if nobody
decides it: without it, an orchestrator driving four workers fills its column with their
housekeeping.

Not a concurrency hazard either way: `InlineJobSink.SetJobs` is additive by id
(`InlineJobSink.cs:53-58`), so concurrent loops append rather than clobber.

---

## 5. The two unasked questions

These are not open questions in the §6 sense — they are decisions the design cannot proceed without.

### 5.1 On which thread do N loops run?

cxagent has **zero** `ConfigureAwait(false)`, installs a synchronization context
(`AppBootstrap.cs:35`), and launches goals fire-and-forget from the key handler (`:540`). So today the
entire loop — streaming, tool dispatch, compression, and the per-delta `StripReasoning` over the whole
accumulated text (`SingleAgentLoop.cs:531-541`) — resumes on the **UI thread**.

- Spawn N workers from the orchestrator's context and they multiplex against rendering on one thread,
  compounded by the per-token full-body re-render in §2.2.
- Spawn them via `Task.Run` and the races currently *masked* by UI-thread affinity become real.

Either is workable. Not deciding is not.

### 5.2 How does any of it stop?

The only `CancellationTokenSource` is app-lifetime (`AppBootstrap.cs:75`); Ctrl+Q and `/quit` cancel it
and call `Shutdown()` (`:584`, `:476-479`). There is **no per-goal cancel** to inherit. Undesigned:

- What happens to running workers when their orchestrator finishes or fails.
- What happens on quit — after `Shutdown()` the UI queue stops pumping, so a continuation posted to
  the sync context never runs. A worker not linked to `cts.Token` freezes rather than cancels, and
  its shell children (killed only via ct/timeout, `ProcessRunner.cs:107-121, 145-147`) can outlive
  the app.
- **Esc is already globally bound** to dialog-cancel/DiscardDraft routing (`AppBootstrap.cs:599-608`),
  so "Esc to return" (§2.3) must be merged into that router rather than added beside it.

Related: `GoalRunner` passes `maxTurns = int.MaxValue` in single-agent mode *explicitly because* "the
user is watching it, can stop it" (`GoalRunner.cs:464`). A fanned-out worker is unwatched and, per
the above, individually unstoppable — so reusing the loop unchanged silently reuses a justification
that no longer holds. Sub-agents need a turn cap, a per-worker cancel, or both.

One pressure this removes: that comment also claims "the context window ends a session that
genuinely cannot continue." That is now true — an agent compacts its own context on its own
measurement, so a long-running worker no longer grows until the provider rejects it. It bounds
*context*, not *time*, so an unwatched worker can still loop indefinitely without spending more
context. The cap is still wanted; it is no longer the only thing standing between a worker and a
wall.

---

## 6. Open questions

1. **Nesting depth.** Can a sub-agent spawn sub-agents? One level needs a single swap; deeper needs
   a view stack. Recommend deciding explicitly rather than discovering it.
2. **Parent aggregate tokens.** Whether the session panel shows a combined total across workers, and
   if so by which of the two mechanisms in §1.
3. **What fills an orchestrator's context.** Not a design question — an orchestrator is an agent like
   any other and needs nothing special — but worth knowing when thresholds are tuned. An ordinary
   agent's context is mostly file reads and shell output — bulky, and often superseded. An
   orchestrator's is mostly sub-agent summaries: already dense, already the compressed form of
   something larger, and so far less compressible again. Same machinery, same threshold, but an
   orchestrator gets less back per compaction. Worth watching once fan-out is real, since it is the
   most likely place a cheap tier would earn its way back in.
4. **Orchestrator hooks.** This spec covers what an agent IS. The tool surface an orchestrator drives
   sub-agents through — spawn, ask, examine, stop, collect (§1) — is deliberately out of scope, but
   note it cannot
   be built without touching the loop: the toolset is a fixed static list of `WorkerTool`s
   (`SingleAgentLoop.cs:191`, `WorkerToolset.Specs`) with no injection point, so a spawn/await tool
   means growing the enum and Specs table or making the toolset injectable. The result channel is
   also currently a side effect (`conversation.Add`, `:238`, `:373`) plus a bare `GoalState`, and
   `TurnCompleted` is wired to session-panel/token UI, so per-worker rewiring is a decision rather
   than a given.
5. **goalId minting.** Sub-agents need distinct goalIds or their log streams collide — two loops both
   writing `turn-000`, and now `context-000`, under one goalId. `GoalStarted`→`SessionId` display
   also assumes one goal. The per-turn context logs make this more valuable than it was: with
   distinct ids, each sub-agent's context is separately inspectable, which is most of what debugging
   a fan-out needs.
6. **Transcript factory.** New worker transcripts need the role-style setup `MainWindow` applies to
   `Chat` at build (`:336-405`). Trivial, but someone must own it.
7. **F5 re-wire.** Re-wiring disposes the runner and rebuilds the `PluginRegistry` mid-flight
   (`AppBootstrap.cs:137-144`); N long-running workers widen an existing hazard.

---

## 7. Summary of cost

Revised twice: after review, and after the context refactor that made the agent side real. The core
identification — a sub-agent is a `SingleAgentLoop`, cleanly injected via `IChatSink`/`IJobPanel` —
held up. What this document originally overstated was how much already existed; what it *understated*
was how much of the remaining work is presentation rather than agent behaviour.

**Done** (commit `77734f9`):

| Item | Status |
|---|---|
| Sub-agent = full `SingleAgentLoop` | Verified |
| Own conversation, own context, own occupancy | `AgentContext`, one per agent |
| Own compression, own pruning | Per-turn, against its own context; single route |
| Continuity across tasks | Driven live: goal 2 answered from goal 1's read |
| Row: ctx figure | `AgentContext.Used` / `UsedFraction` |
| Permission queueing | Already existed — `SemaphoreSlim` in the gate |

**Remaining:**

| Item | Cost |
|---|---|
| Own token ledger | Re-plumbing across app-wide wiring; not free |
| `TokenLedger` synchronisation | Wanted regardless of fan-out — already multi-writer |
| Row: elapsed time, waiting state | New machinery |
| Expand → swap, Esc → back | New seam with its own bookkeeping; trigger undesigned |
| Off-screen loops keep running | Holds, except spinner re-registration and per-token re-render |
| Permission attribution | Threading identity through ~4 types |
| Denial echo, shared policy, TrustQuestion bypass | Three separate fixes |
| Threading model (§5.1) | Must be decided |
| Cancellation and shutdown (§5.2) | Must be designed |
| Orchestrator tool surface | Touches the loop's toolset |
| Existing fan-out code | Obsolete, replaced |
