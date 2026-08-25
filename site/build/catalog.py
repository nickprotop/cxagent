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
    parser.add_argument("--asset", required=True, type=Path,
                       help="the release artifact to hash")
    parser.add_argument("--out", required=True, type=Path,
                       help="where to write the published catalog")
    parser.add_argument("--plugin", default="csharp-lsp",
                       help="the entry whose source.sha256 this asset fills in")
    args = parser.parse_args()

    # LOUD, NOT NULL. A catalog published with sha256 still null looks exactly like the committed
    # file, so the one thing this script exists to add would be missing with nothing to show for it.
    if not args.asset.is_file():
        print(f"error: no artifact at '{args.asset}' -- refusing to publish a catalog "
              f"without the hash it exists to carry.", file=sys.stderr)
        return 1

    catalog = json.loads(args.catalog.read_text(), object_pairs_hook=OrderedDict)

    # THE MAINTAINER COMMENT DOES NOT SHIP. It is 60 lines telling whoever edits the source file
    # how to edit it, which a client fetching this over the network pays for on every request and
    # can do nothing with.
    catalog.pop("$comment", None)

    digest = sha256_of(args.asset)
    stamped = False
    for entry in catalog.get("plugins", []):
        if entry.get("name") == args.plugin:
            entry.setdefault("source", OrderedDict())["sha256"] = digest
            stamped = True

    if not stamped:
        print(f"error: no catalog entry named '{args.plugin}'.", file=sys.stderr)
        return 1

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(catalog, indent=2) + "\n")
    print(f"{args.plugin}: {digest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
