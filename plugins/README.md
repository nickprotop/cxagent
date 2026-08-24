# Plugin catalog

Plugins cxagent releases and supports. One directory each, and
[`plugins.json`](plugins.json) beside them as the machine-readable version of this page — what the
plugin dialog will read rather than parsing prose.

| plugin | what it does | needs | |
| --- | --- | --- | --- |
| **csharp-lsp** | Go-to-definition, find-references and diagnostics for C#, across project boundaries | a C# language server (`csharp-ls` by default) | [details](csharp-lsp/README.md) |

## Installing one

`install.sh` places `csharp-lsp` in your config folder's `plugins/` automatically. Any plugin here
can also be taken from a release by hand — see its own page for the asset name and the config entry.

**Installed is not enabled.** cxagent reports a plugin it finds but has no configuration for, and
leaves it alone: loading runs code cxagent did not write, so it asks once, showing a hash of the
plugin's whole load set. Enable one with `/plugin load` for a single session, or a `config.json`
entry to keep it.

## Adding a plugin to this catalog

A plugin here ships as a release asset and gets installed by the installer, so it carries the same
expectations as the app. Add its directory, a `README.md` documenting the asset and configuration,
and an entry in `plugins.json`.

Nothing requires a plugin to live here. A third-party plugin is a DLL and a sidecar dropped in the
plugins folder or named by `pluginPaths`; it needs nothing from this directory and gets the same
treatment at load.

Two rules if you do add one:

**Name its tools for what makes them yours.** A tool name is claimed session-wide and two plugins
cannot share one — `csharp_definition`, never `lsp_definition`, or the next language-server plugin
cannot load beside this one.

**Reference `CxAgent.Core` with `Private="false"`.** The host process already has it loaded; a copy
beside the plugin is dead weight in every release asset.

See [the plugin guide](../cxagent.Core/Core/Plugins/README.md) for the contract, the sidecar format,
permission, settings, and two worked calculator examples.
