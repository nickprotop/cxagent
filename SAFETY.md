# Safety

What cxagent asks before it does, where the model's commands run, and everything it does to your
machine. The settings behind it are in [CONFIG.md](CONFIG.md).

## Permissions

Reading and writing inside the working folder is free, and so are commands that can only look:
`ls`, `cat`, `grep`, and `cd` into the folder before one. Anything that can write, and anything
outside the folder, stops and asks with **Allow once**, **Always allow**, or **Deny**.

**"Always" grants the command's NAME, not the exact string.** Approving `git status` stores
`git status*`, so the next `git status --short` runs, while `git push` still asks, because a
subcommand is part of what names a command. The prompt shows the rule it would write before you
grant it.

Grants are remembered per folder, and a folder is identified by more than its path: delete a folder
and recreate it, and its old grants do not apply to the new one.

**`auto` lets a model decide some of what would otherwise ask.** In a trusted folder, a shell command
the static check refused is no longer a guaranteed prompt: it goes to the classifier first, and an
*allow* runs it silently. This is only for commands already confined: every path in the command
(including a `cd` target) has to resolve inside the working folder, the command has to be fully
parseable with no `$(...)` or backticks, and neither an egress verb like `curl` or `scp` nor a
recursive delete like `rm -rf` is ever eligible.
Those stay outside what a verdict can silence, no matter what the model says about them. The
classifier can also **deny** an action outright, with a reason you're shown. Every failure
(timeout, transport error, an answer it can't parse) means ask, same as if there were no classifier.
Auto-decided rows are marked `auto-approved` / `auto-denied` so you can tell a model's call from a
stored rule or a silent boundary pass.


## Sandbox

On Linux, the model's shell commands run inside [bubblewrap](https://github.com/containers/bubblewrap).
A contained command can write only in the project (the repository root, or the working folder outside
a repository), the temp folder and package caches like `~/.cache`, `~/.nuget/packages` and `~/.npm`.
It sees `~/.ssh`, `~/.aws`, `~/.gnupg`, cxagent's own config folder and the other usual credential
places as empty, loses credential-shaped variables (`*_API_KEY`, `*_TOKEN`, `AWS_*` and the like),
and cannot reach your session's sockets, so no D-Bus, no SSH agent, no tmux server, no X11, no Docker.
Inside the project, git's hooks and config and folders like `.cxagent`, `.vscode` and `.github` stay
read-only, because what is in them runs later, outside the sandbox. What a mount cannot hold (a
redirected `.git`, a new `.github`) is checked after every command: the session says so, and contained
commands ask until you run `/sandbox review`.

**A contained command skips the prompt where a write would.** In a trusted folder under Accept edits
or `auto`, it runs without asking, and `auto` does not ask the classifier about it either. Under
Always ask, or in a folder you have not trusted, it asks as before.

**Leaving the sandbox is always your call.** A command that fails because the sandbox blocked it can
be retried with `unsandboxed: true` and a reason. That asks every time, with no Always button, and no
rule, hook or mode can approve it. Rows that ran outside are marked `uncontained`.

**What it does not do.** With the network on (the default) a contained command can still send out
whatever it can read, which is everything outside the hidden list. `sandbox.network: false` closes
that and breaks installs and pushes with it. Package caches stay writable, and some hold code that runs
later. Your own shell tabs, hooks, MCP servers and plugins are not sandboxed; the file tools are not
either, because the permission rules already see their paths.

On by default where bubblewrap starts. Where it is not installed, or this is not Linux, commands run as
before and the session says so once. Where it is installed but will not start (user namespaces
blocked), each command asks whether to run uncontained. `/sandbox` shows what a command may touch,
`/sandbox on` and `/sandbox off` switch it, and F8 › General sets the rest. See
[CONFIG.md](CONFIG.md#sandbox--where-the-models-commands-run).


## What this thing actually does to your machine

Read this once. It is short, and every line of it is something the software genuinely does.

**It edits your files.** Inside the working folder it writes without asking. That is the point of
it, and it is why you should run it in a git repository with your work committed. `git diff` is the
review step. There is no undo inside cxagent.

**It runs shell commands.** On Linux they run inside a sandbox that lets them write only in the
project, temp and package caches, and hides your credentials. In a trusted folder under Accept edits
or `auto`, a contained command runs without asking. Elsewhere, and every command the sandbox does not
contain, asks first: **Allow once**, **Always allow**, or **Deny**. "Always" is remembered per folder.
Read what you are approving: the command is shown in full, and *"always allow"* means the next one
like it will not ask. A model can propose a command that deletes things, and if you approve it, it
runs. Inside the project the sandbox does not stop it: `git` is the undo. With the network on, a
contained command can still send out what it can read.

**In `auto` mode, a model can run a shell command without asking you first.** This is bounded (the
folder has to be trusted, the command has to resolve entirely inside it, and things like `$(...)`
substitution, `curl`/`scp`-style egress and `rm -rf`-style recursive deletes are never eligible,
whatever the model decides), but the decision to skip the prompt is still made by a language model,
and language models are wrong sometimes. That trade is the point of the mode: fewer interruptions on the commands most likely to be
fine, at the cost of trusting a model's judgment on the ones it approves without you seeing them
first. If you want every shell command to ask, don't use `auto`.

**`cxagent web` puts all of that behind a browser.** Whoever logs in gets an agent that edits files and
runs commands here, just as you do at the terminal. It listens on localhost unless told otherwise, needs
a login once it listens anywhere else, and says so loudly if you turn the login off. A shell, the
configuration and the editor beyond `web.roots` stay off for browsers until you turn each on. Plain HTTP beyond
this machine sends passwords and replies unencrypted: use `--self-signed` or a proxy, and set
`web.login` to `always` behind one.

**It spends your money.** Every turn is a request to whichever provider you configured, and a
sub-agent is a whole second run of turns. A single delegated search can cost several hundred thousand
tokens. The session panel shows the running total and the parent/worker split, `/stats` shows what
past sessions cost and which tools fill the context, and `orchestrator.maxTurns` stops a request
after a set number of turns. **A turn cap bounds iterations, not spend**: one turn that reads a
large file costs more than ten that do not, so it is a backstop against a runaway, not a budget. If you point cxagent at a paid API, **you are paying for what
it does**, including work that turns out to be wrong.

A measured example, on a local model where the cost was only electricity: one evening of six sessions
came to roughly two million input tokens, because every turn re-sends the whole conversation and one
sub-agent made ninety-five web requests before its turn cap stopped it. On a metered API that is real
money for a result that was partly wrong.

**It talks to whatever you configure.** Your prompts, your file contents and your shell output go to
your chosen model provider, and to any MCP server you have enabled. What they do with it is between
you and them.

**A plugin is code you chose to run.** A plugin is a DLL loaded into cxagent's own process, so it can
do anything cxagent can. It is not sandboxed, and no permission gate constrains what it does
internally. What cxagent can enforce is the decision to load it at all: it asks once, showing a hash
of the plugin's whole contents, and asks again if a single byte of it changes. Nothing in config can
pre-approve one. Past that, a plugin gates its own operations, or does not; **trusting a plugin is
trusting its author**, exactly as installing any other software is.

**A sub-agent is an agent.** It inherits its parent's tools and the same permission gate. A type's
briefing ("never edit files") is a request written into its prompt, not a sandbox. Models do not
reliably follow instructions they are given. **Permissions and tool selection are the mechanisms
that constrain an agent; prose is not.**

A [tool selection](CONFIG.md#tools--what-an-agent-is-offered) narrows what an agent is offered, and a withheld tool is
refused if called by name rather than merely hidden, so it constrains rather than suggests. It is
not a security boundary on its own: it decides what this deployment offers, while the permission
gate decides what any offered tool may do.

**Nobody is responsible for the results but you.** cxagent is provided as-is under the MIT licence,
with no warranty of any kind. The authors are not liable for lost work, deleted files, broken builds,
leaked secrets, provider bills, or anything an agent does with the access you granted it. Review the
diff. Keep backups. Do not run it against anything you cannot afford to have changed.
