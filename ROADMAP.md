# cxagent — Roadmap

What is next. See [README](README.md) for what cxagent does and
[cxagent.Core](cxagent.Core/README.md) for the library.

## Next

- **Checkpoints and rewind** — roll the working tree back to before a turn. Today there is no undo
  inside cxagent; git is the safety net, and an edit the model made has to be reverted by hand.
- **More hook events and types** — sub-agent, compaction, model-switch, to-do and other events, and
  hooks that ask a model, run an agent, call a URL or an MCP tool. They load today and show as not
  supported; each starts firing when it is wired.
- **Hooks in plugins** — a plugin folder bringing its own `hooks/hooks.json`, approved with the plugin.
- **Sandboxed shell** — commands run as the user, gated by permission rules but not contained; the
  same thing stands between this loop and running tools in parallel.
- **A web front end** — the reason the extraction was worth doing.
- **Skill scripts** — a skill that runs, not just one that is read.
