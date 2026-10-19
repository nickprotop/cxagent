"""Tests for plugin_submission.py: python3 -m unittest discover -s .github/scripts -v"""
import json
import re
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import plugin_submission as ps  # noqa: E402

REPO = Path(__file__).resolve().parents[2]

ENTRY = {
    "name": "weather", "displayName": "weather", "version": "1.0.0",
    "description": "Looks up the weather.", "publisher": "you", "license": "MIT",
    "repository": "https://github.com/you/weather",
    "source": {"kind": "url", "url": "https://github.com/you/weather/releases/download/v1.0.0/weather.zip",
               "sha256": "a" * 64},
    "file": "weather.dll", "kind": "managed",
    "compatibility": {"pluginContract": 4, "platforms": ["any"]},
    "spawns": False, "tools": [{"name": "weather_search", "gated": "dynamic"}],
}

CATALOG = {"plugins": [
    {"name": "calculator", "directory": "calculator", "version": "0.9.37",
     "repository": "https://github.com/nickprotop/cxagent"},
    {"name": "weather", "version": "1.9.0", "repository": "https://github.com/you/weather"},
]}


def form_body(entry_text, notes="_No response_"):
    """What GitHub writes for the form: each field under its label, the JSON field fenced."""
    return f"### Catalog entry\n\n```json\n{entry_text}\n```\n\n### Notes for the reviewer\n\n{notes}"


def with_(**changes):
    entry = json.loads(json.dumps(ENTRY))
    entry.update(changes)
    return entry


class ExtractEntry(unittest.TestCase):
    def test_the_forms_entry_is_read(self):
        entry, problem = ps.extract_entry(form_body(json.dumps(ENTRY, indent=2)))
        self.assertIsNone(problem)
        self.assertEqual(entry, ENTRY)

    def test_an_authors_own_fences_are_stripped(self):
        entry, problem = ps.extract_entry(form_body("```json\n" + json.dumps(ENTRY) + "\n```"))
        self.assertIsNone(problem)
        self.assertEqual(entry["name"], "weather")

    def test_crlf_is_read(self):
        entry, problem = ps.extract_entry(form_body(json.dumps(ENTRY, indent=2)).replace("\n", "\r\n"))
        self.assertIsNone(problem)
        self.assertEqual(entry["name"], "weather")

    def test_text_after_the_json_is_a_problem(self):
        entry, problem = ps.extract_entry(form_body(json.dumps(ENTRY) + "\nthanks!"))
        self.assertIsNone(entry)
        self.assertIn("not JSON", problem)
        self.assertIn("line 2", problem)

    def test_no_section_names_the_form(self):
        entry, problem = ps.extract_entry("please add my plugin")
        self.assertIsNone(entry)
        self.assertIn("Submit a plugin", problem)

    def test_an_empty_field_is_a_problem(self):
        entry, problem = ps.extract_entry(form_body("_No response_"))
        self.assertIsNone(entry)
        self.assertIn("empty", problem)

    def test_a_whole_plugins_json_is_refused(self):
        entry, problem = ps.extract_entry(form_body(json.dumps({"plugins": [ENTRY]})))
        self.assertIsNone(entry)
        self.assertIn("whole plugins.json", problem)

    def test_an_array_is_refused(self):
        entry, problem = ps.extract_entry(form_body(json.dumps([ENTRY])))
        self.assertIsNone(entry)
        self.assertIn("one JSON object", problem)

    def test_an_entry_with_no_name_is_refused(self):
        entry, problem = ps.extract_entry(form_body(json.dumps(with_(name=""))))
        self.assertIsNone(entry)
        self.assertIn("name", problem)


class CatalogRules(unittest.TestCase):
    def test_a_new_name_passes(self):
        self.assertEqual(ps.catalog_problems(with_(name="sunrise"), CATALOG), [])

    def test_a_first_party_name_is_refused(self):
        problems = ps.catalog_problems(with_(name="calculator"), CATALOG)
        self.assertEqual(len(problems), 1)
        self.assertIn("this repository builds", problems[0])

    def test_another_repositorys_name_is_refused(self):
        problems = ps.catalog_problems(with_(repository="https://github.com/else/weather", version="2.0.0"), CATALOG)
        self.assertIn("https://github.com/you/weather", problems[0])

    def test_an_update_compares_versions_as_numbers(self):
        self.assertEqual(ps.catalog_problems(with_(version="1.10.0"), CATALOG), [])

    def test_the_same_version_is_refused(self):
        self.assertIn("must be higher", ps.catalog_problems(with_(version="1.9.0"), CATALOG)[0])

    def test_a_lower_version_is_refused(self):
        self.assertIn("must be higher", ps.catalog_problems(with_(version="1.8.5"), CATALOG)[0])

    def test_trailing_zeros_are_the_same_version(self):
        self.assertIn("must be higher", ps.catalog_problems(with_(version="1.9"), CATALOG)[0])

    def test_a_version_that_is_not_numbers_cannot_be_compared(self):
        self.assertIn("numbers separated by dots", ps.catalog_problems(with_(version="2.0.0-beta"), CATALOG)[0])


class MergedCatalog(unittest.TestCase):
    def test_a_new_entry_is_appended(self):
        merged = json.loads(ps.merged_catalog(json.dumps(CATALOG), with_(name="sunrise")))
        self.assertEqual([p["name"] for p in merged["plugins"]], ["calculator", "weather", "sunrise"])

    def test_an_update_replaces_in_place(self):
        merged = json.loads(ps.merged_catalog(json.dumps(CATALOG), with_(version="2.0.0")))
        self.assertEqual([p["name"] for p in merged["plugins"]], ["calculator", "weather"])
        self.assertEqual(merged["plugins"][1]["version"], "2.0.0")

    def test_the_real_catalog_round_trips_byte_for_byte(self):
        # A MERGE CHANGES ONLY THE ENTRY: plugins.json is written by publish.sh with the same dump,
        # so reading and writing it back is the identity, and the PR's diff is the entry alone.
        text = (REPO / "plugins" / "plugins.json").read_text(encoding="utf-8")
        self.assertEqual(json.dumps(json.loads(text), indent=2) + "\n", text)
        merged = json.loads(ps.merged_catalog(text, with_(name="sunrise")))
        self.assertEqual(merged["plugins"][:-1], json.loads(text)["plugins"])


import tempfile as _tempfile

OK = (0, json.dumps({"ok": True, "problems": []}))


class FakeRunner:
    """Records every command; answers by the first matching prefix of the joined arguments."""

    def __init__(self, **replies):
        self.calls = []
        self.replies = {
            "gh pr list": (0, "[]"),
            "cxagent plugin verify": OK,
            "git diff --cached --quiet": (1, ""),
            "gh pr create": (0, "https://github.com/nickprotop/cxagent/pull/9\n"),
        }
        self.replies.update({k.replace("_", " "): v for k, v in replies.items()})

    def run(self, args, check=True):
        self.calls.append(args)
        joined = " ".join(args)
        for prefix, reply in self.replies.items():
            if joined.startswith(prefix):
                return reply
        return 0, ""

    def ran(self, prefix):
        return [c for c in self.calls if " ".join(c).startswith(prefix)]


def event(entry=ENTRY, action="opened", labels=("plugin-submission",), number=7):
    return {"action": action, "repository": {"default_branch": "master"},
            "issue": {"number": number, "body": form_body(json.dumps(entry)),
                      "labels": [{"name": n} for n in labels]}}


class Handle(unittest.TestCase):
    def setUp(self):
        self.root = _tempfile.mkdtemp()
        (Path(self.root) / "plugins").mkdir()
        self.catalog = Path(self.root) / "plugins" / "plugins.json"
        self.catalog.write_text(json.dumps(CATALOG, indent=2) + "\n", encoding="utf-8")

    def comment(self, runner):
        calls = runner.ran("gh issue comment")
        self.assertEqual(len(calls), 1)
        return calls[0][calls[0].index("--body") + 1]

    def test_a_passing_entry_opens_a_pr(self):
        runner = FakeRunner()
        self.assertEqual(ps.handle(event(with_(name="sunrise")), runner, self.root), "passed")
        self.assertIn("--https-only", runner.ran("cxagent plugin verify")[0])
        self.assertTrue(runner.ran("git checkout -B plugin-submission/7 origin/master"))
        self.assertTrue(runner.ran("git push --force origin plugin-submission/7"))
        self.assertTrue(runner.ran("gh pr create --base master --head plugin-submission/7"))
        self.assertIn("https://github.com/nickprotop/cxagent/pull/9", self.comment(runner))
        names = [p["name"] for p in json.loads(self.catalog.read_text())["plugins"]]
        self.assertEqual(names, ["calculator", "weather", "sunrise"])

    def test_verifys_problems_are_commented_one_per_line(self):
        refused = (1, json.dumps({"ok": False, "problems": [
            {"entry": "sunrise", "field": "source.sha256", "message": "is aaa, but the zip hashes to bbb."},
            {"entry": "sunrise", "field": "tools", "message": "leaves out 'x'."}]}))
        runner = FakeRunner(**{"cxagent_plugin_verify": refused})
        self.assertEqual(ps.handle(event(with_(name="sunrise")), runner, self.root), "refused")
        text = self.comment(runner)
        self.assertIn("\n- source.sha256: is aaa, but the zip hashes to bbb.", text)
        self.assertIn("\n- tools: leaves out 'x'.", text)
        self.assertFalse(runner.ran("git"))

    def test_a_catalog_problem_never_reaches_verify(self):
        runner = FakeRunner()
        self.assertEqual(ps.handle(event(with_(name="calculator")), runner, self.root), "refused")
        self.assertFalse(runner.ran("cxagent"))
        self.assertIn("this repository builds", self.comment(runner))

    def test_a_failing_rerun_closes_the_open_pr(self):
        open_pr = (0, json.dumps([{"number": 9, "state": "OPEN", "url": "u"}]))
        runner = FakeRunner(**{"gh_pr_list": open_pr})
        ps.handle(event(with_(name="calculator"), action="edited"), runner, self.root)
        self.assertTrue(runner.ran("gh pr close 9"))

    def test_a_passing_rerun_updates_the_open_pr(self):
        open_pr = (0, json.dumps([{"number": 9, "state": "OPEN", "url": "https://x/pull/9"}]))
        runner = FakeRunner(**{"gh_pr_list": open_pr})
        self.assertEqual(ps.handle(event(with_(name="sunrise"), action="edited"), runner, self.root), "passed")
        self.assertTrue(runner.ran("gh pr edit 9"))
        self.assertFalse(runner.ran("gh pr create"))

    def test_a_closed_pr_is_reopened_by_a_passing_rerun(self):
        closed_pr = (0, json.dumps([{"number": 9, "state": "CLOSED", "url": "https://x/pull/9"}]))
        runner = FakeRunner(**{"gh_pr_list": closed_pr})
        ps.handle(event(with_(name="sunrise"), action="edited"), runner, self.root)
        self.assertTrue(runner.ran("gh pr reopen 9"))

    def test_an_unchanged_entry_makes_no_commit(self):
        open_pr = (0, json.dumps([{"number": 9, "state": "OPEN", "url": "https://x/pull/9"}]))
        runner = FakeRunner(**{"gh_pr_list": open_pr, "git_diff_--cached_--quiet": (0, "")})
        self.assertEqual(ps.handle(event(with_(name="sunrise"), action="edited"), runner, self.root), "passed")
        self.assertFalse(runner.ran("git commit"))
        self.assertFalse(runner.ran("git push"))

    def test_closing_the_issue_closes_the_pr_and_checks_nothing(self):
        open_pr = (0, json.dumps([{"number": 9, "state": "OPEN", "url": "u"}]))
        runner = FakeRunner(**{"gh_pr_list": open_pr})
        self.assertEqual(ps.handle(event(action="closed"), runner, self.root), "closed")
        self.assertTrue(runner.ran("gh pr close 9"))
        self.assertFalse(runner.ran("cxagent"))
        self.assertFalse(runner.ran("gh issue comment"))

    def test_an_issue_without_the_label_is_left_alone(self):
        runner = FakeRunner()
        self.assertEqual(ps.handle(event(labels=("bug",)), runner, self.root), "not a submission")
        self.assertEqual(runner.calls, [])

    def test_verify_output_that_is_not_json_fails_the_run(self):
        runner = FakeRunner(**{"cxagent_plugin_verify": (2, "usage: …")})
        with self.assertRaises(ps.BotError):
            ps.handle(event(with_(name="sunrise")), runner, self.root)
        self.assertFalse(runner.ran("gh issue comment"))


WORKFLOWS = REPO / ".github" / "workflows"


class Workflows(unittest.TestCase):
    def test_no_workflow_interpolates_an_issue_field(self):
        # AN ISSUE IS ANYONE'S TEXT. ${{ github.event.issue.body }} in a run: step is pasted into the
        # shell before it runs; the script reads the event file instead.
        for path in WORKFLOWS.glob("*.yml"):
            text = path.read_text(encoding="utf-8")
            for field in ("issue.body", "issue.title", "comment.body", "pull_request.title", "pull_request.body"):
                self.assertNotIn(f"github.event.{field}", text, f"{path.name} interpolates {field}")

    def test_the_bot_and_catalog_ci_verify_https_only(self):
        self.assertIn("plugin_submission.py", (WORKFLOWS / "plugin-submission.yml").read_text(encoding="utf-8"))
        self.assertIn("--https-only", (WORKFLOWS / "catalog.yml").read_text(encoding="utf-8"))

    def test_only_the_bot_asks_to_write(self):
        for path in WORKFLOWS.glob("*.yml"):
            if path.name in ("plugin-submission.yml", "release.yml", "pages.yml"):
                continue
            self.assertIsNone(re.search(r":\s*write\b", path.read_text(encoding="utf-8")), path.name)

    def test_the_form_names_the_heading_the_script_reads(self):
        form = (REPO / ".github" / "ISSUE_TEMPLATE" / "submit_a_plugin.yml").read_text(encoding="utf-8")
        self.assertIn("label: Catalog entry", form)
        self.assertEqual(ps.ENTRY_HEADING, "### Catalog entry")
        self.assertIn('labels: ["plugin-submission"]', form)


if __name__ == "__main__":
    unittest.main()
