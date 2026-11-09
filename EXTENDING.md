# Extending cxagent

Skills, custom commands, hooks and plugins: what each adds and where it lives. Writing a plugin is
covered in [the SDK](cxagent.Core/sdk/README.md).

## Skills

Instructions the model loads **when it needs them**, instead of carrying them on every turn.

Put a `SKILL.md` in `.cxagent/skills/<name>/` with a `description` saying *when* it applies. Only the
name and description ride in the prompt; the body is fetched by a tool when the model decides a task
matches, so twenty skills cost a few hundred characters instead of sixty thousand.

```markdown
---
name: double-entry-posting
description: Use when adding or fixing ledger posting or balance logic in this
  repo. Covers house rules that are not obvious from the code.
---

Every posting must sum to exactly zero. Reject unbalanced transactions with
ERR-UNBALANCED in the message.
```

`/skills` lists what was found and, more usefully, every `SKILL.md` that was **skipped and why**,
because a file with broken frontmatter is otherwise invisible: you wrote it, nothing happened, and
there was no error anywhere. The session panel shows which skills are currently in force.

Skills from every folder are offered together; for a name that appears twice, the nearest wins.
Type a skill's name (`/double-entry-posting`) to run it yourself. Claude Code's `.claude/skills/`
are not read: they are written for that tool's tools. One that suits cxagent too can be copied or
linked into `.cxagent/skills/`, where cxagent's own permission gate still governs every tool call
and no `allowed-tools` grant comes with it.

**Same caveat as delegation: whether a model reaches for a skill is a property of the model.** On a
local `qwen3.6-35b-a3b` it does (it announced *"this is a double-entry bookkeeping task, so let me
load that skill first"* and loaded it unprompted), but only after the prompt was changed to say
explicitly that reading the file directly is not the same thing. Before that, the model found the
`SKILL.md` with the file-listing tool and read it: the instructions arrived, and nothing else in
the session knew a skill was in force.

See [CONFIG.md](CONFIG.md#skills) for where they live and how shadowing works.


## Custom commands

A prompt you keep retyping, saved as a file. `.cxagent/commands/review.md`:

```markdown
---
description: Review a pull request
argument-hint: [pr-number]
---

Review PR $1 against the house style. The diff: !`gh pr diff $1`
```

`/review 42` sends that, with the PR's diff in place of the shell line, which asks first, like any
shell command the model runs. A Claude Code command copied into `.cxagent/commands/` works
unchanged; `.claude/commands/` itself is not read. Its `allowed-tools` is ignored, and `/commands`
says so, along with every file that was shadowed or skipped. A command typed while a turn runs waits and runs next. `cxagent -p "/review 42"` runs it
headlessly. See [CONFIG.md](CONFIG.md#custom-commands).


## Hooks

Commands that run on the agent's events: before a tool call, after one, when a prompt is sent, when a
turn ends, when a session opens. A hook reads the event as JSON on stdin and answers with its exit
code: `2` blocks, with its message going to the model. With JSON on stdout it can allow, ask, deny or
rewrite a tool call, add context, or send the agent back to work when the tests still fail. Besides a
command, a hook can be a yes-or-no question to one of your models, a read-only agent that checks the
project before it answers, a web request, or a call to one of your MCP servers.

```json
"hooks": {
  "PreToolUse": [ { "matcher": "run_shell", "hooks": [ { "type": "command", "command": "~/bin/guard.sh" } ] } ]
}
```

System hooks live in `config.json`; a repository's in `.cxagent/hooks.json` (shared) and
`.cxagent/hooks.local.json` (yours alone), which run only once you trust the folder. They fire for
sub-agents and headless runs too, so a guard cannot be stepped around. A hook's allow never reaches a
write into `.git`, `.cxagent` and the other protected folders. F8's **Hooks** tab edits all three files;
**Project** shows what a repository brings, its hooks among them. `/hooks` lists what runs. See
[CONFIG.md](CONFIG.md#hooks--commands-that-run-on-the-agents-events).


## Plugins

Tools from a DLL you drop in a folder, without rebuilding cxagent.

Four are in the catalog:

- **csharp-lsp**: go-to-definition, find-references and diagnostics for C#, backed by a language
  server, crossing project boundaries as grep cannot.
- **calculator**: evaluates a whole arithmetic expression exactly, in one call.
- **triggers**: work that starts without anybody typing: a turn schedules a prompt to arrive later
  in the same session.
- **clone-finder**: duplicated code found without reading the repository: locations ranked by size,
  in one call.

None is bundled with the installer. Each arrives through the plugin manager (**F2**), which
downloads, verifies and unpacks into your plugins folder and stops there. cxagent tells you a plugin
is present and leaves it alone:

```
plugin 'csharp-lsp' found in ~/.config/cxagent/plugins (3 tool(s)), no auto load.
```

Turn it on for one session, with no file to edit:

```
/plugin load csharp-lsp.dll
```

or keep it, in `config.json`:

```json
"plugins": { "csharp-lsp": { "file": "csharp-lsp.dll" } }
```

Either way it asks once whether to trust the binary, showing a hash of its contents. **Installing a
plugin is not approving it**, and nothing in config can pre-approve one. It needs a C# language
server on PATH: `dotnet tool install -g csharp-ls`.

Its lookups run unasked inside the folder you started in, and ask before reading a file outside it:
a plugin decides that per call, from the arguments, rather than being on or off for every call.

Browse plugins at [https://nickprotop.github.io/cxagent/plugins.html](https://nickprotop.github.io/cxagent/plugins.html). Writing one starts at
[https://nickprotop.github.io/cxagent/writing-a-plugin.html](https://nickprotop.github.io/cxagent/writing-a-plugin.html); the full contract is in
[cxagent.Core/sdk/docs/plugins.md](cxagent.Core/sdk/docs/plugins.md).


## Building your own?

Everything under the terminal ships as [**`CxAgent.Core`**](cxagent.Core/README.md): sessions,
agents, tools, sub-agent delegation, permissions and MCP, with no UI dependency. cxagent is one
consumer of it; [a Spectre.Console front end](cxagent.Core/examples/SpectreAgent) is another, in a
hundred lines.

```
dotnet add package CxAgent.Core
```
