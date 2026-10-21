#!/usr/bin/env python3
"""Turn the committed catalog into the published one.

THE HASH CANNOT LIVE IN THE COMMITTED FILE. plugins.json is committed before a release builds the
zip it describes, so it cannot contain that zip's hash -- see the design doc, "The problem". This
reads the committed catalog, hashes the artifact a release actually shipped, and writes a copy
carrying the value. Nothing is committed: the output goes into a Pages artifact, which never
enters git.

STANDARD LIBRARY ONLY. This runs in CI with no pip install step, and adding one would make a
release depend on an index being reachable.
"""

import argparse
import hashlib
import json
import sys
from collections import OrderedDict
from pathlib import Path


def sha256_of(path: Path) -> str:
    """Streamed rather than read whole: the artifact is a zip of unbounded size."""
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", required=True, type=Path,
                       help="the committed plugins.json")
    parser.add_argument("--out", required=True, type=Path,
                       help="where to write the published catalog")
    parser.add_argument("--plugin", action="append", default=[], metavar="NAME[@RID]=PATH",
                       help="a catalog entry and the release artifact whose hash fills its "
                            "source.sha256 -- or, as NAME@RID, its sources[RID].sha256; repeat "
                            "for each plugin and platform")
    parser.add_argument("--version", metavar="X.Y.Z",
                       help="cxagent's own version, published as cxagentVersion for the site to "
                            "show; omitted locally, where there is no release to name")
    args = parser.parse_args()

    # EVERY PLUGIN IN ONE PASS. Chaining invocations would mean each reading the previous one's
    # output, so a failure partway leaves a half-stamped catalog already written — and this script
    # exists to make that state impossible.
    # KEYED (name, rid): rid is None for an entry with one `source`, and names the platform for
    # an entry with per-platform `sources` -- each a different zip with its own hash.
    assets = {}
    for pair in args.plugin:
        key, _, path = pair.partition("=")
        name, _, rid = key.partition("@")
        if not name or not path:
            print(f"error: --plugin wants NAME=PATH or NAME@RID=PATH, got '{pair}'.", file=sys.stderr)
            return 1
        assets[(name, rid or None)] = Path(path)

    def label(name, rid):
        return f"{name}@{rid}" if rid else name

    if not assets:
        print("error: no --plugin given; nothing to stamp.", file=sys.stderr)
        return 1

    # LOUD, NOT NULL. A catalog published with sha256 still null looks exactly like the committed
    # file, so the one thing this script exists to add would be missing with nothing to show for it.
    for (name, rid), path in assets.items():
        if not path.is_file():
            print(f"error: no artifact at '{path}' for '{label(name, rid)}' -- refusing to publish a catalog "
                  f"without the hash it exists to carry.", file=sys.stderr)
            return 1

    catalog = json.loads(args.catalog.read_text(), object_pairs_hook=OrderedDict)

    # THE MAINTAINER COMMENT DOES NOT SHIP. It is 60 lines telling whoever edits the source file
    # how to edit it, which a client fetching this over the network pays for on every request and
    # can do nothing with.
    catalog.pop("$comment", None)

    # A url ENTRY IS ALREADY PINNED. Stamping one would replace a hash the maintainer committed
    # deliberately -- pinning a file this project does not control -- with the hash of whatever CI
    # happened to be handed.
    def source_of(entry, rid):
        return (entry.get("source") or {}) if rid is None else ((entry.get("sources") or {}).get(rid) or {})

    by_name = {entry.get("name"): entry for entry in catalog.get("plugins", [])}
    for (name, rid) in assets:
        entry = by_name.get(name)
        if entry is not None and source_of(entry, rid) and source_of(entry, rid).get("kind") != "release":
            print(f"error: '{label(name, rid)}' is not a 'release' source; its sha256 is committed and must not "
                  f"be overwritten.", file=sys.stderr)
            return 1

    stamped = set()
    for (name, rid), path in assets.items():
        entry = by_name.get(name)
        if entry is None:
            continue
        source = source_of(entry, rid)
        if not source:
            continue
        digest = sha256_of(path)
        source["sha256"] = digest
        stamped.add((name, rid))
        print(f"{label(name, rid)}: {digest}")

    missing = set(assets) - stamped
    if missing:
        print(f"error: no catalog entry or source named {', '.join(sorted(label(n, r) for n, r in missing))}.",
              file=sys.stderr)
        return 1

    # EVERY ENTRY LEAVES WITH A HASH, however it got one. An entry still null here is one a client
    # cannot verify, which is the whole reason this script exists — but the two source kinds get
    # there differently and only one of them is ours to stamp.
    # AN ENTRY USING PER-RID `sources` carries a hash per platform, and each one counts: a platform
    # left null is a download no client on that platform can verify.
    unstamped = []
    for e in catalog.get("plugins", []):
        if "sources" in e:
            unstamped += [f"{e.get('name')}@{rid}" for rid, source in e["sources"].items()
                          if (source or {}).get("sha256") is None]
        elif (e.get("source") or {}).get("sha256") is None:
            unstamped.append(e.get("name"))
    if unstamped:
        print(f"error: {', '.join(unstamped)} would publish with a null sha256. A 'release' source "
              f"needs --plugin NAME=PATH (or NAME@RID=PATH); a 'url' source must carry its hash in "
              f"plugins.json.", file=sys.stderr)
        return 1

    # CXAGENT'S OWN VERSION, WHICH IS NOT A PLUGIN'S. A plugin that did not change keeps its number
    # across releases, so plugins[0].version answers "which csharp-lsp is this?" and never "which
    # cxagent is this?" — the two agreed only until the first release that carried a plugin forward.
    # Published as its own key so a reader cannot mistake one for the other.
    if args.version:
        catalog["cxagentVersion"] = args.version

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(catalog, indent=2) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
