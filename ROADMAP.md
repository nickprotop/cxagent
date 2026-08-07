# cxagent — Build Roadmap

**cxagent** is a TUI AI orchestrator built on SharpConsoleUI (part of the cx app family). An LLM decomposes a goal into a DAG of typed jobs, which run in parallel respecting dependencies, with real-time UI and AI-driven failure recovery.

- **Design spec:** `~/source/cxagent-design.md` (the authoritative design; framework-refreshed 2026-07-10, copilot mode specified).
- **Plans:** `docs/superpowers/plans/` (gitignored — working artifacts).
- **This file** is the durable, committed status tracker. Update it as each plan lands.

The spec is deliberately built as a **sequence of plans**, each producing working, testable software on its own. The foundation is done; the rest layer on top.

---

## Status at a glance

| Plan | Subsystem | Status |
|------|-----------|--------|
| **P1** | Headless core (DAG + scheduler + orchestrator + LLM HAL/mock) | ✅ **Done** |
| **P2** | Persistence (SQLite + log files + crash-resume) | ✅ **Done** |
| **P3** | Real Job Engine + built-in plugins (self-contained: shell/file/wait/http; docker/git/llm_agent → P3b) | ✅ **Done** |
| **P4** | Provider drivers (Claude native + `openai-compatible` + `ollama`) | ✅ **Done** |
| **P5** | UI — decomposed into P5a/P5b/P5c | ✅ **Done** |
| ↳ **P5a** | App shell + live goal run (bootstrap, GridControl split, ChatTranscript+streaming, MLE input, statusbar, GoalRunner) | ✅ **Done** |
| ↳ **P5b** | Job panel (JobBlockControl composites + live log tail; read-only) | ✅ **Done** — 157/157, live tmux-verified |
| ↳ **P5c** | First-run wizard + settings (consumes ProviderRegistry) | ✅ **Done** — 185/185, live tmux-verified |
| **P6** | Diagnosis / recovery flow + cost caps + resource monitoring | ✅ **Done** — 256/256, live tmux-verified against real llama.cpp |
| **P7** | Roles + provider routing (catalog-bound, multi-instance; incl. the `llm_agent` plugin deferred from P3) | ✅ **Done** — 12 tasks, drive-verified against real llama.cpp |
| **P7b** | Job output references (`{{job.key}}` between jobs) | ✅ **Done** — closed a silent-success defect the P7 drive found |
| **P8** | Orchestrator feedback loop (plan → execute → report → replan) + mid-run job introspection | ✅ **Done** — drive-verified |
| **P8b** | Worker tools (roles declare tools; bounded, metered tool loop) | ✅ **Done** — one job now reads/shells/writes, drive-verified |
| **P9** (v1.1) | Copilot mode (Draft phase — approve before run, F9/Esc) | ✅ **Done** — drive-verified; edit affordances + DagTreeView NOT built |
| **P9b** | Copilot gates jobs the orchestrator adds mid-goal | ✅ **Done** |

---

## P5c — First-run wizard + settings ✅ DONE

**Commit range:** `e2aa345..e115116` (8 SDD tasks: `ProviderKindCatalog`, `ProviderConfigWriter`, `IModelCatalog`, `OllamaProvider` HttpClient fix, `ProviderProbe`, `MaskedPromptStep`, `SetupWizard`, MainWindow/AppBootstrap wiring) · **Tests:** 185/185 green (157 P5a+P5b + 28 P5c) · **Live tmux-verified** end to end, including a real local LLM endpoint (llama.cpp `llama-server`, `qwen3.6-35b-a3b-ud-iq4_xs.gguf`).

A 6-step wizard (Welcome → Provider → name/endpoint/credentials → Test Connection → Model → Setup complete) opens automatically on first run (no configured provider), is re-openable anytime via **F5**, and writes `~/.config/cxagent/config.json` at `0600`. Driven twice against a live endpoint:

- **Pass A (real provider):** `openai-compatible` kind against `http://127.0.0.1:8771/v1`. The kind list showed all three registry kinds (Anthropic, OpenAI-compatible, Ollama); the API-key field rendered `*****` for a 5-char key, never plaintext; **Test Connection returned "Connected. Tool calling: supported"** against the live endpoint; the **model step showed a real HTTP-fetched dropdown** containing `qwen3.6-35b-a3b-ud-iq4_xs.gguf` — the only true end-to-end proof of `IModelCatalog`; Back correctly returned from Test Connection to Credentials, clearing the masked field. After Finish, the chat posted `Configuration saved. Provider: openai-compatible …` with **no API key in the message**, the composer was immediately usable with no restart, and a typed goal (`list three colours`) ran against the real model end to end (`GoalRunner` → `IChatSink` → live streamed assistant reply). A relaunch with the same `XDG_CONFIG_HOME` skipped the wizard entirely and a goal still ran.
- **Pass B (failure path):** re-ran via F5 with kind `anthropic` and a bogus key. Test Connection failed gracefully — `Could not reach the provider: provider 'anthropic' returned 401 after 1 attempt(s). (You can continue and fix this later in Settings.)` — **no mention of tool calling on the unreachable branch**, wizard still fully navigable, Back returned to Credentials, and closing via the window's `[X]` left the app usable with the Pass-A config on disk untouched (verified byte-for-byte after Pass B).
- **F1 help** (verified by a synthetic mouse click to expand the collapsed system message, since `ChatTranscriptControl` exposes no content accessor) lists **F5 — provider settings / re-run setup**.

**The mandatory tmux smoke-drive caught one real bug** that all 185 headless tests missed, because it's a literal string only visible on the painted buffer:

1. **Both buttons on the final "Setup complete" step are labeled "Finish".** `SetupWizard.cs`'s last step calls `ctx.Confirm("Setup complete", message, "Finish", "Finish")` — the `Confirm(title, message, ok, cancel, …)` signature takes distinct OK/Cancel labels, but the call passes the literal `"Finish"` for both, so the negative/secondary button reads "Finish" instead of a Back/Cancel-style label. Functionally harmless past `ctx.Commit()`'s Back barrier (there is nothing left to go back to), but confusing UX and a genuine copy-paste bug. **Not fixed here per plan** — reported for the fix loop.

**Also observed, not a P5c defect but worth recording:** `AppPaths.EnsureCreated()` creates the config directory via a bare `Directory.CreateDirectory` with no explicit mode, so the directory's permissions follow the process `umask` (verified `775` under `umask 0002`) rather than the `0700` the P5c plan's self-review table assumed ("dir already handled by `AppPaths`"). The config **file** itself is correctly forced to `0600` by `ProviderConfigWriter` regardless of umask. Deferred — no task in this plan owned directory permissions.

**Interaction quirks noted during the drive (not correctness bugs):** the wizard's own OK/Continue/Begin buttons respond to **Space**, not Enter, once the window has been reached by keyboard navigation from a fresh step (Enter works immediately after a mouse-driven step transition, but not consistently after a keyboard one) — worth a follow-up UX pass, not a functional defect since Space always worked. Provider-kind list items are cycled with **Tab**, not arrow keys.

---

## P5b — Job panel ✅ DONE

**Commit range:** `f1cb40c..b860480` (6 SDD tasks + a verification pass) · **Tests:** 157/157 green under `--blame-hang` (128 P5a + 23 P5b + 6 from the verification pass) · **Live tmux-verified** end to end.

The job panel is real: submit a goal → one `JobBlockControl` per job with ColorRole border + status icon + elapsed → live `UpdateJob` as the DAG runs → focus the panel (F3) → Enter collapses/expands a block → **expanding starts the live log tail** (`fetched 3 repositories` read from P2's log file appears inside the expanded block).

**The mandatory tmux smoke-drive again caught what all headless tests missed** — 3 real bugs, none of which a test could see because they need the painted buffer and a real key path:

1. **The goal composer accepted no input at all.** `MultilineEditControl` is modal: focused-but-not-editing handles only navigation keys and BUBBLES the rest, so typed characters are discarded. It flips to editing only on Enter — but AppBootstrap's `PreviewKeyPressed` consumes every Enter to implement Enter-submits, so that transition could never fire. The app was unusable. Fix: `FocusComposer()` sets focus **and** `IsEditing = true`, and every path back to the composer goes through it.
2. **`--mock` threw on every goal.** `ProviderResolver` returned an unseeded `MockLlmProvider`, whose queue-driven `ChatAsync` does `Queue.Dequeue()` → `InvalidOperationException("Queue empty.")`. That surfaced as a red `✗ Queue empty.` and left the panel permanently empty (`SetJobs` runs only after `PlanCompiler.BuildDag`). Fix: `SeedDemoPlan` — 4 jobs, two parallel roots → join → report, side-effect-free built-ins only.
3. **The live log tail froze past `tailLines`.** `LogTailPoller._emittedCount` was compared against the *sliding tail window*, whose `Count` pins at the cap — so once the log outgrew 20 lines `tail.Count > _emittedCount` never held again and the tail silently stopped. Found by code review, not the drive. Fix: count against total lines, prime the first read with the window, resync on truncation. Regression test appends *after* the poller starts (the pre-existing window test only read a static file, which is why it missed this).

**Also fixed (P5a carry-over, blocking P5b's headline feature):** the status bar advertised `Ctrl+N`/`Ctrl+J`/`Ctrl+H`/`F1` but **only `Ctrl+Q` was ever registered** — four dead keys. Worse, two of them are unbindable in principle: a terminal sends `Ctrl+J` as `0x0A` (identical to Enter) and `Ctrl+H` as `0x08` (identical to Backspace), so no implementation can distinguish them — the same class of limitation as the `Ctrl+Enter` problem in `e272fac`. Verified empirically: a file-logging callback registered on `Ctrl+J` never fired. **Rebound to F-keys** (escape sequences, unambiguous): **F1** help · **F2** new goal · **F3** focus jobs · **F4** focus chat · **Ctrl+Q** quit. A drift-guard test asserts the bar only ever advertises keys that can actually be bound.

**Deferred (unchanged):** AI diagnosis, DAG-mutating actions (retry/skip/edit), resource sparklines → P6. >20-job virtualization → later.

---

## P5b — original plan (as written before the build)

Spec: `docs/superpowers/specs/2026-07-12-p5b-job-panel-design.md` · Plan: `docs/superpowers/plans/2026-07-12-p5b-job-panel.md` (both gitignored). Second slice of P5. Replaces the "JOBS (P5b)" placeholder + the lossy latest-only sink with a live job panel: one `JobBlockControl` per job (status/progress/**ColorRole border** by state), updated live as the DAG runs, expandable to a **live-tailing log view**. Read-only monitoring.

**Scope (decided):** blocks + expand + live log tail. **Deferred → P6:** AI diagnosis, DAG-mutating actions (retry/skip/edit/remove), resource sparklines (the block reserves the actions row, stubbed). **Deferred → later:** >20-job virtualization.

**Key design:** dedicated **`IJobPanel`** seam (`SetJobs`/`UpdateJob`) parallel to `IChatSink` — `GoalRunner` calls `SetJobs(dag.AllJobs)` after `PlanCompiler.BuildDag` and `UpdateJob(job)` per `JobTransitioned`, staying UI-agnostic; the lossy `IChatSink.ShowJobTransition` is REMOVED. `JobBlockControl : CollapsiblePanel` + `JobPanelControl : ScrollablePanelControl` are composites (no paint code). The one real piece of machinery is **`LogTailPoller`** (its own isolated, tested unit): ~500ms re-read of P2's log file, emit-only-new-lines, into a readonly `MultilineEditControl`; started on expand, cancel+await on collapse; ONE active poller (collapse-others-on-expand). **Folds in the P5a fire-and-forget carry-forward** (wrap `GoalRunner.RunAsync` in an outer try/catch).

**6 tasks (SDD):** T1 LogTailPoller · T2 JobBlockControl · T3 IJobPanel + JobPanelControl · T4 JobPanelSink (marshalled) · T5 GoalRunner rewire + remove ShowJobTransition · T6 MainWindow/AppBootstrap wire + E2E + mandatory tmux drive.

**Plan-authoring verified the framework** and caught the LogViewer mismatch: `LogViewerControl` is a structured log *console* (LogLevel/entry model), NOT a text-append control → the tail body is a **readonly `MultilineEditControl`** (`.ReadOnly`+`.Content` verified). Also confirmed built-in keyboard: `ScrollablePanelControl` handles ↑↓, `CollapsiblePanel` handles Enter→Toggle (so no hand-wired keys). Applies the P5a **star-collapse fix** (`Fill`/`Stretch` on the JobPanelControl) so the panel renders — a lesson the tmux drive will re-verify.

---

## P5a — App shell + live goal run ✅ DONE

**Commit range:** `1f1c50a..22aad35` (7 tasks + 2 render fixes) · **Tests:** 128/128 green under `--blame-hang` (111 prior + 17 P5a) · **Live shell tmux-verified** · Whole-branch review: **READY TO MERGE**.

cxagent now has a real, usable TUI: launch → load provider from config (or graceful no-provider / `--mock`) → type a multi-line goal (Ctrl+Enter) → watch it stream its decomposition token-by-token and execute its DAG to completion. Replaces the console demo.

**Reviewed:** every task per-task + whole-branch. **The mandatory tmux smoke-drive caught 2 real render bugs that ALL 128 headless tests missed** (they can't read the painted buffer): (1) nested GridControls render blank — flattened `MainWindow` to a single grid (the AgentStudio-proven pattern); (2) `ScrollablePanelControl` Star rows collapse to 0 under unbounded-height measure — added `VerticalAlignment.Fill`/`HorizontalAlignment.Stretch` to the grid + chat. This is exactly why CLAUDE.md mandates the live drive. The whole-branch review confirmed: the ONE P1 touch (PlanCompiler extraction) is a verbatim, isolated move (OrchestratorTests unchanged); the marshalling model has NO off-UI-thread control mutation (GoalRunner is UI-agnostic; ChatTranscriptSink wraps all 6 methods in EnqueueOnUIThread); one LLM call drives everything with no plan divergence; VendorBody contained; no-provider path crash-safe; zero console output.

**Key P5a facts:** goal input is `MultilineEditControl` (Ctrl+Enter submit, a deliberate deviation from the spec's single-line PromptControl). Test hooks (UI-queue drain, key injection) are `internal` → `SharpConsoleUI.Tests` only, so headless tests assert marshalling *deferral* + call `RunAsync` directly; the real key/render path is the tmux drive's job.

**Deferred to P5b (tracked, NOT gaps):** real job-block composites (replace the JOBS placeholder) — **note: the real job-panel content will likely need the same Fill/Stretch treatment (the Star-collapse trap)**; wrap the whole `GoalRunner.RunAsync` body in an outer try/catch so a residual fire-and-forget fault becomes a visible chat error (currently unreachable — job errors caught inside DagScheduler); wire `SqliteGoalStore`/`PersistenceSubscriber` (the run-loop seam). Ghost `using System.Text.Json` in GoalRunner + `MainWindow.Build()` double-call guard = harmless defers.

---

## P4 — Provider drivers ✅ DONE

**Commit range:** `f98b109..596a825` (9 tasks + 2 mid-branch fixes) · **Tests:** 111/111 green under `--blame-hang` (80 prior + 31 P4) · Whole-branch review: **READY TO MERGE**.

Spec: `docs/superpowers/specs/2026-07-11-p4-provider-drivers-design.md` · Plan: `docs/superpowers/plans/2026-07-11-p4-provider-drivers.md` (both gitignored). Turns P1's mock-only `ILlmProvider` HAL into real, configurable, multi-backend LLM access. Everything sits **below** the HAL — the core never changes.

**Reviewed:** every task per-task + whole-branch. The SDD process caught & fixed two plan self-contradictions mid-branch: (1) T3/T5 streaming used `while(!reader.EndOfStream)` — a synchronous blocking read (CA2024) wrong for SSE — replaced with `while((line = await ReadLineAsync(ct)) is not null)`; (2) T8 `ProviderRegistry` set `ProviderId` to the *kind* (my brief was self-contradictory) — corrected to the **instance name** so multiple same-kind instances are distinguishable and `Goal.ProviderId` records which instance ran (user-decided). The whole-branch review confirmed zero unauthorized P1/P2/P3 edits (`LlmTypes.cs` purely additive), the neutral contract is genuinely vendor-agnostic at the HAL boundary, the shared retry helper's disposal contract holds across all 4 call sites, and the opposite-by-design tool-arg handling (OpenAI string round-trip vs Anthropic object passthrough) is internally symmetric per driver.

**Broad backend reach (verified):** the `openai-compatible` `baseUrl` covers OpenAI, **OpenRouter**, Groq, Together, Fireworks, DeepSeek, Mistral API, vLLM, llama.cpp, LM Studio — and any Llama/Mistral model over the OpenAI wire — with zero extra code. `extraHeaders` gives OpenRouter attribution / gateway routing.

**New Low findings for later plans (not P4 defects):** (a) `extraHeaders` is parsed for any kind but only consumed by the openai-compatible driver — a config'd `extraHeaders` on an anthropic instance is silently ignored (P5 wizard could warn). (b) don't blind-render `LlmProviderException.VendorBody` into shared/persisted logs (P5 error UI). (c) a truncated mid-stream SSE reads as a clean final in both drivers — no error surfaced (a future reconnection/offline task should know the driver won't signal it). All deferred.

**Scope (decided in brainstorming):** Claude native (`anthropic`) + `openai-compatible` + a named `ollama` preset. The `openai-compatible` `baseUrl` already covers OpenAI, **OpenRouter**, Groq, Together, Fireworks, DeepSeek, Mistral API, vLLM, llama.cpp, LM Studio — and any Llama/Mistral model served over the OpenAI wire — with zero extra code. Retry-in-driver (429/5xx/Retry-After), real SSE streaming now (StopReason/Usage normalized at the driver boundary), optional `extraHeaders` for gateways (OpenRouter attribution), and a config-driven `ProviderRegistry` + batched startup validation (the capability the P5 wizard and P3b llm_agent build on).

**9 tasks (SDD, same cycle as P3):**
- **T1** — `LlmProviderException` + `LlmHttpRetry` (shared transient-retry helper)
- **T2** — `OpenAiCompatibleProvider.ChatAsync` + `LoopbackServer` test fixture
- **T3** ✅ (e859688) — `OpenAiCompatibleProvider` streaming (SSE)
- **T4** ✅ (819e33f) — `AnthropicProvider.ChatAsync` (native Messages, system hoist, tool blocks)
- **T5** ✅ (fe64e1d) — `AnthropicProvider` streaming (SSE)
- **T6** ✅ (6018a97) — `OllamaProvider` (localhost/keyless preset of the OpenAI-wire driver — genuine subclass, not a facade)
- **T7** ✅ (05a1d66) — `ProviderConfig` loader + batched validation + env-var key override
- **T8** ✅ (c4f6a98) — `ProviderRegistry` + factory (construct by kind, `Default`, `TryGet`; ProviderId = instance name)
- **T9** ✅ (4736369) — End-to-end: real `Orchestrator` decomposes + runs a goal through a real driver over loopback HTTP

**Deferred (tracked, NOT gaps):** vendor-specific kinds with bespoke auth — `google`/`bedrock`/`vertex`/`azure-openai`/native-`mistral`/`cohere` (each a small later plan on the open registry, no HAL change); provider UI wizard → **P5**; `llm_agent` + per-job `routing` consumption → **P3b** (P4 parses+validates routing config only); cost-cap enforcement from normalized `Usage` → **P6**; reconnection/offline-queue loop → existing orchestrator concern.

**Plan-authoring caught two real bugs before build** (via verifying P1's actual code): `StartGoalAsync` runs the whole DAG (so the E2E's `runJob` is load-bearing, goal returns `Completed`), and `Goal` has no `.Jobs` property (jobs live in the internal DAG — same friction P3 hit). T9 assertions fixed to P1's real contract.

**Only permitted edit to existing code:** additive `LlmProviderException` in `Core/Llm/LlmTypes.cs`. HAL, P1/P2/P3, and `AppPaths` untouched.

---

## P3 — Job Engine + self-contained plugins ✅ DONE

**Commit range:** `7b00daf..14ee325` (8 tasks) · **Tests:** 80/80 green under `--blame-hang` (46 P1+P2 + 34 P3) · Whole-branch review: **READY TO MERGE**.

Plan: `docs/superpowers/plans/2026-07-11-p3-job-engine.md` (gitignored). Turns P1's `runJob` stub into real execution. Scope: infra + shell/file/wait/http; docker/git/llm_agent → P3b; DLL-scan + script/container tiers deferred (spec v1 scope). **P1/P2 untouched** — the whole-branch review confirmed zero P1/P2 production edits; `JobExecutor.RunJobAsync` matches the existing `runJob` signature by structural type-match.

**Reviewed:** every task per-task (spec + quality) + whole-branch. The SDD process independently verified several things green tests hid or that a task-scoped review couldn't see: the ProcessRunner kill-tree/timeout-vs-cancel semantics, the HTTP retry loop's exact attempt count (2 for `max_retries:1`, no double-increment), the JobExecutor chaining key (dependency ULID, producer/consumer-consistent) and fail-fast order, and — critically — that JobExecutor's swallowed `OperationCanceledException` is **benign, not just deferred**: it matches P1's own scheduler catch, and with shutdown-only cancellation + manual-only retry it cannot cause a retry storm or wrong goal state in v1.

- [x] **T1** ✅ (171deb6) — Plugin contract (`IJobPlugin`/`IJobContext`/`JobSchema`/`JobValidation`)
- [x] **T2** ✅ (f4eb9c7) — `ProcessRunner` (async stdout/stderr, timeout+cancel kill tree)
- [x] **T3** ✅ (06535db) — ShellJobPlugin
- [x] **T4** ✅ (5f26371) — FileJobPlugin
- [x] **T5** ✅ (b5217e9) — WaitJobPlugin + HttpJobPlugin
- [x] **T6** ✅ (c49d8e3) — PluginRegistry (first-wins + shadow warnings)
- [x] **T7** ✅ (fc32bea) — JobContext + JobExecutor (the P1 runJob bridge; chaining via DAG)
- [x] **T8** ✅ (70ab4f1) — End-to-end: real file+shell jobs run to completion
- [x] **Final** ✅ — whole-branch review: READY TO MERGE (all 5 carry-forward Minors triaged *defer*, none fix-now)

**Deferred out of P3** (tracked): docker/git/llm_agent plugins (**P3b**); native DLL-scan + script/container plugin tiers (later — biggest scope sink); LLM consumption of `JobSchema` for `create_plan` (**P4**); `wait` manual-click + progress events (**P5** — `// TODO(P5)` seams); executor↔Orchestrator DAG-sharing (P5/run-loop — same P1-friction seam as P2); command allowlist/sandbox (post-v1, spec trust model).

---

## P2 — Persistence ✅ DONE

**Commit range:** `dc4e751..7b00daf` (6 tasks + 1 disposal fix) · **Tests:** 46/46 green under `--blame-hang` · Whole-branch review: **READY TO MERGE**.

Built (all under `cxagent/cxagent/Core/Storage/`):
- `AppPaths.cs` — per-OS config-dir / db / logs resolution (overridable for tests).
- `SqliteGoalStore.cs` (+ `IGoalStore.cs`) — Microsoft.Data.Sqlite, raw parameterized SQL, spec schema, `PRAGMA foreign_keys=ON`+`WAL` on every connection, idempotent upserts, `JsonElement`-safe param/result round-trip (reuses P1's `Get<T>`), dangling-tool-result drop on conversation load.
- `LogFileManager.cs` — per-job `logs/<goal>/<job>.{log,stdout,stderr}`; `DeleteGoalAsync` removes the log dir.
- `ResumeService.cs` — startup reconciliation: Running→Failed('interrupted'), Queued→reprime-list, Paused stays, Draft untouched, reconciled states re-persisted.
- `PersistenceSubscriber.cs` — observes the orchestrator's `JobStateChanged`/`GoalStateChanged` (P1 untouched); serialized-channel writer + `IAsyncDisposable`.

**Reviewed:** every task per-task + whole-branch. The SDD process caught three real issues green tests hid: a write-ordering race in the plan's own subscriber design (fixed via a single-reader channel), the GoalId P1-friction bridge, and a subscriber worker/channel disposal leak (fixed via `IAsyncDisposable`).

- [x] **T1** ✅ (dc4e751) — AppPaths + PersistenceException + Microsoft.Data.Sqlite package
- [x] **T2** ✅ (dbd159a) — SqliteGoalStore: schema, PRAGMAs (FK+WAL), goal/job save-load (JsonElement round-trip)
- [x] **T3** ✅ (f4f0511) — Conversation persistence + dangling-tool-result drop
- [x] **T4** ✅ (3da5da7) — LogFileManager + DeleteGoalAsync removes log dir
- [x] **T5** ✅ (f72f408) — ResumeService: startup state reconciliation
- [x] **T6** ✅ (767bbc4..7b00daf) — PersistenceSubscriber: wire orchestrator events → store + end-to-end
- [x] **Final** ✅ — whole-branch review: READY TO MERGE

**Deferred (tracked so they aren't lost):**
- Retention pruning — needs config + a startup loop (a later config/run-loop plan). Primitives (`DeleteGoalAsync`, `LogFileManager.DeleteGoalLogs`) exist.
- PID/handle double-run guard — **P3** (documented `// TODO(P3)` seam in `ResumeService`).
- LLM interrupted-goal re-evaluation — **P4/P6** (documented `// TODO(P4/P6)` seam).
- `GoalState.Paused` resume — `ResumeService` loads Active/Draft only; Paused goals aren't produced yet (no pause feature until UI/run-loop). Add a Paused branch when goal-level pause lands.
- Whole-branch nits (non-blocking): `jobs.log_file` is a dead write (round-trips via `result_json`; kept as a future audit column); no write-ordering regression test (ordering is architectural via SingleReader channel); assert `JobResult.Duration` in the round-trip test; a dispose-race silent write-drop could get a logging seam.

---

## P1 — Headless core ✅ DONE

**Commit range:** `fbd2fe6..b4cccab` (10 commits) · **Tests:** 24/24 green under `--blame-hang` · **Demo:** `dotnet run` decomposes a canned goal and runs the DAG to completion (exit 0).

Built (all under `cxagent/cxagent/`):
- `Helpers/UlidGenerator.cs` — monotonic sortable ids (dependency-free).
- `Core/Models/` — `Job`/`JobResult`/`JobParameters`/`Goal`/`ChatMessage`. `JobParameters.Get<T>` is `JsonElement`/`JsonNode`-safe (survives LLM-args + future SQLite round-trips — never blind-casts).
- `Core/Orchestrator/JobDag.cs` — pure graph: ready/dependents/ancestors/topo-sort, cycle + dangling-dependency validation. Skip propagates like success.
- `Core/Orchestrator/DagScheduler.cs` — the concurrency core: serialized `_schedulerLock` (propagation + slot fill atomic), concurrent execution fired-not-awaited, idempotent slot release, `maxParallel` cap, retry-through-Queued, skip-as-synthetic-success, quiescence → Completed/Failed. **Guards against overlapping drives** (throws rather than deadlock). `IDisposable`.
- `Core/Llm/` — `ILlmProvider` HAL (the only vendor seam), neutral `LlmResponse`/`LlmStreamChunk`/`ToolDefinition`, `MockLlmProvider` (queue-driven, the dev/test backbone).
- `Core/Orchestrator/Orchestrator.cs` — goal → `ChatAsync` → `create_plan` tool call → two-pass plan-local-id→ULID mapping (dup/dangling ids rejected) → validated DAG → `DagScheduler`. Disposes the scheduler via `using`.
- `Program.cs` — headless demo entrypoint.

**Reviewed:** every task per-task + a whole-branch review (verdict: READY TO MERGE). The process caught three real issues: a `MaxRetries` mutability regression (kept `init`), a latent concurrent-drive deadlock (found by concurrency review, invisible to the sequential tests), and a scheduler-disposal handle leak.

**Known v1 gaps deliberately left for later plans** (documented in code with `TODO`):
- The `create_plan` `ToolDefinition` is not yet *sent* to the provider — v1 relies on the mock being pre-loaded. A real provider (P4) needs the tool definition passed on the `ChatAsync` call.

---

## Next steps (remaining plans)

**P1-P6 are DONE and shipped** (see the per-phase sections above). The plans below are what remains.
Each is its own `brainstorm -> writing-plans -> subagent-driven-development` cycle.

### P7 - Roles + provider routing ✅ DONE
Plan: `docs/superpowers/plans/2026-08-04-p7-roles-routing.md`

Makes `llmAgent.routing` real - it has been parsed and validated since P4 and consumed by nothing.
Named roles (built-in planner/implementer/reviewer/debugger, plus user-defined) each bound to a
**provider-instance + model pair from the configured catalog**, not to a bare model name. The catalog
holds several instances of the same kind (e.g. two OpenRouter accounts, one local llama.cpp, one
Anthropic), so a binding must name the instance.

Delivers: `RoleRegistry` + persistence under `llmAgent.roles`; an OpenRouter *preset* over the
existing `openai-compatible` kind (not a new driver); a searchable model picker (OpenRouter returns
several hundred ids); a multi-instance catalog editor and an additive wizard; `RoleResolver` with
per-call model override via `ILlmProvider.WithModel`; a role editor UI; the **`llm_agent` plugin**
(deferred from P3 - until it exists, no job can invoke an LLM and roles have nothing to attach to);
per-job `model_hint`; and role exposure in `create_plan`.

Two texts, two audiences, enforced at four points: a role's **Description** (3rd person, what the role
IS) goes to the orchestrator's role enum *and* the worker's system message; its **SystemPrompt**
(2nd person, how it BEHAVES) goes to the worker only.

### P7b - Job output references ✅ DONE
Plan: `docs/superpowers/plans/2026-08-04-p7b-job-output-references.md`

Unplanned, and only found because P7's live drive ran a real goal: "review this file, then write the
review to <path>" produced four jobs that ALL SUCCEEDED and wrote the literal 10 bytes `{{review}}`
to disk. Silent success — not a crash, a goal reporting Succeeded while producing a placeholder.

Root cause was three-layered: no substitution existed anywhere; `CompletedJobOutputs` was readable by
`llm_agent` alone, so `file`/`shell` jobs could not see an upstream result at all; and `PlanCompiler`
discarded the orchestrator's own plan-local job ids, so `{{r1}}` could never have resolved even with
substitution in place.

Delivers: `Job.PlanLocalId` carried through compilation AND persistence; a `{{job.key}}` parser that
fails loudly rather than passing a placeholder through; one substitution pass at `JobExecutor`'s
choke point serving every plugin; and the syntax documented in `create_plan` with a worked example.

Two Criticals were caught by the final review AFTER the plan's own tasks were green: substitution
initially fired on ANY `{{`, breaking every previously-working job that used Go-template, Jinja or
Handlebars syntax (`docker inspect -f '{{.State.Running}}'`, Helm charts, GitHub Actions workflows);
and `PlanLocalId` was not persisted, so references broke across a restart in exactly the resume path
`ResumeService` exists for. Both fixed; `{{x}}` is now a reference only when `x` names a declared
dependency, with a near-miss still failing loudly so a misspelled id cannot silently revert the
original defect.

### P8 - Orchestrator feedback loop
Design notes: `.superpowers/sdd/p8-design-notes.md` (decisions already made with the user)

Today the orchestrator plans once and never learns what happened: `GoalRunner` ends at
`ShowGoalResult` and returns. P8 closes the loop - plan -> execute -> report -> replan - which is the
difference between a DAG executor and an agent.

Decided: **extend the live DAG** (stable job ids, one continuous panel, prior outputs stay addressable);
**three termination conditions** (an explicit `finish_goal` tool, a max-rounds cap, and
`GoalTokenBudget`); **Claude-Code-style feedback** (bounded per-job digest with a visible
`[... N bytes elided ...]` marker, errors never truncated, plus the log path and a
`get_job_output(job_id, offset, limit)` tool - large results go to files, not context); and **automatic
rounds** the user can cancel, with each round's plan and results shown in chat.

Also in scope: **mid-run job introspection** - `list_jobs`, `get_job_status`, and `get_job_output`
against *running* jobs, so the orchestrator can ask what a job is doing and what remains. This is a
tool-surface task rather than plumbing: `Job.Progress`/`ProgressMessage`, continuously-written logs,
`ResourceSnapshot`, and the full `JobDag` all already exist and are wired. Open question deliberately
left for real usage: whether introspection happens only at round boundaries or *during* a round, and
if during, whether the orchestrator may act (cancel/skip) or only observe - acting mid-round re-enters
the drive-overlap hazard P6 spent two review rounds on.

Prerequisites, both met by P6 as shipped: diagnosis spend now records into the same `TokenLedger`
(so the cap is real, not fiction), and scheduler re-entry is safe (`WaitForQuiescenceAsync` plus
no-eager-dispose). One hard-won constraint carried forward: **quiescence is not ownership.**

### P9 (v1.1) - Copilot mode
Additive over v1 autopilot: `GoalState.Draft` (scheduler inert), block-level editing (params via
`FormControl`), the per-job dependency picker, LLM-assist authoring actions, and the read-only
`DagTreeView` (over `TreeControl`). Only new code: the Draft gating, the `DagTreeView` composite, the
mode UI. **Spec §Copilot Mode.**

Deliberately last: a copilot that cannot pick a model per role is the weaker version of P7, and one
without P8's loop is a chat window rather than an agent.

---

## Post-v1 (from the spec's Future Considerations)
Plugin marketplace · client/server split · shared DAGs (export/import) · webhooks & scheduling · multi-goal tabs · OS keychain · per-plugin concurrency limits · job priorities · notification integrations.

---

## Conventions (for any contributor / future session)
- **.NET 10** (`net10.0`), `Nullable` + `ImplicitUsings` enabled. Solution is `cxagent.slnx`.
- **ConsoleEx reference is conditional** — local `ProjectReference` to `../../ConsoleEx/SharpConsoleUI/SharpConsoleUI.csproj` when the sibling repo exists, else `PackageReference SharpConsoleUI 2.5.11`.
- **TDD** per task; commit frequently; `--blame-hang-timeout` on anything touching the scheduler.
- Gitignored: `.claude/`, `CLAUDE.md`, `docs/superpowers/`, `.superpowers/`, `bin/`, `obj/`.
- **Never block on async from the UI thread** once the UI lands (P5) — `InstallSynchronizationContext = true` makes that a self-deadlock.
