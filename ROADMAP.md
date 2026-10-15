# cxagent — Roadmap

What is next. See [README](README.md) for what cxagent does and
[cxagent.Core](cxagent.Core/README.md) for the library.

## Next

- **Publishing a plugin from outside this repository** — the catalog and the picker that installs
  from it ship (`/plugin browse`, F2); adding an entry still takes a change to
  [`plugins.json`](plugins/plugins.json) here.
- **A plugin that is not written in C#** — the ABI path is tested but nothing real uses it.
- **A web front end** — the reason the extraction was worth doing.
- **Skill scripts** — a skill that runs, not just one that is read.
- **Per-type skill catalogs** — a planner that sees planning skills and nothing else.
- **Sandboxed shell** — what stands between this loop and running tools in parallel.
- **OpenAI's reasoning text** — the Responses API returns reasoning summaries; Chat Completions,
  which cxagent speaks, returns none, so an OpenAI model's levels work but nothing shows.
- **Narrow `SessionManager.Shared`** — read-facing accessors instead of live stores.

## Known

- **Pipes still prompt.** A read-only command runs unasked unless it is piped.
- **A configured plugin's own `Load` runs before it is approved** in the TUI: the trust question comes
  after the plugin's code has run once. A headless run decides first and skips an unapproved plugin.
