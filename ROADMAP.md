# cxagent — Roadmap

**cxagent** is a terminal AI coding agent built on [SharpConsoleUI](https://github.com/nickprotop/ConsoleEx),
part of the cx app family.

This file is the durable, committed status tracker: what is built, what is next, and what was
deliberately abandoned. Update it as work lands.

---

## The architecture changed, and this file was written before it did

The original plan (P1–P9b, below) built an **orchestrator**: an LLM decomposed a goal into a DAG of
typed jobs, a scheduler ran them respecting dependencies, and a feedback loop replanned on failure.
That was built, drive-verified, and then **removed** — `Core/Orchestrator/` no longer exists.

What replaced it is smaller and does more: **one linear turn loop.** Send the conversation, and if
the reply has no tool calls the turn is over; otherwise run the tools, append the results, and send
again. No DAG, no scheduler, no plan compiler. The model decides what to do next by looking at what
just happened, which is what a DAG's dependency edges were approximating.

The plan-first machinery was not wrong so much as **premature**: it fixed the shape of the work
before the model had seen any of it. Recording this because a reader finding the P-plan tables below
would otherwise reasonably conclude the DAG is still in there.

---

## Status

| Subsystem | State |
|---|---|
| **Turn loop** — send, run tools, repeat; cancellation-safe | ✅ Done |
| **Providers** — Anthropic native, `openai-compatible`, `ollama`; multi-instance, role-bound | ✅ Done |
| **Tools** — read/write/edit/search/shell/http, permission-gated | ✅ Done |
| **Permissions** — per-call gate, always-allow rules, boundary-aware | ✅ Done |
| **Persistence** — SQLite resume buffer, per-turn context dumps, per-tool logs | ✅ Done |
| **Context management** — summarising compaction with an orphan-safe cut | ✅ Done |
| **MCP** — stdio + HTTP servers, OAuth, per-server instructions in the prompt | ✅ Done |
| **Sub-agents** — typed workers, isolated context, several concurrent per turn | ✅ Done |
| **Usage history** — `/stats` dashboard over a local archive | ✅ Done |
| **Skills** — on-demand instructions, catalog in the prompt, body on request | ✅ Done |
| **UI** — transcript, job rows, session panel, command palette, settings | ✅ Done |

**1146 tests.** Every subsystem above is drive-verified against a real local model
(`qwen3.6-35b-a3b` on llama.cpp) as well as unit-tested — the two catch different things, and the
drives have caught more.

---

## Recent work

| Feature | What it added |
|---|---|
| **Sub-agents** | A parent delegates to isolated workers with their own context and briefing. Several launched in one message run concurrently; none outlives the turn that started it. |
| **Usage history** | Five tables in a separate `history.db`, and a `/stats` dashboard: tokens by project, by agent type, what fills the context. See `stats-spec.md`. |
| **Skills** | `SKILL.md` files whose name and description ride in the prompt while the body is loaded on demand, with its other files listed by absolute path. |
| **Kernel isolation** | How a provider-agnostic `cxagent.kernel` would talk to a presentation layer, so a web front end becomes a sibling rather than a rewrite. See `isolated-kernel.md` — **designed, not built.** |

**The sub-agent and skills specs were working documents and have been removed.** Their reasoning is
in the code comments, which is where it stays current; the specs themselves are in git history if the
argument behind a decision is ever needed —
`git show 423d8bc:sub-agents-spec.md` and `git show e0ceca7:skills-spec.md`.

---

## Next

Nothing here is committed to. Ordered by how much each would change day-to-day use.

### Verify what is built before building more

- **Compaction with a skill loaded, live.** The notice that tells the model its skill was summarised
  away is unit-tested and has never fired in a real session. The same blind spot hid the `load_skill`
  bypass: every test passed while the live behaviour was wrong.
- **A sub-agent loading a skill, live.** The worker row's `skills:` line exists for exactly this and
  has only been seen in tests.
- **One unexplained test flake.** `AgentChallengeTests.TheModelsRawResponseIsLogged` failed once in a
  full-suite run and never again. Not reproduced, not understood.

### Features

- **`cxagent.kernel` extraction.** The design exists. The payoff is a web presentation and reuse in
  the other cx apps; the cost is that every seam has to be honest about what it needs.
- **Skill scripts.** Reference *documents* now work — a skill's other files are listed by absolute
  path when it loads, and the model reads them through the permission-gated file tool. What is still
  deferred is a skill shipping something meant to be RUN: that is code from disk the user did not
  write, and the gate governs *shell access*, not *where a command came from*.
- **Narrowing a child's skill catalog by agent type.** A planner that can load a deployment skill is a
  planner with a distraction. Cheap once a skill exists that someone wants withheld.
- **Sandboxed shell.** The one thing standing between the current loop and running tools in parallel:
  concurrent shell commands sharing a working directory can interfere in ways nothing here would
  detect.

### Known limits, kept in view

- **Delegation and skill-loading are properties of the model, not the tool.** On a local
  `qwen3.6-35b-a3b` both work — but skill-loading only after the prompt was changed to say that
  reading a `SKILL.md` directly is not the same as loading it. A stronger model may need neither
  nudge; a weaker one may ignore both.
- **A briefing is a request, not a permission.** "Never edit files" in a type's briefing asks; it does
  not remove the tool. Permissions are the mechanism.
- **No child outlives its turn.** The message format requires every tool call to be answered in the
  same turn, so a background agent has nowhere to put its result.

---

## Conventions

- **.NET 10** (`net10.0`), nullable + implicit usings. Solution is `cxagent.slnx`.
- **SharpConsoleUI reference is conditional** — a local `ProjectReference` to `../../ConsoleEx/…`
  when the sibling repo exists, else a `PackageReference`.
- **Tests first, and make them fail first.** A test that passes before the fix has proven nothing.
  Several bugs here were found only because a test was deliberately sabotaged to check it could fail.
- **Drive it live.** Every subsystem above was driven in tmux against a real model before being called
  done, and several defects were invisible to the suite: a permanently-poisoned session on Escape, a
  worker row that measured a permission prompt as runtime, a model bypassing `load_skill` entirely.
- **Never block on async from the UI thread** — `InstallSynchronizationContext = true` makes that a
  self-deadlock.
- Gitignored: `.claude/`, `CLAUDE.md`, `bin/`, `obj/`.

---

## The original plan (P1–P9b) — built, then superseded

Kept for provenance. **The DAG orchestrator these describe was removed**; see the top of this file.
The provider, persistence, plugin and UI foundations they built are still here and still in use.

| Plan | Subsystem | Status |
|------|-----------|--------|
| **P1** | Headless core (DAG + scheduler + orchestrator + LLM HAL/mock) | Superseded |
| **P2** | Persistence (SQLite + log files + crash-resume) | ✅ Kept |
| **P3** | Job engine + built-in plugins (shell/file/wait/http) | ✅ Kept |
| **P4** | Provider drivers (Claude native, `openai-compatible`, `ollama`) | ✅ Kept |
| **P5a** | App shell + live run (bootstrap, transcript, streaming, statusbar) | ✅ Kept |
| **P5b** | Job panel (composite rows + live log tail) | ✅ Kept |
| **P5c** | First-run wizard + settings | ✅ Kept |
| **P6** | Diagnosis / recovery flow + cost caps + resource monitoring | Partly kept — the AI diagnosis loop went with the orchestrator; `ProcessResourceMonitor` still meters every shell command |
| **P7** | Roles + provider routing (catalog-bound, multi-instance) | ✅ Kept |
| **P7b** | Job output references (`{{job.key}}` between jobs) | Superseded |
| **P8** | Orchestrator feedback loop (plan → execute → report → replan) | Superseded |
| **P8b** | Worker tools (roles declare tools; bounded, metered tool loop) | ✅ Kept — became the turn loop |
| **P9** | Copilot mode (approve before run) | Superseded by the permission gate |
| **P9b** | Copilot gates jobs added mid-goal | Superseded |
