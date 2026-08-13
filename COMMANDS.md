# Commands

Typed into the composer, like a message. They are handled by the app before the model sees anything,
so they cost nothing — no request, no tokens.

**Type `/` and the list appears.** Arrow to a command, Enter to fill it in. A command that takes
arguments shows them as a hint — `/stats  usage: …  [<days>|all|clear]` — and typing a space
descends into them: `/mcp ` offers `reload`, `login` and `<server>`, `/mcp re` narrows to `reload`,
Enter completes the whole thing. `<angle brackets>` mark a value you supply, so those rows are shown
but not completed.

| Command | What it does |
|---|---|
| `/help` | Keys and commands |
| `/mode` | Show how this session works |
| `/mode agent single` · `/mode agent fan-out` | Set delegation, live |
| `/clear` | Wipe the conversation |
| `/compress` | Summarise the conversation to free room |
| `/stats` | Usage: tokens, projects, agent types, what fills the context |
| `/stats 30` · `/stats all` | Widen the window (default: 7 days) |
| `/stats clear` | Delete all usage history, after confirming |
| `/mcp` | List MCP servers, or inspect one |
| `/mcp reload` | Re-read config and reconnect |
| `/mcp login <server>` | Authorise a server that needs OAuth |
| `/skills` | List available skills, and any `SKILL.md` that was skipped |
| `/init` | Write the project instruction file this agent reads each session |
| `/diff` | What has changed in the working tree |
| `/diff --staged` · `/diff <path>` | Narrow it to the index, or to one file |
| `/sessions` | Earlier conversations in this folder |
| `/sessions resume <n\|id>` | Restore one, by its number in the list or its id |
| `/sessions all` | Every folder, not just this one |
| `/exit` | Quit |

---

## `/mode`

```
/mode                    Working mode

                           agent  fan-out
                             can spawn sub-agents

                           set with /mode agent single | fan-out

/mode agent single       agent: single — this agent works alone; the spawn tool is
                         withdrawn. The conversation is unchanged.
/mode agent fan-out      agent: fan-out — this agent can now spawn sub-agents.
```

**The axis is named because there will be more than one.** Delegation is one way a session can be
set up; file editing and a build/plan mode are coming, and each would otherwise have wanted a
command of its own — three entries in the palette where one will do, with no single place showing
the whole picture. A bare `/mode` reports every axis for that reason.

`/mode fan-out` without the axis still works. Agent is the only axis today, so naming it is ceremony
for the one thing anyone is switching — the day a value means something on two axes is the day that
value stops being unambiguous on its own.

**Fan-out is the default.** Single mode withdraws the spawn tool and removes the sub-agent guidance
from the system prompt — its prompt is what shipped before sub-agents existed, so turning delegation
off really does turn it off.

**The conversation survives a switch.** Only the system message is rewritten; everything you and the
agent have said is untouched. History is not rewritten either — a `spawn_agent` call made in fan-out
mode stays visible after switching to single. Erasing it to match current capability would
misrepresent what happened.

**Declined while a turn is running**, with *"a turn is running — press Escape to stop it first."*
The tool list is fixed once a request begins, deliberately, so a tool cannot appear or vanish between
two turns of one request and leave the model chasing something that is gone. Asking `/mode` with no
argument still works mid-turn: it reads nothing and changes nothing.

Setting the mode you are already in says so and changes nothing. An unrecognised value names the
valid ones.

---

## `/stats`

What this installation has actually done, as a dashboard in the transcript. Seven days by default;
`/stats 30` or `/stats all` widens it.

```
Usage · last 7 days

  2,023,023 tokens  ↑2.0M ↓40.9k
  4 sessions  · 28 turns

  █████████████████───── 79% to workers  (1.6M of 2.0M)
```

**The worker share is the number fan-out users want.** Children share the parent's ledger and usually
its model, so nothing else distinguishes their spend — before this existed, a session that sent 86%
of its tokens to sub-agents looked identical to one that spawned nothing.

**"What fills the context" is the section that explains an expensive session.** Tools are ranked by
characters returned, not by call count, because a turn re-sends everything before it: a tool that
returns 11k characters fifty times does not cost that once, it costs it again on every later turn.
Forty cheap calls are not the problem; one large result repeated is.

**"By agent type" needs history, not a session.** Whether 41k is typical for a `planner` or an
outlier is a question about many runs. `capped` is counted apart from `failed` — a run that exhausted
its turn cap did not fail, it ran out of room, which is a fact about the briefing rather than the
work.

A separate database from resume (`history.db` beside `cxagent.db`) and **never pruned**: the resume
store is a buffer worth nothing once a session ends cleanly, this is the archive. Recording is
best-effort throughout — a locked file costs statistics and never a session.

**Nothing is recorded before this version.** A fresh install says so rather than showing an empty
dashboard, which would read as "you have done nothing".

---

## `/compress`

Summarises the conversation through the model to free room, rather than dropping the oldest half.
The compaction shows as a job row with its own spinner and an expandable summary.

**Declined while a turn is running.** It measures and rewrites a context that is actively changing —
running it later is a different operation from the one you asked for, and running it now would tear
the list the agent is appending tool results to. Nothing is lost by refusing: compaction also happens
automatically on measured pressure, so this costs a keystroke rather than a compaction.

---

## `/clear`

Clears the agent's context — the messages the model is sent on every turn. The transcript on screen
is left alone; it is your record, not the model's memory.

---

## `/mcp`

With no argument, lists configured servers and their state. A server that failed to start is shown
with the reason, because a server that silently never appears is indistinguishable from one you never
configured.

`/mcp reload` re-reads `config.json` from disk and reconnects — adding a server does not need a
restart. `/mcp login <server>` runs the OAuth flow for a server that returned 401, opening a browser
and storing the token at `0600`, never in the config file.

---

## `/skills`

Lists what was found, which directory it came from, and — the reason this command exists — **every
`SKILL.md` that was skipped, with why**. A file with broken frontmatter is invisible to the model and
nothing else in the app would ever mention it: you wrote a file, nothing happened, and there is no
error anywhere.

It lists; it does not load. The model decides what a task needs, and loading one here would spend
context on a document nothing had asked for.

It is also **not a refresh.** Skills are re-read from disk every turn, so a skill you just edited is
already live on the next one — there is nothing to apply.

See [CONFIG.md](CONFIG.md#skills) for where skills live and what a `SKILL.md` looks like.

---

## `/init`

Writes the project instruction file the agent reads at the start of every session here — the
bootstrap step, and the one place a human writes down what looking around cannot tell you.

**It is a turn, not a command.** Unlike everything else in this file it costs tokens and takes time:
the agent explores the project, then writes what it found. Its tool calls are visible and its file
write goes through the permission gate like any other.

**It edits the file that already governs**, whichever one the resolver would pick:

| On disk | `/init` writes |
|---|---|
| nothing | a new `CXAGENT.md` |
| `CXAGENT.md` | into `CXAGENT.md` |
| `AGENTS.md` only | into **`AGENTS.md`** — no second file |
| both | into `CXAGENT.md`, the resolver's own winner |
| `CLAUDE.md` only | a new `CXAGENT.md`, and it says why |

Writing a `CXAGENT.md` beside an existing `AGENTS.md` would produce two near-identical documents,
one of which rots — and improving `AGENTS.md` in place benefits every agent that reads the repo, not
only this one. **`CLAUDE.md` is read but never written**: honouring it when it is all there is is a
courtesy, and treating another product's file as ours to edit is not.

**An existing file is merged, never appended to or rewritten.** It is your work and your words: the
instruction is to preserve what is there, add only what is genuinely missing, and stop rather than
overwrite if a safe merge is not possible.

**What it is asked to write is what is not discoverable.** "This is a .NET project" is visible from
a directory listing and helps nobody. The commands that actually work, the architecture that takes
several files to see, the convention that looks arbitrary until explained, and the thing that was
tried and abandoned — those earn their place.

---

## `/diff`

The review step, in the transcript.

```
Diff · uncommitted · 1 file · +2 −1

diff --git a/a.txt b/a.txt
@@ -1,3 +1,4 @@
 one
-two
+TWO CHANGED
 three
+four
```

The app writes files without asking inside the working folder, and the README says plainly that
`git diff` is how you check that. Which meant the one action every user has to perform after every
session was the one thing the app could not show them — you either trusted it or opened another
terminal.

**It is `git diff`, not a record of our own.** Snapshotting files ourselves would create a second
baseline that disagrees with git's: it would miss an edit made in another window, and would show as
changed a file you had since reverted. Deferring to git means the answer matches the tool you will
check it against.

**An empty diff is not always "nothing changed".** `git diff` exits 0 with no output both for a path
that does not exist and for a file git has never seen — so a brand-new file, which is what an agent
spends its time creating, would otherwise report as no change. Untracked files are named instead,
and a path that is not there is said to be missing.

**Capped at 400 lines, and the cut is stated.** A diff that silently stops is one you read as
complete, and "everything after this is fine" is the worst thing to imply by accident.

Outside a git repository it says so rather than passing git's message about discovery and ownership
along, which reads as a bug in this app rather than the plain fact that there is nothing to diff.

**This is for you, not the model.** Whether the agent should be able to diff its own work is a
separate and larger question; this is a command you type, and its output goes to the transcript
rather than into the conversation.

---

## `/sessions`

Every conversation recorded in this folder, and a way back into one.

```
Sessions · 3 here

   1  6QC33Q  just now     214k  rename list_files to glob everywhere
   2  5PSCPG  4m ago        31k  why does /mode not show the file axis yet
   3  CV5TAC  yesterday     18k  add the arity table to the permissions doc

  /sessions resume <number|id>  ·  /sessions all
  sessions closed cleanly are removed after 30 days
```

**A session is named twice, because the two names are different promises.** The number belongs to
the listing on screen — renumbered every time, useless in a script, ideal at a prompt. The id is the
session itself: stable, quotable, and the form that works from the command line. `/sessions resume `
offers the numbered list inline, so the usual case is two keystrokes.

**The id shown is the first six characters, like a git hash.** That works because session ids put
their randomness first and their timestamp last — the reverse of a ULID. A timestamp-first id makes
every session started in the same few minutes read as the same six characters: three sessions from
one afternoon all showed as `01KZXC`, an identifier that identified nothing. Ids created before this
change still have their random half at the end, so typing either end of an id matches.

**An ambiguous id is reported, never resolved.** Two matches name both rather than picking the
newer: silently restoring the wrong conversation is not something you find out about quickly.

**Refused while a turn is running.** Restoring replaces the agent the running turn is writing into.

Sessions are listed for the folder you are in — `/sessions all` widens it, and adds the folder to
each row. **The retention window is stated in the output** because these rows are deleted on a
schedule, and a policy nobody mentioned is one you discover by losing something.

**Three things can happen to a session, and only one of them is pruned.**

| | offered by bare `--resume` | pruned |
|---|---|---|
| still open (crashed, killed) | yes | **never** — it is the only thing here that cannot be reconstructed |
| closed cleanly (`/exit`) | no | after 30 days |
| superseded (you resumed it) | no | **never** |

A superseded session is a live conversation somebody continued: its successor was built on it, so
deleting it would drop the history behind work that is still going, and a long chain of resumes
would age out from its tail one link at a time. It is hidden from bare `--resume` for a different
reason — accepting the same context twice forks one history into two sessions claiming it — and
stays listed and reachable by id.

Thirty days rather than the week this started with. Seven was right when these rows were an
invisible crash buffer, where nothing was lost by dropping one because nobody could name it. They
are now a listing you read and an id you resume by, which makes the question *how far back would
someone look* rather than *how long until a crash is stale*.

---

## `/exit`

Quits, and marks the session finished — so bare `--resume`, which continues the most recent
*unfinished* session, skips it. It is still listed by `/sessions` and still opens by id.

**On the way out it prints how to come back:**

```
Resume this session:  cxagent --resume 5ZFAVZ
```

That is the one moment the id is worth something — everywhere else it is an implementation detail,
and here it turns "I closed that by accident" into a command you can paste. It is skipped for a
session where nothing was said, since nothing was stored to come back to.

A session that ends any other way — a crash, a kill — stays **unfinished**, which is what bare
`--resume` looks for and what the startup line mentions by name.

**Nothing is offered on startup.** Earlier versions asked *"an earlier session ended without closing
— resume it?"* on the first render, before you had typed anything. It asked at the worst moment,
could only ever offer one session, and made resume something that happened *to* you. Now a grey line
says what is there:

```
An earlier session here ended without closing (13 messages). /sessions to see it — 4 in this folder.
```

---

## From the shell

Three flags that concern sessions, alongside `--mock` and `--mode <single|fan-out>`.

```bash
cxagent --sessions              # print this folder's sessions and exit
cxagent --sessions all          # every folder
cxagent --resume                # continue the most recent unfinished session here
cxagent --resume 5ZFAVZ         # continue that one, by id or any unambiguous abbreviation
```

**`--sessions` prints and exits — no TUI, no provider, no turn.** "Which conversations do I have
here" is a question you answer by looking, and making someone launch a full-screen app to read a list
is the kind of friction that stops people looking. Output is tab-separated with the **full** id, so
`cxagent --sessions | cut -f1` does what you expect:

```
5ZFAVZHZYPHHAWC501KZXEBFY1	2026-08-13 14:33	18213	add the arity table
```

**`--resume` has three states, and they are different requests.** Absent starts fresh; bare
continues the most recent unfinished session *in this folder*, which is the one the app would have
offered you; with an id continues that one specifically. An id is **not** folder-scoped — copying one
out of `--sessions all` and pasting it here opens it, because naming a session is an explicit act.

**A resume that finds nothing still starts.** It says which of the two things went wrong — nothing to
resume, or an id that matched several — and begins a new session rather than refusing to launch. An
unnoticed fresh start is how someone spends a turn wondering why the agent forgot everything.

`--sessions` and `--resume` together is an error: one asks a question, the other starts work, and
doing half of what was typed leaves no way to tell which half you got.

---

## Not commands

**Escape** stops a running turn. Anything queued goes back into the composer rather than being
discarded — it was never sent, so stopping must not eat what you typed.

**Enter during a running turn** queues the message rather than starting a second one. Several queued
messages are joined into one prompt, newline-separated, and sent when the turn ends. They are
appended rather than replaced: two messages typed in succession are usually one thought completed,
and keeping only the last would silently discard half of what you said.

**A question from the model** takes over the composer, one question at a time. `↑↓` and `Enter`
choose an option — the first is already highlighted, so a recommended answer is one keypress — or
type your own. `Space` checks when several answers are allowed. With more than one question you get
a step indicator, `Alt+←` to go back, and a summary of every answer before any of it is sent.
`Esc` skips: answers already given are kept, and a skipped question tells the model to decide.

**F3** cycles the session panel — shown, hidden, automatic. It carries context occupancy, spend,
session id, MCP servers, configured agent types, and granted permissions. Spend appears per model
when more than one model has been used.
