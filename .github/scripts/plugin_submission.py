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


BOT_NAME = "github-actions[bot]"
BOT_EMAIL = "41898282+github-actions[bot]@users.noreply.github.com"


class Runner:
    """Runs a command in the checkout. An argument list, never a shell: nothing from the issue is
    ever parsed as shell syntax."""

    def __init__(self, cwd):
        self.cwd = cwd

    def run(self, args, check=True):
        done = subprocess.run(args, cwd=self.cwd, capture_output=True, text=True)
        if check and done.returncode != 0:
            raise BotError(f"{' '.join(args[:3])} exited {done.returncode}: {done.stderr.strip()[:500]}")
        return done.returncode, done.stdout


def verify_problems(code, stdout):
    """verify's --json answer as one "<field>: <message>" line per problem. Exit 1 is the entry's
    fault; anything else, or output that is not that JSON, is the bot's."""
    if code == 0:
        return []
    try:
        result = json.loads(stdout)
    except json.JSONDecodeError:
        raise BotError(f"verify exited {code} with output that is not JSON: {stdout[:500]}")
    if code != 1 or not isinstance(result, dict) or not result.get("problems"):
        raise BotError(f"verify exited {code}: {stdout[:500]}")
    return [f"{p['field']}: {p['message']}" if p.get("field") else p["message"] for p in result["problems"]]


def find_pr(runner, branch):
    """The newest PR from this issue's branch, open, closed or merged; None if there never was one."""
    _, out = runner.run(["gh", "pr", "list", "--head", branch, "--state", "all", "--json", "number,state,url"])
    prs = json.loads(out or "[]")
    return max(prs, key=lambda p: p["number"]) if prs else None


def refused_comment(name, problems, run_url):
    lines = [f"**{name} is not ready yet.** Fix these, then edit this issue to check it again:", ""]
    lines += [f"- {p}" for p in problems]
    if run_url:
        lines += ["", f"[This check's log]({run_url})"]
    return "\n".join(lines)


def passed_comment(entry, pr_url, run_url):
    lines = [f"**Verified.** {entry['name']} {entry['version']}: every zip downloaded over https, its sha256 "
             "matched, and its contents agree with the entry. No plugin code ran.", "",
             f"The pull request adding it is {pr_url}. A maintainer reviews and merges it; editing this "
             "issue checks the entry again and updates the pull request."]
    if run_url:
        lines += ["", f"[This check's log]({run_url})"]
    return "\n".join(lines)


def pr_body(entry, number, updating):
    platforms = ", ".join(entry.get("compatibility", {}).get("platforms", []))
    tools = ", ".join(f"{t['name']} (gated {t['gated']})" for t in entry.get("tools", []))
    return "\n".join([
        f"{'Updates' if updating else 'Adds'} **{entry['name']}** {entry['version']} in the plugin catalog, from #{number}.",
        "",
        f"- Repository: {entry.get('repository')}",
        f"- Publisher: {entry.get('publisher')}",
        f"- License: {entry.get('license')}",
        f"- Kind: {entry.get('kind')}, platforms: {platforms}",
        f"- Tools: {tools}",
        "",
        "Verified by the submission bot: each zip downloaded over https, its sha256 matched, and its sidecar "
        "and files agree with the entry. No plugin code ran. Review asks whether the entry is truthful and "
        "useful; see cxagent.Core/docs/publishing-plugins.md.",
        "",
        f"Closes #{number}",
    ])


def comment(runner, number, text):
    runner.run(["gh", "issue", "comment", str(number), "--body", text])


def handle(event, runner, root, cxagent="cxagent", run_url=""):
    """One run of the bot for one issue event."""
    issue = event["issue"]
    if LABEL not in [label["name"] for label in issue.get("labels", [])]:
        return "not a submission"

    number = issue["number"]
    branch = f"plugin-submission/{number}"
    base = event["repository"]["default_branch"]
    pr = find_pr(runner, branch)
    is_open = pr is not None and pr["state"] == "OPEN"

    if event.get("action") == "closed":
        if is_open:
            runner.run(["gh", "pr", "close", str(pr["number"]), "--comment", f"Closed with #{number}."])
        return "closed"

    catalog_path = Path(root) / "plugins" / "plugins.json"
    catalog_text = catalog_path.read_text(encoding="utf-8")
    catalog = json.loads(catalog_text)

    entry, problem = extract_entry(issue.get("body"))
    problems = [problem] if problem else catalog_problems(entry, catalog)
    if not problems:
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "entry.json"
            path.write_text(json.dumps(entry), encoding="utf-8")
            code, out = runner.run([cxagent, "plugin", "verify", "--https-only", "--json", str(path)], check=False)
        problems = verify_problems(code, out)

    if problems:
        # AN OPEN PR ALWAYS MEANS THE ISSUE'S CURRENT ENTRY PASSES: one that no longer does is closed,
        # and a later passing edit reopens it.
        if is_open:
            runner.run(["gh", "pr", "close", str(pr["number"]), "--comment",
                        f"Closed: the entry in #{number} no longer passes; see the issue."])
        comment(runner, number, refused_comment(entry["name"] if entry else "This submission", problems, run_url))
        return "refused"

    updating = any(p.get("name") == entry["name"] for p in catalog["plugins"])
    title = f"{'Update' if updating else 'Add'} {entry['name']} {entry['version']} in the plugin catalog"
    runner.run(["git", "checkout", "-B", branch, f"origin/{base}"])
    catalog_path.write_text(merged_catalog(catalog_text, entry), encoding="utf-8")
    runner.run(["git", "add", "plugins/plugins.json"])

    # AN EDIT TO THE NOTES ALONE CHANGES NOTHING TO COMMIT; committing anyway would fail the run.
    changed, _ = runner.run(["git", "diff", "--cached", "--quiet"], check=False)
    if changed != 0:
        runner.run(["git", "-c", f"user.name={BOT_NAME}", "-c", f"user.email={BOT_EMAIL}", "commit", "-m", title])
        runner.run(["git", "push", "--force", "origin", branch])

    body = pr_body(entry, number, updating)
    if pr is not None and pr["state"] in ("OPEN", "CLOSED"):
        if pr["state"] == "CLOSED":
            runner.run(["gh", "pr", "reopen", str(pr["number"])])
        runner.run(["gh", "pr", "edit", str(pr["number"]), "--title", title, "--body", body])
        url = pr["url"]
    else:
        # NONE YET, OR THE LAST ONE MERGED: a merged PR cannot be reopened, so an update is a new one.
        _, out = runner.run(["gh", "pr", "create", "--base", base, "--head", branch, "--title", title, "--body", body])
        url = out.strip().splitlines()[-1]

    comment(runner, number, passed_comment(entry, url, run_url))
    return "passed"


def main():
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
    server = os.environ.get("GITHUB_SERVER_URL", "https://github.com")
    run_url = f"{server}/{os.environ.get('GITHUB_REPOSITORY', '')}/actions/runs/{os.environ.get('GITHUB_RUN_ID', '')}"
    try:
        print(handle(event, Runner(os.getcwd()), os.getcwd(), os.environ.get("CXAGENT", "cxagent"), run_url))
    except BotError as e:
        print(f"::error::{e}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
