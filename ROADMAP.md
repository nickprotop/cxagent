# cxagent — Roadmap

**cxagent** is a terminal AI coding agent built on [SharpConsoleUI](https://github.com/nickprotop/ConsoleEx),
part of the cx app family.

What is built and what is next. Update it as work lands.

---

## What works

**It is one loop.** Send the conversation; if the reply has no tool calls the turn is over, otherwise
run them, append the results, send again. Everything below hangs off that.

Cancellation that cannot poison a session. Providers: Anthropic native,
`openai-compatible`, `ollama`, several at once and bindable per role. The usual tools — read, write,
edit, glob, grep, shell, http, web fetch — behind a permission gate that knows where the working
boundary is, and that stops asking about commands which can only look. SQLite resume, per-turn
context dumps, per-tool logs. Compaction that summarises rather than truncates, and never cuts a
tool call away from its result. MCP over stdio and HTTP, OAuth included. Sub-agents, usage history,
skills, a plan the model keeps across turns, and a way for it to ask you a question. The UI:
transcript, job rows, session panel, command palette, settings.

**It is also a package.** `CxAgent.Core` ships everything above without a terminal — a session, the
agent running it, tools, sub-agents, permissions, MCP and resumable stores. cxagent is one consumer
of it; `cxagent.Core/examples/SpectreAgent` is another, in about a hundred lines.

**1881 tests**, and every one of those subsystems has also been driven live against a real local
model. The drives keep finding things the tests do not.

---

## Recent work

**Sub-agents.** The agent can hand a job to a worker with its own context and briefing. Several in
one message run at once, and none outlives the turn that started it.

**Usage history.** `/stats` — tokens by project, by agent type, and what actually fills the context.

**Skills.** `SKILL.md` files whose description sits in the prompt while the body loads on demand.

**`CxAgent.Core` extracted.** What the isolated-kernel design called for, shipped as an assembly and a
package: `Session` and `SessionManager` are the API, `AgentHost` is internal, and the composition
root owns no turn lifecycle. Verified as a consumer would — packed to a local feed, referenced from
a clean project with no source access, and run.

**One send point.** Four layers that each took a prompt became two. `Session.Submit` decides,
starts, drains the steer queue and reports; `Agent.SendAsync` is the turn loop children use
directly. It returns a receipt rather than a task, because "a turn began" and "text was queued" are
different things for a caller to do.

**A second front end.** `examples/SpectreAgent` — a prompt, streamed text, one line per tool. Writing
it found two things the TUI never would: tool starts must be announced from `ToolsChanged` rather
than `ToolUpdated`, and the default working mode offered no spawn tool at all.

---

## Next

Ideas, not promises.

**Publish `CxAgent.Core`.** It packs clean and a consumer runs against it; nothing has been pushed to
nuget.org. The version is a default `1.0.0`, which is a claim about stability worth making
deliberately.

**Narrow `SessionManager.Shared`.** It hands out the whole services record — the SQLite stores, the
log manager, the gate. A consumer holding `Shared.Resume` can bypass every command that reads it.
Deliberately deferred: the fix is read-facing accessors rather than live store objects, and that is
its own design.

**A web front end.** The reason the extraction was worth doing. Nothing in the package assumes a
terminal, and the observer contract is the whole seam — a socket implements the same eight methods a
console does.

**Skill scripts.** Reference documents already work — a skill's files come back by absolute path and
the model reads them through the normal gate. Something meant to be *run* is a different question:
that is code from disk nobody here wrote, and the gate covers shell access, not where a command came
from.

**Per-type skill catalogs.** A planner that can load a deployment skill is a planner with a
distraction. Cheap to add, once there is a skill worth withholding.

**Sandboxed shell.** The last thing standing between this loop and running tools in parallel. Two
shell commands in one directory can trip over each other and nothing here would notice.

**Pipes still prompt.** A command that can only look runs without asking — unless it is piped, and
the model pipes constantly (`find . | head`). Allowing a single pipe between two safe verbs needs a
real split-and-check on both sides rather than a substring test, and the failure mode is silent,
which is why it was left out.

---

## Things worth knowing

**Whether the model uses a tool at all is the model's business.** A local `qwen3.6-35b-a3b` loads
skills and keeps a plan — but skills only after the prompt spelled out that reading a `SKILL.md` is
not the same as loading it, and `glob`/`grep` only after they were renamed from `list_files` and
`search_files` to the words it already knew.

`question` (once `ask_user`) is the sharpest example. It was never called once across three drives:
asked something ambiguous, the model listed the options and ended the turn — exactly the failure the
tool exists to prevent. The cause turned out to be the tool's own description, which spent a
paragraph arguing against using it. Rewritten to say what the tool is *for*, the model called it
unprompted on a destructive, ambiguous request. It still guesses on ordinary ambiguity, and still
writes questions as prose when asked to gather requirements. Better than never; not reliable.

A better model may need none of these nudges; a worse one may ignore them all.

**A briefing asks; permissions decide.** "Never edit files" in a type's briefing is a request. It
does not take the tool away.

**No sub-agent outlives its turn.** Every tool call has to be answered in the turn that made it, so
there is nowhere for a background agent to put its result.

---

## Conventions

- **.NET 10** (`net10.0`), nullable + implicit usings. Solution is `cxagent.slnx` — `cxagent.Core`
  (library), `cxagent` (the TUI), `cxagent.Tests`. Examples build standalone and are deliberately
  out of the solution.
- **Core sees no UI.** `CxAgent.Core` references `Microsoft.Data.Sqlite` and `AngleSharp` and
  nothing else. Anything a front end needs — a window, a dispatcher, a console — is a delegate or an
  interface it supplies.
- **Internals are internal**, behind `InternalsVisibleTo("cxagent.Tests")`. Assembling a session is
  a sequence, and half of it leaves one that looks built and answers wrongly — `SessionManager.Open`
  is the way in.
- **SharpConsoleUI reference is conditional** — a local `ProjectReference` to `../../ConsoleEx/…`
  when the sibling repo exists, else a `PackageReference`.
- **Tests first, and make them fail first.** A test that passes before the fix has proven nothing.
  Several bugs here were found only because a test was deliberately sabotaged to check it could fail.
- **Drive it live.** Everything here was driven in tmux against a real model before being called
  done, and several defects were invisible to the suite: a permanently-poisoned session on Escape, a
  worker row that measured a permission prompt as runtime, a model bypassing `load_skill` entirely.
- **Never block on async from the UI thread** — `InstallSynchronizationContext = true` makes that a
  self-deadlock.
- Gitignored: `.claude/`, `CLAUDE.md`, `.agents/`, `.cxagent/`, `bin/`, `obj/`.
