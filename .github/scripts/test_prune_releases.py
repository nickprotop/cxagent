"""Tests for prune_releases.py: python3 -m unittest discover -s .github/scripts -v"""
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import prune_releases as pr  # noqa: E402


class Plan(unittest.TestCase):
    def test_keeps_the_newest_three_by_version_not_by_text(self):
        kept, deleted = pr.plan(["v0.9.9", "v0.9.10", "v0.9.43", "v0.9.44", "v0.10.0"], 3, "v0.10.0")
        self.assertEqual(kept, ["v0.10.0", "v0.9.44", "v0.9.43"])
        self.assertEqual(deleted, ["v0.9.10", "v0.9.9"])

    def test_never_deletes_the_tag_this_run_is_for(self):
        # A re-run for an older tag, after newer releases exist: the sort alone would delete it.
        kept, deleted = pr.plan(["v1.0.0", "v1.0.1", "v1.0.2", "v0.9.0"], 3, "v0.9.0")
        self.assertIn("v0.9.0", kept)
        self.assertNotIn("v0.9.0", deleted)

    def test_leaves_releases_with_other_tag_names_alone(self):
        kept, deleted = pr.plan(["v1.0.0", "nightly", "v1.0.1-rc1", "plugin-x", "v0.1.0", "v0.2.0", "v0.3.0"], 3, "v1.0.0")
        self.assertEqual(deleted, ["v0.1.0"])
        for other in ["nightly", "v1.0.1-rc1", "plugin-x"]:
            self.assertNotIn(other, kept + deleted)

    def test_three_or_fewer_deletes_nothing(self):
        self.assertEqual(pr.plan(["v0.1.0", "v0.2.0"], 3, "v0.2.0"), (["v0.2.0", "v0.1.0"], []))


class DryRun(unittest.TestCase):
    def test_lists_what_it_keeps_and_deletes_and_deletes_nothing(self):
        with tempfile.TemporaryDirectory() as folder:
            tags = Path(folder) / "tags.txt"
            tags.write_text("v0.9.40\nv0.9.41\nv0.9.42\nv0.9.43\n")
            deleted = []
            exit_code = pr.main(["--repo", "nickprotop/cxagent", "--current", "v0.9.43", "--dry-run",
                                 "--tags-from", str(tags)], run=deleted.append)
        self.assertEqual(exit_code, 0)
        self.assertEqual(deleted, [])

    def test_without_dry_run_deletes_exactly_the_planned_tags(self):
        with tempfile.TemporaryDirectory() as folder:
            tags = Path(folder) / "tags.txt"
            tags.write_text("v0.9.40\nv0.9.41\nv0.9.42\nv0.9.43\nv0.9.44\n")
            deleted = []
            pr.main(["--repo", "nickprotop/cxagent", "--current", "v0.9.44", "--tags-from", str(tags)],
                    run=deleted.append)
        self.assertEqual(deleted, ["v0.9.41", "v0.9.40"])


if __name__ == "__main__":
    unittest.main()
