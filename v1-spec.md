# Towards v1 — sessions, `/init`, `/diff`

Three features that answer the same complaint: **the app knows things the user cannot get at.** It
has every conversation you have had in this folder and offers you one of them, once, at the wrong
moment. It reads a project instruction file on every turn and gives you no way to write one. It
edits your code and shows you a row saying `replace_in_file … done`.

---

## 0. Decisions

| # | Decision | Where |
|---|---|---|
| S1 | **The auto-offer goes.** Replaced by one system line at startup: `3 earlier sessions here · /sessions`. Information, not a decision. | §2 |
| S2 | **The UID is the identity; the list number is a convenience.** Short prefixes, git-style, with unambiguous-prefix matching and ambiguity reported rather than guessed. | §3 |
| S3 | **`--sessions` prints and exits**, so `cxagent --sessions \| grep` works. | §4 |
| S4 | **`--resume` bare resumes the newest here; `--resume <uid>` resumes exactly that one.** The second is why UIDs must be first-class. | §4 |
| S5 | **`/exit` prints the way back** — the UID and the command. The one moment the app knows something the user is about to lose. | §5 |
| S6 | **`finished` stops gating the LIST, and keeps gating RESUME-BY-DEFAULT.** It carries two meanings — ended cleanly, and superseded by a resume — and only the first is safe to ignore. | §6 |
| S7 | **The first user message is the title**, derived from the stored context rather than a new column. Free, already there, and how a human recognises a conversation. | §3 |
| S8 | **Cross-folder resume is allowed and announced.** The user named a specific session; refusing would be second-guessing them. | §4 |
| S9 | **`/init` edits the file that already governs** — whichever `ProjectInstructions` would pick. It never creates a second file beside an existing one. | §7 |
| S10 | **`/init` merges, never appends.** A file that reads as two documents stapled together is one nobody maintains. | §8 |
| D1 | **`/diff` is `git diff`, rendered.** No snapshotting of our own: git already has the baseline, and a second one would disagree with it. Outside a repo the command says so plainly. | §9 |
| D2 | **It shows the WORKING TREE, not "what the agent did"** — including the user's own uncommitted edits. Honest, and the only thing answerable without tracking we do not have. | §9 |
| D3 | **A fenced `diff` block**, so the transcript's markdown gives colouring for free. An uncoloured diff is markedly harder to scan. | §9 |
| D4 | **Capped, and the cut is stated.** A 5,000-line diff is unreadable in a transcript and expensive if it ever reaches the model. | §9 |
| D5 | **The COMMAND is for the user, not the model.** Whether the agent should be able to diff its own work is a separate question, and a bigger one. | §9 |

---

## 1. What is wrong today

**The auto-offer asks the wrong question at the wrong moment.** You have opened a terminal to do
something. Before you type a word, the app interrupts with a decision about *last time* — and all it
can tell you is a size and an age, because summarising a conversation would cost a model call.

**It knows about exactly one.** `SqliteSessionStore.LoadLatestUnfinished` is the entire read API.
Crash twice in a folder and the older session is not hidden, it is **unreachable** — no command in
the app can name it.

**Observed, not theorised:** during a live drive in this project the resume offer fired, the session
restored silently, and the run continued against a context from a previous experiment. The measured
result was invalid and was nearly reported as real.

---

## 2. Startup

One system message, when there is something to say:

```
3 earlier sessions here · /sessions
```

**NO PROMPT, NO KEYPRESS.** It costs nothing to ignore, which is the entire difference from what it
replaces. A user who wants their session types six characters; a user who does not is already
typing what they came to type.

Absent entirely when the folder has no history — a line saying "0 sessions" is noise on every first
run in every new project.

---

## 3. `/sessions`

```
Sessions · /home/nick/source/cxagent

  1  7f3a2b   2h ago      18 turns ·  214k   add the arity table
  2  01kzwz   yesterday    6 turns ·   31k   why does /mode not show…
  3  9c4e10   3d ago      41 turns ·  1.2M   implement skills

  /sessions resume 1 · /sessions resume 7f3a · /sessions all
```

**THE TITLE IS THE FIRST USER MESSAGE**, clipped. It is the only column a human actually recognises a
conversation by — a ULID identifies without describing, and a size and an age describe without
identifying.

**IT IS ALREADY STORED, INSIDE `context_json`** — verified: `LoadLatestUnfinished` deserialises that
column into the full message list. So no schema change is strictly required, and that is the cheap
version.

**BUT THE LIST SHOULD NOT DESERIALISE EVERY SESSION'S WHOLE CONVERSATION** to render one line each.
A `title` column, added by the existing `AddColumnIfMissing` path that `working_dir` already uses,
is written once when a session takes its first user message and read directly thereafter. Rows that
predate the column fall back to deriving it — one deserialise for old rows, none for new ones.

**TWO WAYS TO NAME ONE, AND THEY ARE DIFFERENT PROMISES.** The number is a property of the listing
you are looking at now — renumbered on every `/sessions`, meaningless in a script, perfect at a
prompt. The UID is the session itself: stable, quotable in a bug report, and the only thing that
works from the command line. Offering both is not redundancy, it is two different needs.

**PREFIXES MATCH LIKE GIT.** Six characters shown, any unambiguous prefix accepted. An ambiguous one
is REPORTED rather than resolved to the newest — silently picking is how a user restores the wrong
conversation and does not find out for ten minutes.

**FOLDER-SCOPED BY DEFAULT; `all` widens it** and gains a folder column. Listing across folders is
safe. Restoring across them is the case S8 covers.

---

## 4. Command line

```
cxagent --sessions            list, print to stdout, exit
cxagent --resume              the newest session in this folder
cxagent --resume 7f3a         that one, wherever it was recorded
```

**`--sessions` PRINTS AND EXITS**, no TUI. It is the difference between a feature and a scriptable
one: `cxagent --sessions | grep skills` is a thing people expect to work, and it cannot if the list
lives inside a window.

**`--resume` BARE IS THE OLD AUTO-OFFER, MADE OPT-IN.** The behaviour was not wrong, the timing was —
a user who types it has asked for exactly what the prompt used to guess at.

**`--resume <uid>` IS WHY UIDS EXIST.** A number from a listing means nothing here, and this is the
path a user reaches for after reading the exit hint. It is also the one that survives a reboot, a
different terminal, and a note written on paper.

**A CROSS-FOLDER RESUME IS ANNOUNCED, NOT REFUSED** (S8):

> This session was recorded in `/home/nick/source/cxlog`.

The context will be full of that project's paths. The user named a specific session, so refusing
would be second-guessing them — but restoring one silently is how a conversation about someone
else's code appears with no explanation.

---

## 5. Exit

```
Session 7f3a2b · 18 turns · 214k tokens
Resume:  cxagent --resume 7f3a2b
```

**THE ONE MOMENT THE APP KNOWS SOMETHING THE USER IS ABOUT TO LOSE.** It is also the only place a
flag like `--resume` gets discovered: nobody reads `--help` for a feature they do not know exists.

Shown on `/exit` and on a clean shutdown. Not on a crash — there is nowhere to print it, which is
precisely why `/sessions` exists.

---

## 6. `finished` — two meanings, only one of them safe to drop

An earlier draft of this section said `finished` should stop gating what can be reached, on the
reasoning that a session you ended cleanly is no less resumable than one you crashed out of. Reading
the callers shows that is **half right and half dangerous.**

`MarkFinished` is called from three places, and only one of them is `/exit`:

| Caller | Means |
|---|---|
| `AgentHost.MarkSessionFinished` via `/exit` | the user ended this session |
| `AppBootstrap` after accepting a resume (twice) | **this row has been superseded** |

That second meaning is load-bearing. A resumed session is a NEW agent with a new id writing its own
rows; the row it came from is retired precisely so the same context cannot be accepted twice and
**fork the conversation into two sessions claiming one history.**

So the rule splits:

- **The LIST shows everything**, finished or not, with the state as a column. Why a session ended is
  worth seeing; it is not a reason to hide it.
- **`--resume` with no uid still skips finished rows.** "Give me the newest one here" should not hand
  back a session that was superseded ten minutes ago.
- **`--resume <uid>` honours the uid.** The user named that session. If it was superseded, say so —
  *"this session was resumed as 9c4e10"* — and let them decide; a fork they asked for by uid is a
  choice, a fork they got by accident is a bug.

The index built for the old behaviour, `(working_dir, finished, updated_at)`, still serves this: the
default read keeps all three predicates, and the list drops the middle one.

**PRUNING NEEDS A DECISION, AND SILENCE IS THE WRONG ONE.** `Prune` deletes finished rows past a
retention window, on the reasoning that a cleanly-ended session is a buffer nobody needs. Once those
rows are LISTED that reasoning weakens: the user can see them, so deleting them silently is deleting
something they were shown.

Keep a window — unbounded history is its own problem — and **state it in `/sessions`**:
`older than 30 days are removed`. A user who wants permanence has git; a user who loses a month of
conversations to a policy nobody mentioned has a grievance.

The superseded rows are the exception worth noting: they are duplicates of a conversation that lives
on under a newer id, so pruning them loses nothing. If the list ever feels cluttered by them, hiding
superseded rows is a better answer than shortening the window.

---

## 7. `/init` — which file it writes

`/init` writes the project instruction file that the agent then reads on every turn. It is the
bootstrap step: how a repo teaches cxagent its conventions.

**IT EDITS THE FILE THAT ALREADY GOVERNS**, which is whichever one `ProjectInstructions` would pick
(`ProjectFileNames = ["CXAGENT.md", "AGENTS.md", "CLAUDE.md"]`, first match wins):

| On disk | `/init` writes |
|---|---|
| nothing | a fresh `CXAGENT.md` |
| `CXAGENT.md` | into `CXAGENT.md` |
| `AGENTS.md` only | into **`AGENTS.md`** — no second file |
| both | into `CXAGENT.md`, the resolver's own winner |

**NEVER A SECOND FILE BESIDE AN EXISTING ONE.** Writing `CXAGENT.md` next to an `AGENTS.md` produces
two near-identical documents, one of which will rot — and the repo already committed to the
vendor-neutral name. Improving `AGENTS.md` in place benefits every agent that reads the repo, not
only this one.

**`CLAUDE.md` IS READ, NEVER WRITTEN.** It is third in the resolver so a repo carrying only that one
still works. Seeding *from* it would mean copying another product's instructions into a file we
maintain; honouring it when it is all that exists is a courtesy, treating it as our source of truth
is not.

**IF SOMETHING CXAGENT-SPECIFIC NEEDS SAYING** — a rule only this app honours — that is the moment to
SUGGEST a `CXAGENT.md`, and to suggest rather than create. Everything `/init` normally writes (build
and test commands, house rules, project conventions) is useful to any agent and belongs in the shared
file.

---

## 8. `/init` — what it writes

**A TURN, NOT A COMMAND.** Unlike `/mode` or `/skills` this costs tokens and takes time: the agent
explores the project — build files, README, test layout, an existing instruction file — and writes
what it found. It shows as an ordinary turn, with its plan and its file write visible, and the write
goes through the permission gate like any other.

**MERGED, NOT APPENDED** (S10). An existing file is the user's work and their words. The instruction
is to preserve what is there, add only what is genuinely missing, and never restate in different
words something the file already says — a document that contradicts itself in two registers is worse
than one that is merely incomplete.

**WHAT IS WORTH WRITING is what is not discoverable.** "This is a .NET project" is visible from the
directory listing and helps nobody. The file earns its place with the test command that actually
works, the convention that looks arbitrary until explained, the thing that was tried and failed.
This repository's own `CXAGENT.md` is the model: every line is something a newcomer would otherwise
get wrong.

**NEVER SILENTLY OVERWRITE.** The destructive path must not be the default. A merge that cannot be
made safely stops and says so.

---

## 9. `/diff`

The agent edits your code and the only evidence is a row reading `replace_in_file … done`. You
either trust it or open another terminal. Every comparable tool shows the diff; this one has the
plumbing already — `SessionPanel` shells out to `git status --porcelain` with a 500ms timeout, and
the same shape works here.

```
/diff              everything uncommitted
/diff <path>       one file
/diff --staged     for people who stage as they go
```

Rendered as a fenced `diff` block in a transcript row, expanded, the same treatment the plan row
gets — the content IS the point of the row, and collapsing it behind `expand…` hides what was just
asked for.

**IT IS `git diff`, NOT OUR OWN RECORD (D1).** Snapshotting files ourselves would mean a second
baseline that disagrees with git's the moment anyone stages, stashes or checks out — two answers to
one question, and the wrong one wins whenever the user believes it. Git already knows.

**SO IT SHOWS THE WORKING TREE, INCLUDING YOUR OWN EDITS (D2).** That is a real limitation and worth
stating rather than papering over: `/diff` answers *"what does my repo look like now"*, not *"what
did the agent just do"*. The second question is the one you actually have after a long turn, and
nothing can answer it today — `IsWrite` tells the loop whether a turn wrote anything, as a boolean,
with no paths. See §10.

**OUTSIDE A REPO IT SAYS SO.** `git diff` simply fails there, and "not a git repository" is the
honest answer; anything else would mean building the baseline this section just declined to build.

**CAPPED, WITH THE CUT STATED (D4).** A 5,000-line diff is unreadable in a transcript. Truncate and
say what was elided — `… 31 more files` — because a diff that silently stops is one a user will
read as complete.

**THE COMMAND IS FOR THE USER (D5).** Like `/skills` and `/stats` it is handled before the model sees
anything and costs no tokens.

**WHETHER THE MODEL SHOULD HAVE ITS OWN `git_diff` TOOL IS A SEPARATE, LARGER QUESTION.** It is
arguably the more valuable feature — this codebase has a measured history of the model reporting
success it had not verified — but it is a tool, with a permission story and a context cost, not a
command. Not decided here.

---

## 10. What `/diff` does not answer

**"What did this turn change?"** — the question a user has after watching forty tool calls scroll
past. `/diff` cannot answer it, because the working tree contains their edits too.

Answering it needs the loop to record PATHS rather than a boolean, which is a small change with a
real payoff: a per-turn line reading `3 files · +42 −7`, and a `/diff` that could then be scoped to
what the agent touched.

**Not in this spec**, deliberately. It changes the turn loop, and `/diff` is useful without it —
shipping the cheap honest version first is how the limitation gets measured rather than guessed at.

---

## 11. Steps

Each ends with something usable.

| # | Step | Gate |
|---|---|---|
| 1 | Store: title column (via `AddColumnIfMissing`, as `working_dir` already does), `List(workingDir, all)`, `LoadByUid(prefix)` | Prefix matching: exact, unambiguous-prefix, ambiguous-reported, no-match. A pre-existing database gains the column without losing rows |
| 2 | `/sessions` — list, `resume <n\|uid>`, `all` | Renders with title and short uid; resuming by number and by prefix reach the same session |
| 3 | CLI: `--sessions` (print and exit), `--resume [uid]` | `--sessions` writes to stdout and returns 0 with no window; a bad uid exits non-zero and names the problem |
| 4 | Remove the auto-offer; add the startup line and the exit hint | Startup prompts nothing; the hint names a uid that `--resume` then accepts — the round trip, not the string |
| 5 | Pruning window stated in `/sessions`; `finished` stops gating the LIST but still gates `--resume` with no uid | A finished session appears in the list; bare `--resume` skips it; `--resume <uid>` reaches it and says it was superseded. **The fork case is the one to test**: resuming a superseded row must not produce two sessions claiming one history |
| 6 | `/diff`, `/diff <path>`, `/diff --staged` | Outside a repo it says so rather than erroring; a diff past the cap states what was elided; the fence is `diff` so colouring lands |
| 7 | `/init` | Each of the four file cases in §7; an existing file's own lines survive a merge |

**Steps 6 and 7 were one step and should not be.** `/diff` is a shell-out and a fence — an
afternoon. `/init` is a model turn with a merge strategy. They share nothing, and bundling them
means neither ships until both are done. `/diff` is also the one a user notices first.

### Four things the plan surfaced that the spec had glossed

**THE RETENTION WINDOW IS 7 DAYS, NOT 30.** `SqliteSessionStore.DefaultRetention` is
`TimeSpan.FromDays(7)`, and §6's example text says thirty. Seven is defensible for an invisible
buffer and short for something a user can now SEE listed — a session from last Tuesday is simply
gone. Raising it is a behaviour change and belongs in step 5 as a decision, not as a silent edit
while writing the message that mentions it.

**`--resume` NEEDS THREE STATES, NOT TWO.** Absent, present-with-no-uid, present-with-a-uid. A
`string?` cannot express the middle one: null already means "not passed". `bool Resume` plus
`string? ResumeUid`, or the middle state silently becomes "resume nothing".

**`--sessions` RUNS BEFORE THERE IS A WINDOW.** Printing and exiting means a path in
`AppBootstrap.Run` that executes before `ConsoleWindowSystem` is constructed — the `options.Error`
branch already does exactly this, so there is precedent, but the session store is currently built
AFTER the window and will have to move up.

**THE LIST MUST NOT DESERIALISE EVERY CONVERSATION.** `SessionInfo` is a new record — uid, title,
turns, tokens, updated-at, folder, finished — deliberately not `SessionSnapshot`, which carries the
full message list. Rendering ten rows should not cost ten context deserialisations.

**Step 4 is the one to drive live.** The exit hint promising a command that then fails is worse than
no hint at all, and only a real run proves the uid printed is the uid accepted.
