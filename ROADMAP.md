# cxagent — Roadmap

**cxagent** is a terminal AI coding agent built on [SharpConsoleUI](https://github.com/nickprotop/ConsoleEx),
part of the cx app family.

What is built, what is next, and what got thrown away. Update it as work lands.

---

## The DAG is gone

The original plan (P1–P9b, at the bottom) built an orchestrator: an LLM broke a goal into a DAG of
typed jobs, a scheduler ran them by dependency, a feedback loop replanned on failure. All of it
worked. All of it was removed — there is no `Core/Orchestrator/` any more.

What replaced it is one loop. Send the conversation; if the reply has no tool calls the turn is over,
otherwise run them, append the results, send again. The model works out what to do next from what
just happened, which is what the dependency edges were guessing at in advance.

It was not a bad design, just an early one — it fixed the shape of the work before the model had seen
any of it. Worth saying here, because the P-plan tables below still describe it.

---

## What works

The turn loop, with cancellation that cannot poison a session. Providers: Anthropic native,
`openai-compatible`, `ollama`, several at once and bindable per role. The usual tools —
read, write, edit, search, shell, http — behind a permission gate that knows where the working
boundary is. SQLite resume, per-turn context dumps, per-tool logs. Compaction that summarises rather
than truncates, and never cuts a tool call away from its result. MCP over stdio and HTTP, OAuth
included. Sub-agents, usage history, skills. The UI: transcript, job rows, session panel, command
palette, settings.

**1152 tests**, and every one of those subsystems has also been driven live against a real local
model. The drives keep finding things the tests do not.

---

## Recent work

**Sub-agents.** The agent can hand a job to a worker with its own context and briefing. Several in
one message run at once, and none outlives the turn that started it.

**Usage history.** `/stats` — tokens by project, by agent type, and what actually fills the context.

**Skills.** `SKILL.md` files whose description sits in the prompt while the body loads on demand.

**Kernel isolation.** Designed, not built: `isolated-kernel.md` works out how a provider-agnostic
`cxagent.kernel` would talk to a presentation layer, so a web front end is a sibling rather than a
rewrite.

---

## Next

Ideas, not promises.

**Extract `cxagent.kernel`.** The design is written. It buys a web front end and reuse across the
other cx apps, and it costs making every seam honest about what it actually needs.

**Skill scripts.** Reference documents already work — a skill's files come back by absolute path and
the model reads them through the normal gate. Something meant to be *run* is a different question:
that is code from disk nobody here wrote, and the gate covers shell access, not where a command came
from.

**Per-type skill catalogs.** A planner that can load a deployment skill is a planner with a
distraction. Cheap to add, once there is a skill worth withholding.

**Sandboxed shell.** The last thing standing between this loop and running tools in parallel. Two
shell commands in one directory can trip over each other and nothing here would notice.

**One flake nobody has explained.** `AgentChallengeTests.TheModelsRawResponseIsLogged` failed once in
a full-suite run and has never failed since.

---

## Things worth knowing

**Whether the model delegates, or reaches for a skill, is the model's business.** A local
`qwen3.6-35b-a3b` does both — but it only started loading skills after the prompt spelled out that
reading a `SKILL.md` is not the same as loading it. A better model may need neither nudge. A worse
one may ignore both.

**A briefing asks; permissions decide.** "Never edit files" in a type's briefing is a request. It
does not take the tool away.

**No sub-agent outlives its turn.** Every tool call has to be answered in the turn that made it, so
there is nowhere for a background agent to put its result.

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
- Gitignored: `.claude/`, `CLAUDE.md`, `.agents/`, `.cxagent/`, `bin/`, `obj/`.

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
