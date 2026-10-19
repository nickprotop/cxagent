#!/usr/bin/env python3
"""
The plugin-submission bot: reads a "Submit a plugin" issue, verifies the entry, comments, and opens
or updates the PR adding it to plugins/plugins.json.

EVERY DECISION IS HERE, NOT IN THE WORKFLOW, so the tests exercise it without opening issues. The
workflow installs cxagent and runs this file; this file reads the issue from the event file, never
from text pasted into a shell.

NO AUTHOR CODE RUNS. The entry is JSON, and `cxagent plugin verify` downloads, hashes and reads the
zips without loading anything in them.
"""
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

LABEL = "plugin-submission"
ENTRY_HEADING = "### Catalog entry"


class BotError(Exception):
    """Something the bot itself could not do — a failed git push, verify output it cannot read. The
    run fails loudly; the author is not told their entry is wrong for it."""


def extract_entry(body):
    """The entry under the form's "Catalog entry" heading: (entry, None), or (None, a problem)."""
    body = (body or "").replace("\r\n", "\n")
    start = body.find(ENTRY_HEADING)
    if start < 0:
        return None, ("the issue has no \"Catalog entry\" section; open it from the \"Submit a plugin\" "
                      "form at https://github.com/nickprotop/cxagent/issues/new/choose.")
    section = body[start + len(ENTRY_HEADING):]
    end = section.find("\n### ")
    if end >= 0:
        section = section[:end]

    # EVERY FENCE LINE GOES, not just the form's: the field renders as ```json, and authors paste
    # their own ```json … ``` into it as well. A fence line is never part of the JSON.
    text = "\n".join(line for line in section.split("\n") if not line.strip().startswith("```")).strip()
    if not text or text == "_No response_":
        return None, "the \"Catalog entry\" field is empty; paste the <name>.catalog.json that cxagent plugin pack wrote."
    try:
        entry = json.loads(text)
    except json.JSONDecodeError as e:
        return None, f"the catalog entry is not JSON: {e.msg} at line {e.lineno}, column {e.colno}."
    if isinstance(entry, dict) and "plugins" in entry:
        return None, "this is a whole plugins.json; paste the one entry cxagent plugin pack wrote."
    if not isinstance(entry, dict):
        return None, "the catalog entry must be one JSON object: the <name>.catalog.json that pack wrote."
    if not isinstance(entry.get("name"), str) or not entry["name"]:
        return None, "the catalog entry has no \"name\"."
    return entry, None


def version_key(version):
    """Numbers separated by dots, as a tuple; None for anything else."""
    if not isinstance(version, str) or not re.fullmatch(r"\d+(\.\d+)*", version):
        return None
    return tuple(int(part) for part in version.split("."))


def is_higher(new, old):
    """1.10 beats 1.9, and 1.9 equals 1.9.0: compared padded, as numbers."""
    width = max(len(new), len(old))
    return new + (0,) * (width - len(new)) > old + (0,) * (width - len(old))


def catalog_problems(entry, catalog):
    """What the catalog itself says against this entry: a name taken, or an update that is not one."""
    name = entry["name"]
    current = next((e for e in catalog.get("plugins", []) if e.get("name") == name), None)
    if current is None:
        return []
    if "directory" in current:
        return [f"name: '{name}' belongs to a plugin this repository builds; choose another name."]
    if current.get("repository") != entry.get("repository"):
        return [f"name: '{name}' is already listed from {current.get('repository')}; an update must come "
                "from that repository. A maintainer can decide otherwise on this issue."]
    new, old = version_key(entry.get("version")), version_key(current.get("version"))
    if new is None or old is None:
        return [f"version: an update is compared as numbers separated by dots, and '{entry.get('version')}' "
                f"against the listed '{current.get('version')}' cannot be."]
    if not is_higher(new, old):
        # A CLIENT OFFERS AN UPDATE WHENEVER THE VERSION DIFFERS, so an equal or lower one would be
        # offered to every user as an update that changes nothing, or goes backwards.
        return [f"version: '{name}' is listed at {current.get('version')}; an update must be higher."]
    return []


def merged_catalog(catalog_text, entry):
    """plugins.json with the entry replacing its namesake in place, or appended."""
    catalog = json.loads(catalog_text)
    plugins = catalog["plugins"]
    for i, existing in enumerate(plugins):
        if existing.get("name") == entry["name"]:
            plugins[i] = entry
            break
    else:
        plugins.append(entry)
    # THE DUMP publish.sh WRITES THE FILE WITH, so the PR's diff is the entry and nothing else.
    return json.dumps(catalog, indent=2) + "\n"
