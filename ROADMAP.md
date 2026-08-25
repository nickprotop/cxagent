# cxagent — Roadmap

What is next. See [README](README.md) for what cxagent does and
[cxagent.Core](cxagent.Core/README.md) for the library.

## Next

- **A plugin marketplace** — somewhere to publish an entry, and a picker to install one.
- **A plugin that is not written in C#** — the ABI path is tested but nothing real uses it.
- **A web front end** — the reason the extraction was worth doing.
- **Skill scripts** — a skill that runs, not just one that is read.
- **Per-type skill catalogs** — a planner that sees planning skills and nothing else.
- **Sandboxed shell** — what stands between this loop and running tools in parallel.
- **Narrow `SessionManager.Shared`** — read-facing accessors instead of live stores.

## Known

- **Pipes still prompt.** A read-only command runs unasked unless it is piped.
