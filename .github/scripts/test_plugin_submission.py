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


if __name__ == "__main__":
    unittest.main()
