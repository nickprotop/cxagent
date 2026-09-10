# triggers

Work that starts without anybody typing — on a clock, or when a process exits.

A session only ever does something when a turn asks it to. This plugin is what lets a turn ask for a
*later* turn: schedule a wake now, and the prompt arrives on its own when the time or the command
says so.

## The tools

`trigger_wake(after|at|every, prompt)` — schedules a prompt. Set exactly one of `after` (a delay,
`"90m"`), `at` (a moment, `"2026-09-10 09:00"`), or `every` (a five-field cron expression,
`"0 9 * * 1-5"`).

`trigger_list()` — what is pending: id, next fire time, first line of the prompt. The only way to
find out what was scheduled once it has fallen out of context.

`trigger_update(id, after|at|every, prompt)` — replaces a trigger's schedule and prompt, keeping its
id. Different from cancelling and rescheduling: a recreated trigger would get a new id a user has
already seen.

`trigger_cancel(id)` — stops a pending trigger.

`trigger_on_exit(command, timeout, prompt)` — runs a command unattended until it exits or the
timeout elapses, then wakes with the exit code and output appended to the prompt. This is the one
gated tool: it starts a process now, with arguments the model composed, and nothing downstream asks
about it again.

## The commands

`/triggers-add <when> <prompt>` — schedule a wake. `<when>` is a delay (`20m`), a time (`09:00`), or
a quoted cron line (`"0 9 * * 1-5"`); same three shapes `trigger_wake` accepts, from the command line
instead of a tool call.

`/triggers-list` — what is pending.

`/triggers-update <id> <when> <prompt>` — change a trigger's schedule and prompt, keeping its id.

`/triggers-cancel <id>` — stop a pending trigger.

## What it will not do

**Survive the process.** A trigger dies with the session that scheduled it. There is nowhere durable
to put one — a session is a folder, and this plugin is handed no path to it.

**Fire outside cxagent.** A trigger is not a system cron entry; it exists only while the process that
scheduled it is running. **Triggers fire only while cxagent is running** — a cron line set for 3am
with no terminal open does nothing at 3am. That changes with phase three, a daemon this plugin does
not have yet; until then, a trigger is only as reliable as the session that holds it.

## What it needs

The client capability, declared in the sidecar: a fire submits a fresh turn into the session that
scheduled it, which is materially more than answering a tool call and is disclosed at load like any
other plugin capability.
