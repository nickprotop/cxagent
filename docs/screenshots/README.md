# cxagent, in use

Real sessions, driven by a local `qwen3.6-35b-a3b` (an IQ4_XS quantisation, served by llama.cpp).
Where a capture used a different model, its caption says so. The runs are at two repositories:
[cxgpu](https://github.com/nickprotop/cxgpu), a GPU monitor for the terminal written in C#, and
`invoice`, a small Python repository with a bulk discount, a branch that broke it and a test file.

Every session was served with `--web`, so wherever the browser has the same feature there are two
tabs above the picture: **Terminal** and **Browser**, the same session at the same moment. The few
steps that only the terminal has are a single picture. These demo runs had cxagent's own sandbox
switched off, which is why the browser's mode line reads `unsandboxed`.

Nothing here is staged. The mistakes are in the pictures too, because they are what the runs
actually did.

---

## Trusting a folder

![The first session in cxgpu: the trust question for /home/dev/source/cxgpu under the logo, with Don't trust selected above Trust this folder](01-trust.png "Terminal")
![The same question in the browser: Trust this folder? for /home/dev/source/cxgpu with Trust this folder and Don't trust buttons, above an empty conversation](01-trust-browser.png "Browser")

The first question of the first session, asked on both screens at once — either can answer it.
Reads and read-only commands inside this folder stop asking, and writes inside it go through without
asking in accept-edits mode. Commands that can write, and anything outside the folder, still ask.

---

## Sub-agents

![The lead's conversation after "Put a team on this": explore, review and test workers in the transcript with a table of them, the test worker done, and the Tasks column with the explorer and reviewer still running and the test run finished](37-background-agents.png "Terminal")
![The same moment in the browser: the three worker rows, the explorer and reviewer working in the background and the test worker done, and the Tasks tab listing all three with Open and Stop](37-background-agents-browser.png "Browser")

cxgpu, asked to put a team on it: one worker to map the alert engine, one to review the last commit,
one to run the tests. A sub-agent runs in the background by default: the spawn answers at once, the
conversation carries on, and each report arrives as a message when it is done — here the test run's,
first. `F7` turns the right-hand column into the tasks panel: what is running, with its clock and a
`⏹`, then a Finished section. The browser's Tasks tab lists the same workers with `Open` and `Stop`.

![The reviewer's own tab after it finished: its name, model, done after 3:52, and the end of its report — one line changed in cxgpu.csproj, the build and 230 tests passing, and its assessment](36-agent-tab.png "Terminal")
![The same worker opened in the browser: its header with Back to session, and the same report rendered as headings — what changed, verification and assessment](36-agent-tab-browser.png "Browser")

`View transcript ›` on a card opens that agent in a tab of its own: the brief it was given, each step
and its tool calls, and its answer as it is written. This one is the reviewer, just after it
finished. While an agent runs, its tab has `Stop`, and a foreground one also gets `Background`, which
hands it off so the parent's turn goes on — the same as `Ctrl+B`. The composer under it talks to the
agent, not the session. Closing the tab leaves the agent where it was.

---

## Stopping without losing what you typed

![A long answer about tax and edge cases cut off by Stopped., with the queued line "and end with a list of open questions" back in the composer](03-escape.png)

Escape ends the turn. Anything queued goes back into the composer rather than being discarded — it
was never sent, so stopping must not eat it.

---

## Asking the user

![The model's first ask_user call open in the composer: question 1 of 3, Discount size, with 5%, 10% and Tiered, each described](04-question-step-1.png "Terminal")
![The browser showing the same call as one form: all three questions — discount size, stacking and qualification — with their options, a free-text box under each, and Answer and Skip](04-question-step-1-browser.png "Browser")

In `invoice`, asked for a loyalty discount and told to ask first: one call, three questions, each
option saying what it means. The terminal presents them as steps; the browser shows all three as one
form, answered at once.

![Question 3 of 3, Qualification, with Number of past invoices highlighted and Alt+← to go back in the hint line](05-question-step-3.png)

Every option carries a description. A list of bare labels asks the user to guess what the model was
thinking. `Alt+←` appears from step 2 — an answer can be reconsidered while reading the next question.

![The answers summary: Discount size 10%, Stacking Yes, stack both, Qualification Number of past invoices](06-question-summary.png)

Then every answer under its header before any of it is sent.

---

## MCP

![A cxgpu session with context7 configured: context7_resolve-library-id taking 10.9s and context7_query-docs 7.2s, then a three-point answer about MultilineEditControl](07-mcp.png "Terminal")
![The same answer in the browser, each context7 call followed by answered by terminal, and MCP · context7 · 2 tools in the side pane](07-mcp-browser.png "Browser")

Asked about SharpConsoleUI's `MultilineEditControl` and told not to search the disk, it used
[context7](https://github.com/upstash/context7): one call to find the library, one to read its
documentation. Each MCP tool asked first, separately from local commands — the always-allow rule it
offers covers `mcp:context7_resolve-library-id`, that tool on that server and nothing else.

---

## Running what it built

![The agent finding the project file, running dotnet publish -c Release, then the single-file binary with --help, and reporting cxgpu 1.0.0's usage](08-running-the-build.png "Terminal")
![The same turn in the browser, ending with the help's first line in a code block and the summary under it](08-running-the-build-browser.png "Browser")

A feature that compiles but was never executed is not finished. Told to build cxgpu and run what it
built, not `dotnet run`, it published a Release build, found that the output is a self-contained
single file, and ran that with `--help`. Each `run_shell` asked first; the session was in always-ask.

---

## Reviewing the work

![/diff in the transcript after the loyalty change: invoice.py with the past_invoices argument and loyalty line, and test_invoice.py with test_loyalty_discount_stacks; the panel's Git line says 2 changed](09-diff.png "Terminal")
![The same diff in the browser, coloured, with the session pane beside it](09-diff-browser.png "Browser")

`/diff` is `git diff`, in the transcript. The app edits files without asking inside the working
folder, so the review step should not require another terminal. Long diffs are capped and say what
was elided.

![After a commit and a new CHANGELOG.md: Diff · no tracked changes · 1 untracked file: CHANGELOG.md — git add to see them here](10-diff-untracked.png "Terminal")
![The same notice in the browser under the CHANGELOG.md write](10-diff-untracked-browser.png "Browser")

`git diff` exits 0 with no output for a file it has never seen — so a brand-new file, which is what
an agent spends its time creating, would report as unchanged. Untracked files are named instead.

---

## Switching model mid-conversation

![After /model local-32k: the switch notice saying the conversation is kept and the window shrank from 212k to 32k, then the 32k model answering what was added today and how a customer qualifies; the panel lists tokens for both instances](13-model-switch.png "Terminal")
![The same notice and answer in the browser, the model chip reading local-32k](13-model-switch-browser.png "Browser")

The loyalty discount was built on the 212k instance; the 32k one answers what it was, without reading
anything — the conversation carried. The status bar reads `ctx 46%`, so the **context window
followed** rather than staying at the old model's, and the panel keeps the spend of both instances in
one session.

A `providers` entry is an *instance* — a name bound to one endpoint and one model — so `fast` and
`careful` can be the same server with different models, and switching "the model" and switching "the
provider" are the same act. Here `local` and `local-32k` are the same server with different windows.

The switch says what it costs, and only when there is something to say. Here the new window is
smaller, so it says so: nothing breaks, because the turn loop measures pressure before every send
and compacts if it must, but a conversation that fitted may now have to be summarised.

---

## What it cost

![/stats over the last 7 days: 1,471,347 tokens across 15 sessions and 91 turns, 19% to workers, 65% input cached, then the daily bar, by project, by instance and by agent type](12-stats.png "Terminal")
![The same report in the browser, each section a block with a Copy button](12-stats-browser.png "Browser")

`/stats` over every session in this configuration. Further down, **tools are ranked by characters
returned, not call count** — a turn re-sends everything before it, so one tool returning a lot over
a few calls costs it again on every later turn. Forty cheap calls are rarely the problem. The last
line is the permission gate's own accounting: how many calls asked, and how many an always-allow rule
let through.

The worker share is the number that says whether delegating paid: what the workers spent, against
everything.

---

## Sessions

![The resumed invoice session: the question asked without re-reading anything, the answer listing the day's changes, and /sessions below it — 13 sessions here, two of them just now](11-sessions-and-resume.png "Terminal")
![The same answer in the browser, with the sidebar listing the invoice sessions](11-sessions-and-resume-browser.png "Browser")

`cxagent --resume DQZY42` restored 59 messages, and the question above the listing is the check that
matters — *without re-reading anything*. It answered from memory and spent no tool calls doing it. It
is also wrong in one place: the loyalty change was committed earlier in that session, and it lists it
as uncommitted.

---

## Delegating a piece of reading

![The three worker rows collapsed under the lead's request, each done with its time](16-worker-result.png "Terminal")
![The same rows in the browser, each showing the worker's brief rather than its title](16-worker-result-browser.png "Browser")

A worker is one row. It ran in its own context, spent its own turns, and what comes back is the
answer — the row stays a row, and the transcript carries on.

![The reviewer's row opened: 13 calls across 3 tools in 4.9s, a table of each call with its target, time and output size — git log, git show, the project file, three greps, a build and five test runs](17-worker-timeline.png "Terminal")
![The same row opened in the browser, offering Open in a tab](17-worker-timeline-browser.png "Browser")

The same row, opened in the terminal: every call the reviewer made, with the time each took and how
much came back. It took five tries to run the tests — `dotnet test` at the repository root runs
nothing here — before it named the test project. A worker's report is a claim; this is the work
behind it. The browser's row opens to a link to the worker's tab instead.

---

## Plugins

![F2's plugin manager: nothing installed, six plugins available, Calculator selected with not installed, contract 2, 1 tool, never asks](18-plugin-marketplace.png "Terminal")
![The browser's marketplace: Calculator, Clone Finder and C# Language Server cards with their tools and a folder picker beside Install](18-plugin-marketplace-browser.png "Browser")

**F2** opens the manager, and the browser has the same catalog. The list is read from a catalog
published on the web, so what a machine can install is whatever has been released rather than
whatever shipped with the binary — no plugin is bundled with the installer. `TOOLS 1, never asks` is a
promise the manifest makes and the load gate later repeats: this plugin's tool does not stop to ask
permission, because it reads nothing and reaches nowhere.

![The HTTP permission prompt naming the calculator release URL, with Allow once and Deny](19-plugin-download.png "Terminal")
![The same prompt in the browser, over the marketplace](19-plugin-download-browser.png "Browser")

Installing asks first, and the question names the file it is about to fetch. There is deliberately no
**Always allow** here, though other prompts offer one: a standing rule for plugin downloads has no
honest scope — "any download from github.com" would pre-approve every future plugin, and the exact
URL is useless because the next version has a different one. What arrives is checked against the
catalog's `sha256` before anything is written.

![The load prompt: calculator wants to read files in this folder, adds 1 tool and guidance, and Always allow covers its hash](15-plugin-load.png "Terminal")
![The same prompt in the browser, the hash on the Always allow button](15-plugin-load-browser.png "Browser")

A plugin is a DLL cxagent did not ship, loaded into this session. The prompt is the only boundary
cxagent can enforce on your behalf, so it says what the plugin will contribute and covers its
approval with a hash of the whole load set. Change a byte of it and this question comes back. It is
a different question from the download, asked separately on purpose: one consents to fetching bytes,
this one to running them.

![The manager with calculator and csharp-lsp installed, both active, not in config; csharp-lsp selected with its three tools and what it needs on PATH](20-plugin-installed.png "Terminal")
![The browser's marketplace with both installed and active, each with Deactivate, Add to config and Uninstall](20-plugin-installed-browser.png "Browser")

Two plugins, each in a directory of its own — calculator from the terminal, csharp-lsp installed and
loaded from the browser, and the terminal's list shows both. A plugin's identity is a hash over
everything in its folder, so two plugins sharing one directory would be neither isolated nor
separately identifiable. `active, not in config` is both halves of the truth: the plugin is answering
tool calls now, and nothing in `config.json` names it, so the next session starts without it.

![The model grepping three times, then csharp_definition at 2,810 ms and csharp_references at 306 ms in a table, and the answer: AlertEngine declared at AlertEngine.cs:17 with 44 references](14-plugin-lsp.png "Terminal")
![The same calls in the browser, both opened, with their times](14-plugin-lsp-browser.png "Browser")

Told to use the C# tools and not grep, it grepped three times first, then used them. `csharp_definition`
took **2.8s** and `csharp_references` **0.3s**: the first pays for the language server to index the
solution, and everything after is answered from a warm index. The references cross from `cxgpu` into
`cxgpu.Tests`, which grep cannot tell apart from a name in a comment. Its breakdown says three
production files and names two.

![The manager with three plugins installed and clone-finder selected, its README rendered with a sample report](21-clone-finder.png "Terminal")
![The browser's Clone Finder card with its README opened](21-clone-finder-browser.png "Browser")

`clone-finder` answers "where is the duplicated code" in a single call, because the alternative is
reading forty files to notice that three of them share a block. The README is rendered from disk, so
what a tool returns can be read before deciding to install it: locations and a couple of lines per
finding, never the duplicated block itself.

![find_clones in one call, then the model reading two ranges: OverviewTab.cs 10 lines by 5 and ProcessesTab.cs 22 lines by 2, both judged worth merging](22-clone-finder-working.png "Terminal")
![The same answer in the browser](22-clone-finder-working-browser.png "Browser")

One call returned the duplication in the whole repository; the model reads only the ranges the report
pointed at rather than hunting for them.

---

## A terminal, for the things a tool cannot do

![A shell in its own tab beside Chat in the invoice folder, with copy back on exit and Copy now, after git status, git log and ls in colour](23-shell-terminal.png "Terminal")
![The browser's own shell on the same folder: the same commands in colour, with Copy to transcript and End shell](23-shell-terminal-browser.png "Browser")

`run_shell` runs a command and reads what it printed. That covers most of what an agent needs and
none of what needs a person: a `sudo` password, `gcloud auth login`, `git rebase -i`. A bare `/shell`
opens a real PTY in a tab beside `Chat`, and the browser's `Open a shell` opens one of its own. The
colours are the giveaway — `git` and `ls` emit them only when they believe they are talking to a
terminal.

In both, the checkbox **copy back on exit** is off by default, because a shell you opened for yourself
is a convenience rather than a channel to the agent; `Copy now` sends what it has shown so far. (The
browser picture was taken before its shell had the checkbox, and shows the earlier `Copy to
transcript`.)

![The model answering what was just done in the terminal: the two command lines and what each showed, including the stash on bulk-threshold](24-shell-transcript.png "Terminal")
![The same answer in the browser](24-shell-transcript-browser.png "Browser")

The same session afterwards, from a terminal the agent never touched and cannot drive. The transcript
is queued, not delivered into a turn of its own: someone who has just closed a terminal may be
reading it, or thinking, or gone. It arrives with whatever you say next.

---

## A file, for the edit that is faster to make than to describe

![invoice.py open in a tab with line numbers and Python highlighting, Save ^S and py · 27 lines](25-editor.png "Terminal")
![The same file open in the browser's editor, with Back to session and Save](25-editor-browser.png "Browser")

`/open` puts a file in a tab beside the conversation, and the browser's `Open a file` does the same.
Saving writes the file back with the conventions it arrived with: a BOM stays, CRLF stays, and the
diff shows only what you changed.

The reason it exists is the line the model never sees otherwise. It read a file at turn 12, you
changed it at turn 15, and it edits from a stale read at turn 20. Saving here tells the model:
`[cxagent] the user edited <path>`, queued rather than sent, so it arrives with your next message.

![The same file with "# ten counts" typed but not saved, after the agent rewrote the docstring on disk: Save, Reload, See theirs and "changed on disk — reload to see it"](26-editor-changed.png "Terminal")
![The browser's editor with the same unsaved line: invoice.py • and "Changed on disk. Your edits are kept here." with See theirs and Reload](26-editor-changed-browser.png "Browser")

The other half of the same problem. A watcher notices when the agent writes a file you have open: a
clean buffer just reloads, and a modified one keeps your edits and says so. `Reload` and `See theirs`
appear only in this state. `See theirs` opens what is on disk read-only in a second tab, because
choosing between two versions you cannot read is not a choice. Reload on a modified buffer asks
first, and so does Save.

---

## Background jobs, and what they printed

![The jobs panel after both background jobs finished: dotnet build -c Release exit 0 in 0s and dotnet test exit 0 in 1s, beside the agent's report of both](27-jobs-panel.png "Terminal")
![The browser's Tasks tab listing the same two commands with their pids and exit codes](27-jobs-panel-browser.png "Browser")

Asked to start the tests and a Release build in the background and not to poll, the agent started
both and stopped. `F7` turns the right-hand column into the tasks panel; both jobs finished within a
second here, so both are under Finished, and the agent read their output and reported both.

![The test job in a tab: the command, its pid, 0:01, exit 0, and the output ending Passed! with 230 passed](28-job-tab.png "Terminal")
![The same job's output in the browser, with Copy to transcript](28-job-tab-browser.png "Browser")

`View output ›` opens a job in a tab: the full command, its pid and state, and every line it wrote. A
running job's tab follows its output and has a `Kill` button; once it has finished, only
`Copy to transcript` is left, which sends the end of the output with your next message.

---

## A question on the side

![An aside block, not in the agent's memory, answering which backends work without ROCm, under the main turn's table of cxgpu/Gpu files](29-btw-aside.png "Terminal")
![The same aside in the browser, and the composer in btw mode: Asking on the side — the main conversation is not changed](29-btw-aside-browser.png "Browser")

The main agent was reading every file under `cxgpu/Gpu`. `/btw which of those backends would still
work on a machine with no ROCm installed?` asked about it without interrupting; with one llama.cpp
slot, the aside waited for the model and answered after the main turn's table. The block is headed
`aside · not in the agent's memory`, and that is literal: the side agent starts from what the main
agent last sent, and nothing it is asked or says goes into the conversation the agent re-reads.

The composer's line says `btw · Esc to return`, on both screens: Enter now talks to the aside, and a
follow-up continues its thread.

![The aside asked to read /etc/hostname: run_shell refused, then the session's prompt — Read a file outside the working folder?, asked for by: aside](30-btw-permission.png "Terminal")
![The same prompt in the browser, asked by aside, with Allow once, Always allow (/etc/) and Deny](30-btw-permission-browser.png "Browser")

A side agent can only read — `read_file`, `glob` and `grep`; its `run_shell` was refused. It reads
under the session's own rules: `/etc/hostname` is outside the folder, so it gets the same prompt the
main agent would, marked as the aside's. Esc answers it no and the aside carries on without the file.

---

## One goal, no window

![Four headless runs in a shell in invoice: a one-sentence answer, a pipe from git log, JSON through jq, and a -c run naming the two test methods, ending exit 0](31-headless.png)

`cxagent -p` runs one goal and prints the answer — no window, nothing to press — so it slots into a
pipe like any other command. The second run reads its input from `git log`; piped text is attached
under the goal, not run as one, and when the model tried to run `git log` itself, that was denied —
nobody was there to answer the prompt.

`--output-format json` prints a single result object in Claude Code's shape, which is why `jq` can
pick out `subtype` and `num_turns` without knowing anything about cxagent. The last run is `-c`: it
continues the conversation the JSON run left behind, so "name them" needs no context. `echo $?` is
`0` because it answered; a turn limit is `3`, Ctrl+C `130`. `--trust` here only trusts the folder for
the run, and writes nothing.

---

## A prompt you keep, as a command

![/review-branch off-by-one errors after it ran: ran git diff main...HEAD, the review naming invoice.py:27, and the / palette listing review-branch from the project and release-notes from config](32-custom-commands.png "Terminal")
![The same review in the browser with the same two commands in its palette](32-custom-commands-browser.png "Browser")

`invoice` has `.cxagent/commands/review-branch.md`, in the same format Claude Code uses. Its body asks
for a review and ends with a line ``!`git diff main...HEAD` ``. Typing `/review-branch off-by-one
errors` sent that file, not those four words; the diff ran after the same permission prompt the
agent's own `run_shell` gets, and the notice under the row says what it ran and how much came back.
The review finds the `> 10` at `invoice.py:27`, and then counts two bugs and lists three.

The palette is `/re` typed again, on both screens: the project's command and one from the config
folder, each tagged with where it came from. The nearer one wins a name clash, and the tag is how you
see which one is about to run.

---

## What it thought, and how hard

![Thinking on: two Thought for 1s blocks around replace_in_file and run_shell, the second opened, and the mode line showing on (F10)](33-thinking.png "Terminal")
![The same turn in the browser, the second thought opened](33-thinking-browser.png "Browser")

The same repository with thinking switched on. Each step that thought has its own block, placed before
what that step did, folded to `◆ Thought for 1s · 45 words` as soon as the step moves on. The run
reads as thought, calls, thought, answer. A click opens one, and `Ctrl+O` the latest. The thinking is
shown and nothing more: it is never sent back to the model and never saved.

`F10` steps through what the server offers — here `off` and `on`; a Claude model offers its effort
levels instead.

---

## It remembers you

![Remember for this project: run the tests with python3 -m unittest — the model writing a test-command note, and the panel's Memory line reading 1 system · 1 project](45-memory-note.png "Terminal")
![The same moment in the browser, its Memory section listing test-framework-preference and test-command with Open and Forget](45-memory-note-browser.png "Browser")

Told how this project runs its tests, the model wrote a project memory note. Notes are files in the
config folder: system ones for every project, project ones for this folder. An index of them is in
every session's instructions, so the next session here starts knowing. The browser lists them with
`Open` and `Forget`.

---

## Configuration, in the app

![F8's Providers tab: the Roles block with local as default and deepseek as reviewer, the rail of instances, and local selected with its endpoint, key set · …ocal, model and thinking, and the line saying its open tab](34-configuration.png "Terminal")
![The browser's configuration page on Providers: the defaults, the instance list, and local's kind, model, endpoint, description and key](34-configuration-browser.png "Browser")

`F8` opens one dialog, in tabs; the browser's `Settings` is the same configuration, from the same
drafts. The Roles block picks the default instance and the auto-mode reviewer. The rail lists every
instance, its second line saying its role and its model. Beside it, the selected one's fields: the
endpoint, the model (`Change…` lists what the endpoint serves), the key — `set · …ocal`, its last four
characters, never the key — and the thinking level new sessions start at. A save applies at once, and
the line above Save says what that means for the tab on it.

![The Agents tab: researcher, yours, with its description, briefing and Runs on set to These providers, local first as default](39-agent-types.png "Terminal")
![The browser's Agent types tab on researcher: description, briefing, Runs on with deepseek and local ticked, max turns 20 and tools inherited](39-agent-types-browser.png "Browser")

The Agents tab lists every sub-agent type: `general`, the default child, the ones that ship with
cxagent, then your own. This one is the `researcher` from [CONFIG.md](../../CONFIG.md): a description
saying when the model should reach for it, and a briefing saying how it works. **Runs on** is the
per-sub-agent model: a list of providers makes the first the default, and a spawn may pick another
from the list.

---

## First run

![First run with no config: the configuration dialog open on six cards — Anthropic, OpenRouter, OpenAI, Ollama, llama.cpp and OpenAI-compatible](35-first-run.png)

With no `config.json`, cxagent opens the dialog on a card per kind of endpoint, the first one focused.
A card fills in what it knows — llama.cpp's port, OpenRouter's address and attribution header — and the
model is picked from what the endpoint serves. The first save wires the session; type a goal and it runs.

---

## Exporting a conversation

![The export dialog over the team session: whole session, Readable, saved to a file named 2026-11-07-put-a-team-on-this-one-worker.md, about 20 KB across 34 messages and 3 sub-agents](38-export.png "Terminal")
![The browser's session pane at the same moment, with Readable and Full under Export](38-export-browser.png "Browser")

`F12` — or `/export` — writes the conversation as markdown. *Readable* is for a person: what was said
and done, each tool call a line, edits as diffs. *Full* is for another agent to carry on from: every
tool result, the thinking, the system prompt. The three sub-agents counted at the bottom go into the
file where they worked, so it reads as a timeline. The size is known before anything is written. The
browser's session pane has the same two, `Readable` and `Full`, without the dialog's choices.

---

## A screenshot in the conversation

![The image window over a session on Claude Haiku: a pasted 3500 by 2504 screenshot shrunk to 1568 by 1122, drawn in full in Kitty, its sizes in the title and Open in viewer in the footer, and the composer holding the image's token and the question about it](40-images.png)

A 3500×2504 screenshot, pasted with `Ctrl+V`, sits in the composer as `[Image #2 1568×1122]`: the size
the model is sent, shrunk to 1568 pixels on its long edge. `Ctrl+G` beside the token, or a click on the
message's image button once it is sent, opens this window. Kitty draws the pixels; any other terminal
draws half-blocks, which show what the picture is but not its text, and `Open in viewer` hands the file
to the system's own. The session is on Claude Haiku, which reads the picture; a model that cannot is
sent a note naming it instead, so the conversation goes on.

---

## One session, three screens

![The invoice session in the terminal: the bulk-discount fix and its tests, then "Run the tests once more", answered on the web, and 5 tests ran, 4 passed, 1 failed](47-web-terminal.png "Terminal")
![The same session in a desktop browser: the same turns, the sidebar and the session pane](47-web-browser.png "Browser")
![The same session on a phone: the last answer and the composer with its chips shortened](47-web-phone.png "Phone")

`/web` serves the terminal's sessions to a browser. One session, three screens: the request was typed
in the terminal, the permission prompt for the test run was answered on the web, and the answer is on
all three. On a phone the sidebar and the session pane fold away behind the header's buttons.
