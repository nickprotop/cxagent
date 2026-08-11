# Commands

Typed into the composer, like a message. They are handled by the app before the model sees anything,
so they cost nothing — no request, no tokens.

| Command | What it does |
|---|---|
| `/help` | Keys and commands |
| `/mode` | Show the agent mode |
| `/mode single` · `/mode fan-out` | Set it, live |
| `/clear` | Wipe the conversation |
| `/compress` | Summarise the conversation to free room |
| `/mcp` | List MCP servers, or inspect one |
| `/mcp reload` | Re-read config and reconnect |
| `/mcp login <server>` | Authorise a server that needs OAuth |
| `/exit` | Quit |

---

## `/mode`

```
/mode              → mode: fan-out  (set with /mode single or /mode fan-out)
/mode single       → mode: single — this agent works alone; the spawn tool is withdrawn.
                     The conversation is unchanged.
/mode fan-out      → mode: fan-out — this agent can now spawn sub-agents.
```

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

## `/exit`

Quits, and marks the session finished so it is not offered for resume next time. A session that ends
any other way — a crash, a kill — stays unfinished and is offered when you next start **in the same
folder**. Resume is scoped to the working directory: a session crashed in one project is never
offered in another, because restoring it would fill this conversation with another project's files
and decisions.

---

## Not commands

**Escape** stops a running turn. Anything queued goes back into the composer rather than being
discarded — it was never sent, so stopping must not eat what you typed.

**Enter during a running turn** queues the message rather than starting a second one. Several queued
messages are joined into one prompt, newline-separated, and sent when the turn ends. They are
appended rather than replaced: two messages typed in succession are usually one thought completed,
and keeping only the last would silently discard half of what you said.

**F3** cycles the session panel — shown, hidden, automatic. It carries context occupancy, spend,
session id, MCP servers, configured agent types, and granted permissions. Spend appears per model
when more than one model has been used.
