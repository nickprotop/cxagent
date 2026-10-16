# Scripting cxagent

`cxagent -p "<goal>"` runs one goal with no window, prints the result and exits. That makes the agent
something a shell script, a git hook, a cron job or a CI step can call — and every run is an
ordinary session you can continue from the next line of the script, or open in the TUI afterwards
to see exactly what it did.

The options are Claude Code's, so a script written for `claude -p` runs with `cxagent` swapped in.
Everything on this page was run against a local model before it was written down.

---

## The shape of a run

```bash
cxagent -p "in one sentence, what is this repo?"            # the answer, on stdout
git diff | cxagent -p "review this"                         # piped input goes under the goal
cxagent -p "list the TODOs" --output-format json | jq -r .result
cxagent -c -p "now fix the first one"                       # carry on from the run before
```

- **stdout carries the answer**, stderr carries everything else — warnings, refusals, the provider's
  error. A script can redirect them apart.
- **Piped input is attached under the goal**, in its own block, so `git diff | cxagent -p "review
  this"` sends the goal and then the diff. With no goal text, stdin *is* the goal.
- **A custom command is a goal.** `cxagent -p "/review"` runs `.cxagent/commands/review.md` exactly
  as typing `/review` does in the TUI, with piped input under it — see
  [CONFIG.md](CONFIG.md#custom-commands). A built-in like `/clear` is not run; it goes to the model
  as text, because a goal is always a goal.

## Every run is a session

A headless run is saved like any other conversation in that folder, so the next command can pick it
up:

| | |
|---|---|
| `cxagent -c -p "…"` | continue the most recent session in this folder |
| `cxagent -r <id> -p "…"` | continue that session, from any folder |
| `cxagent --sessions` | list this folder's sessions, newest first, with their ids |
| `cxagent -c` / `cxagent -r <id>` | open it in the TUI instead — every step and tool call is there |

The id is in the JSON result as `session_id`. Continuing a session starts a new one that carries the
conversation on, so each step of a script stays a session of its own.

## Nobody is there to answer

A headless run cannot show a permission prompt, so **anything that would have asked is refused**: the
model is told, the run carries on without it, and the refusal is listed in the result's
`permission_denials` (and on stderr in text mode). What decides *before* a prompt still decides:
trusted folders, the "Always allow" rules you stored in the TUI, and the edit mode.

| You pass | Reads in the folder | Writes in the folder | Shell commands |
|---|---|---|---|
| nothing, untrusted folder | refused | refused | refused |
| `--trust` | allowed | refused | read-only ones (`ls`, `cat`, `grep`…) |
| `--trust --permission-mode acceptEdits` | allowed | allowed | read-only ones |
| `--permission-mode auto` | the classifier decides, as `/mode edits auto` does in the TUI | | |
| `--dangerously-skip-permissions` | allowed | allowed | allowed |

`--trust` trusts the folder **for this run only** and writes nothing. `git` is not on the read-only
list, so a run that needs `git diff` either gets it piped in — usually the better design — or needs a
stored rule or a wider mode.

**`--dangerously-skip-permissions` still refuses one thing:** a file-tool write into a `.git`,
`.vscode`, `.claude` or `.idea` folder, anywhere on the path, because what lands there runs later,
outside the run. That check reads file-tool requests, not shell text — a shell command that writes
there is approved like any other. Use it where the checkout is disposable: a CI container, a scratch
clone.

---

## Recipes

The examples use a small repository with one command file, `.cxagent/commands/review.md`:

```markdown
---
description: Review a diff for bugs and end with a verdict line
argument-hint: [what to focus on]
---
Review the change below for bugs. List each as `file:line — problem`, or say there are none.
End with exactly one line: `VERDICT: PASS` or `VERDICT: FAIL`.
```

A fixed last line is what makes the answer checkable by a script. Anything typed after `/review` is
added to the prompt — `/review off-by-one errors` — because the file has no placeholder for it.

### A review gate in CI

Pipe the branch's diff in, keep the whole result as an artifact, and fail the job unless the verdict
passes:

```bash
git diff origin/main...HEAD |
  cxagent -p "/review" --trust --output-format json > review.json

jq -e '.is_error | not' review.json > /dev/null || exit 1      # the run itself failed
jq -r .result review.json                                      # the review, into the job log
jq -r .result review.json | tail -n 1 | grep -q '^VERDICT: PASS'
```

`--trust` lets the model read the files around the diff for context; nothing it can do writes. Run
against a branch that compared `qty > 10` where its docstring said "ten or more", it answered:

```
prices.py:11 — The docstring says "for ten or more" but the condition is `qty > 10`, which
triggers the discount at 11 instead of 10. Should be `qty >= 10`.

VERDICT: FAIL
```

`review.json` also carries `permission_denials` — anything the model tried and was not allowed —
and the tokens and cost of the run.

### A pre-push hook

The same review, before anything leaves your machine. `.git/hooks/pre-push`, made executable:

```bash
#!/bin/sh
git diff @{push}.. | cxagent -p "/review" --trust | tail -n 1 | grep -q '^VERDICT: PASS' || {
  echo "pre-push: the review failed — run: git diff @{push}.. | cxagent -p /review --trust" >&2
  exit 1
}
```

`git push --no-verify` skips it when you know better.

### One step at a time

Each run continues the one before, so a script can diagnose, then change, then check — with each
step getting only the permissions it needs:

```bash
cxagent -p "bulk_price in prices.py gives no discount at exactly 10 items. Find out why. Do not change anything yet." --trust
cxagent -c -p "Fix it, and add an assert for 10 items to test_prices.py." --trust --permission-mode acceptEdits
cxagent -c -p "Run python3 test_prices.py and tell me the result in one line." --dangerously-skip-permissions
```

The first step found the `>` that should have been `>=`; the second changed one line and added the
test; the third ran it: *"All tests passed (exit code 0)."* `git diff` is the review, and
`cxagent -c` opens the whole chain in the TUI.

### Keep the id, come back later

`-c` takes the newest session in the folder. When other runs happen in between — another script, a
cron job — keep the id and use `-r`:

```bash
id=$(cxagent -p "In one sentence: what does prices.py do?" --trust --output-format json | jq -r .session_id)

# … anything else, any number of runs …

cxagent -r "$id" -p "Which function in it has a bug? One line." --trust
cxagent -r "$id"                       # or open it in the TUI
```

### The same goal over many files

One run per file, each its own session, with failures reported rather than stopping the loop:

```bash
git ls-files '*.py' | while read -r f; do
  printf '%s: ' "$f"
  cxagent -p "In one line, what does $f do?" --trust < /dev/null || echo "(exit $?)"
done
```

**The `< /dev/null` matters.** Inside `while read`, stdin is the list of files, and cxagent attaches
piped stdin to the goal — so without it the first run swallows the rest of the list and the loop ends
after one file.

### Watch it work

`stream-json` writes one JSON object per line as the run happens — `system`/`init` first, then the
model's text and tool calls, `result` last — so a script can show progress:

```bash
cxagent -p "Which file defines bulk_price? Check, then answer in one line." --trust \
  --output-format stream-json |
  while read -r line; do
    case $(jq -r '.type' <<<"$line") in
      assistant) jq -r '.message.content[] | .text // ("→ " + .input.summary)' <<<"$line" ;;
      result)    jq -r '"done: \(.subtype), \(.num_turns) turns"' <<<"$line" ;;
    esac
  done
```

```
→ grep · "bulk_price"
`prices.py:11` — `bulk_price` is defined in `prices.py`.
done: success, 2 turns
```

`--include-partial-messages` adds the answer's text as it streams, for a reader that wants to print
it live.

### Release notes from the log

A command for the prompt, the log piped in, cron for the schedule. `.cxagent/commands/changelog.md`:

```markdown
---
description: Release notes from a git log
---
Write short release notes from the git log below, grouped under Added, Fixed and Changed.
Leave out a group with nothing in it. No preamble.
```

```bash
git log --since="1 week ago" --format='%s' | cxagent -p "/changelog" > notes.md
```

The run needs no permissions at all — everything it reads arrived through the pipe.

---

## Reference

### Options

| Option | |
|---|---|
| `-p`, `--print "goal"` | run the goal and exit; the goal may also come after the options |
| `--output-format text\|json\|stream-json` | the answer alone (default), one result object, or one event per line |
| `--include-partial-messages` | with `stream-json`: the answer's text as it streams |
| `--verbose` | with `text`: progress, tool calls and a line per run of thinking on stderr |
| `--max-turns N` | stop after N turns, exit 3 |
| `--thinking <level>` | think at that level for this run — one the model offers, as `/think` lists them; cxagent's own option. A level it does not offer exits 2; a model that does not answer when asked exits 1 |
| `--permission-mode M` | `default` or `dontAsk` (refuse what would ask), `acceptEdits`, `auto`, `bypassPermissions` |
| `--dangerously-skip-permissions` | approve everything but writes into `.git`, `.vscode`, `.claude`, `.idea` |
| `--trust` | trust this folder for this run only |
| `--plugin NAME` | load that configured plugin; `--plugin none` loads none |
| `-c`, `-r <id>` | continue the newest session here, or that one |
| `--model <instance>` | run on a named provider from `config.json` |

Plugins load in a headless run only if you approved them with "Always allow" in the TUI or name them
with `--plugin`; one that is left out is listed in the result, with why. Claude Code options cxagent
does not have — `--allowedTools`, `--system-prompt`, `--mcp-config` — are refused by name rather than
ignored, so a script never believes one was honoured.

### The JSON result

Claude Code's shape, so a consumer written for `claude -p` reads it unchanged: `type`, `subtype`
(`success`, `error_max_turns`, `error_during_execution`), `is_error`, `result` (or `errors`),
`session_id`, `num_turns`, `duration_ms`, `total_cost_usd`, `usage`, `modelUsage` and
`permission_denials`. cxagent's own facts are kept apart under a `cxagent` key: `exitCode`,
`trusted`, `permissionMode`, `skipped` (`plugins`, `mcp`), `killedJobs` and `warnings`.

### Stream events

`system`/`init` (session, model, tools, MCP servers, plugins), `assistant` (text, a `thinking`
block, or a `tool_use` with a one-line `input.summary`), `user` (the `tool_result` for each
`tool_use`), `system`/`permission_denied`, `stream_event` (with `--include-partial-messages`: the
answer's `text_delta`s and the thinking's `thinking_delta`s) and `result` — the same object `json`
prints, last.

A step that thought sends its `thinking` block before that step's text or tool calls, as Claude
Code does. When the provider hid the text, the block's `thinking` is `""`: the model thought, and
said nothing about it. Thinking never appears in `result`.

### Exit codes

| Exit | When |
|---|---|
| 0 | answered |
| 1 | failed — the provider's message is on stderr and in `errors` |
| 2 | usage — a bad or unsupported option, no model configured, nothing to run |
| 3 | stopped at the turn limit |
| 130 / 143 | Ctrl+C / SIGTERM — the turn is cancelled and the session saved first; a second one ends it at once |
