# Publishing a plugin

*From a plugin that passes `check` to an entry in the catalog every cxagent user can install from.*

The catalog is [`plugins/plugins.json`](https://github.com/nickprotop/cxagent/blob/master/plugins/plugins.json). Each entry says where a plugin's
zip is, its sha256, and what it claims to be: name, version, tools and their gates, contract,
platforms. cxagent reads it to offer plugins in `/plugin`. Your zips stay where you put them — the
catalog only points at them.

## 1. Pack

```
cxagent plugin pack <folder> --out dist                                   # C#
cxagent plugin pack build --native build/wordcount.so=linux-x64 --out dist  # C, one --native per platform
```

`pack` runs `check` first and refuses a plugin with any problem. It writes one zip (one per
platform for native) and `<name>.catalog.json`, with each URL left as `<upload URL>`. The text fields
come from your csproj or `--info catalog-info.yaml`; a field still `<fill in: …>` is refused later.

## 2. Upload

Put the zips somewhere they stay, at an `https://` URL. A GitHub release in your repository is the
usual place:

```
gh release create v1.0.0 dist/*.zip
```

Then replace each `<upload URL>` in `<name>.catalog.json` with that zip's URL.

## 3. Verify what a user would install

```
cxagent plugin verify --https-only dist/<name>.catalog.json
```

It downloads each zip, checks the sha256, unpacks it and holds the sidecar and files up against every
field the entry claims. No plugin code runs. `--https-only` refuses a local path, as the bot does:
leave it off to check an entry pointing at zips on disk before you upload.

## 4. Submit

**Through the issue** (recommended): open
[Submit a plugin](https://github.com/nickprotop/cxagent/issues/new/choose) and paste
`<name>.catalog.json`. A bot then:

- runs the same `verify --https-only`, and comments the result — each problem on its own line;
- when it passes, opens a pull request adding the entry, linked to your issue;
- on each edit of the issue, checks again: a passing edit updates the pull request, a failing one
  closes it until a later edit passes.

A maintainer reviews the pull request and merges it; merging closes your issue.

**Or as a pull request** adding your entry to `plugins/plugins.json`. CI runs
`verify --https-only` on every third-party entry in the file.

## Names and updates

| Your entry's name | Already in the catalog | Result |
|---|---|---|
| new | no | added |
| a plugin this repository builds | yes | refused: choose another name |
| yours | from the same `repository` | an update, if its version is higher (`1.10.0` beats `1.9.0`) |
| someone else's | from another `repository` | refused; a maintainer can decide otherwise on the issue |

An update replaces your entry in place. Its version must be higher: cxagent offers an update whenever
the version differs, so an equal or lower one would be offered as an update that changes nothing.

## What review checks

Whether the entry is truthful and useful: that the plugin exists, its entry matches its sidecar, and
its description is honest. It is not a security review. cxagent asks before loading any plugin,
showing a hash of everything it would load, and that is the boundary a user relies on.
