# Releasing a plugin in this catalog

Maintainer procedure. A plugin in [the catalog](README.md) is one cxagent builds, releases and
installs, so it carries the same expectations as the app itself.

Writing a plugin is a different job — [the developer
guide](../cxagent.Core/docs/plugins.md) covers the contract, the sidecar, permission and settings.
This page is only about getting one released.


**1. The directory.** `plugins/<name>/`, with the project, its sidecar, and a `README.md` covering
what the plugin does, the release asset to download, anything the USER must install separately, and
every setting. Follow [csharp-lsp](csharp-lsp/README.md).

**2. The catalog entry**, in [`plugins.json`](plugins.json). `name`, `version`, `spawns` and `tools`
must match the plugin's own sidecar — `PluginCatalogTests` fails the build if they drift, because the
picker shows what this file says without loading a DLL to check.

`source` says where the plugin comes from, how to download it, and how to check what arrived.

A plugin released here uses `kind: "release"`. It has no fixed URL on purpose — it tracks whichever
cxagent release is being installed — so it carries `urlTemplate` (fill in `{repo}`, `{asset}` and the
tag) plus `latest`, GitHub's always-current alias for a client with no tag in hand. Integrity comes
from the release's own `SHA256SUMS`.

One hosted elsewhere uses `kind: "url"`: a URL fetched as written, and its own `sha256`, because
nothing in this project can vouch for a file it does not build.

**A native plugin is per-platform, and the catalog has to say which.** A managed plugin is portable
MSIL — one build runs everywhere cxagent does, so it says `platforms: ["any"]` and has one `source`.
An ABI plugin is a native library: a `.so` does not load on Windows, and a publisher may ship
`linux-x64` and nothing else. Those entries name the RIDs they actually have and key their downloads
by the same names under `sources`, so a picker can say "not available for your platform" instead of
downloading a library that cannot load. `$exampleNative` in the file shows the shape. Its `publisher`, `license` and
`repository` stop being a formality too — nobody needs telling who wrote a plugin that ships in the
same zip as cxagent, and everybody needs telling for one that does not.

The `$example` entry in the file is a worked third-party instance to copy. It is not installed.

**3. The release build**, in `.github/workflows/release.yml`. A managed plugin with no
`RuntimeIdentifier` is portable MSIL, so it needs ONE build, not a matrix entry — six RIDs would
upload six identical files. Build rather than publish (`Private="false"` keeps `CxAgent.Core` out of
the output, and a publish would pull in its whole dependency tree), then zip the DLL with its
sidecar. They must ship together: the sidecar is read before the assembly is loaded, so a zip
missing it produces a plugin that cannot load and cannot be described.

**4. The installer**, in `install.sh`, if it should be installed by default. Download from the
release pinned to `$TAG` — a plugin built from a later commit than the binary it plugs into is a
skew that surfaces as a puzzling failure — and keep it best-effort, so an older release without the
asset still installs cxagent itself. Note that `install.sh` installs named plugins, not the whole
catalog: a plugin can be in the catalog and left for the user to fetch.

**5. Nothing else.** In particular, do not add a config entry anywhere. An installer that enabled a
plugin would be enabling code the user was never asked about; cxagent reports it as
present-but-unconfigured and they decide.

> The plugin's name appears in the workflow and the installer by hand, in about six places. That is
> fine for a catalog this size and is the first thing worth generating from `plugins.json` if it
> grows.
