# cxagent, in use

Every shot below is from one session: a local `qwen3.6-35b-a3b` closing three unfinished features in
[cxlog](https://github.com/nickprotop/cxlog) — wiring `--follow`, `--export` and `--session` to
services that already existed, then running the binary it had just built.

Nothing here is staged. The mistakes are in the pictures too, because they are what the session
actually did.

> These are rendered from `tmux capture-pane` output rather than photographed off a screen, so the
> colours and text are exact and the glyph metrics are the renderer's rather than your terminal's.

---

## Trusting a folder

![Trust](01-trust.png)

The first question of the first session. Reads and writes inside this folder stop asking; anything
that writes elsewhere, and every shell command, still stops.

---

## Sub-agents

![Sub-agents](02-sub-agents.png)

A worker exploring in its own context, with its own turn count and occupancy — the parent stays at
what it was. Note the panel splitting `workers` from `this agent`: children share the parent's
ledger, so without that split a session that spent 86% of its tokens in sub-agents looks identical
to one that spawned nothing.

The prompt below it says **which child asked**. This one wanted to read outside the working folder,
so it stopped — and it was a detour, so it got denied.

---

## Stopping without losing what you typed

![Escape](03-escape.png)

Escape ends the turn. Anything queued goes back into the composer rather than being discarded — it
was never sent, so stopping must not eat it.

---

## Asking the user

![Question, step 1](04-question-step-1.png)

The model needed three decisions that were mine to make. It gets one call, up to four questions,
presented as steps.

![Question, step 3](05-question-step-3.png)

Every option carries a description saying what it means. A list of bare labels asks the user to guess
what the model was thinking. `Alt+←` appears from step 2 — an answer can be reconsidered while
reading the next question.

![Summary](06-question-summary.png)

Then every answer under its header before any of it is sent. Above the summary is what happened
first: the model wrote its three questions as *prose*, in a message nobody was listening for a reply
to — the exact failure the tool exists to prevent — and had to be told to ask properly. The table it
rendered is its own markdown.

---

## MCP

![MCP](07-mcp.png)

It had been digging through NuGet packages on disk looking for SharpConsoleUI's API. Told to use
context7 instead, it resolved the real documentation site. External servers ask separately from local
commands.

---

## Running what it built

![Running the build](08-running-the-build.png)

A feature that compiles but was never executed is not finished. cxlog is a full-screen TUI, so it
cannot run from a captured shell — told to use tmux, the agent adapted its own invocation across
three failures and got `Exported to /tmp/out.json`.

Failed tool calls render red, and stay in the transcript. The crash it hit on the first run turned
out to be a pre-existing bug in cxlog, not its own.

---

## Reviewing the work

![Diff](09-diff.png)

`/diff` is `git diff`, in the transcript. The app edits files without asking inside the working
folder, so the review step should not require another terminal. Long diffs are capped and say what
was elided.

![Untracked](10-diff-untracked.png)

`git diff` exits 0 with no output for a file it has never seen — so a brand-new file, which is what
an agent spends its time creating, would report as unchanged. Untracked files are named instead.

---

## Sessions

![Sessions and resume](11-sessions-and-resume.png)

This session died when the agent's own `tmux kill-session` took down the server it was running in.
Nothing marked it finished, so it stayed resumable: `cxagent --resume QQEQXA` restored 165 messages.

The question above the listing is the check that matters — *without re-reading anything*, which gaps,
which format, what crash. It answered all three from memory, with the stack trace and the root cause
it had diagnosed, and spent no tool calls doing it.
