from copy import deepcopy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import check_upstream as tracker

FORK = "example/english-fork"
STATE = {"fork_repository": FORK, "upstream_repository": "original/app", "upstream_branch": "main",
         "english_branch": "feat/english-localization", "integrated_commit": "a" * 40}


def data(tip="a" * 40, pending=False):
    return {"tip": tip, "commits": [tip + "\tNew feature"] if pending else [],
            "files": ["src/TradingHub.Core/Trading/Orders.cs", "docs/new-feature.md"] if pending else [],
            "diverged": False, "code_present": not pending}


class FakeGithub:
    def __init__(self):
        self.issues = []
        self.comments = []
        self.calls = []
        self.fail_patch = False

    def __call__(self, method, path, payload=None, paginate=False):
        assert path.startswith(f"repos/{FORK}/issues"), "A write/read escaped the fork issue routes"
        self.calls.append((method, path, deepcopy(payload)))
        if method == "GET":
            return [deepcopy(self.comments if "/comments?" in path else self.issues)]
        if method == "POST" and path.endswith("/comments"):
            self.comments.append(deepcopy(payload))
            return payload
        if method == "POST":
            issue = {**payload, "number": 1, "state": "open", "html_url": "https://github.com/example/english-fork/issues/1"}
            self.issues.append(deepcopy(issue))
            return issue
        if method == "PATCH":
            if self.fail_patch:
                self.fail_patch = False
                raise OSError("Fixture transient PATCH failure")
            self.issues[0].update(payload)
            return self.issues[0]
        raise AssertionError((method, path))


class NotificationTests(unittest.TestCase):
    def setUp(self):
        self.env = patch.dict(os.environ, {"GITHUB_REPOSITORY": FORK})
        self.env.start()
        self.addCleanup(self.env.stop)
        self.api = FakeGithub()

    def notify(self, result):
        return tracker.notify(STATE, result, tracker.render(STATE, result, "2026-10-19T00:00:00+00:00"), self.api)

    def test_first_run_creates_one_assigned_dashboard_and_unchanged_tip_is_quiet(self):
        self.notify(data())
        self.notify(data())
        self.assertEqual(1, len(self.api.issues))
        self.assertEqual(["example"], self.api.issues[0]["assignees"])
        self.assertEqual([], self.api.comments)
        self.assertIn("Up to date", self.api.issues[0]["body"])

    def test_new_tip_notifies_once_and_preserves_notes_and_checked_items(self):
        self.notify(data())
        self.api.issues[0]["body"] = self.api.issues[0]["body"].replace("- [ ] Review", "- [x] Review") + "\nMy note: keep this.\n"
        self.api.issues[0]["state"] = "closed"
        self.notify(data("b" * 40, True))
        self.notify(data("b" * 40, True))
        self.assertEqual(1, len(self.api.comments))
        self.assertIn("- [x] Review", self.api.issues[0]["body"])
        self.assertIn("My note: keep this.", self.api.issues[0]["body"])
        self.assertEqual("open", self.api.issues[0]["state"])

    def test_retry_after_comment_and_failed_body_update_does_not_duplicate_alert(self):
        self.notify(data())
        self.api.fail_patch = True
        with self.assertRaises(OSError):
            self.notify(data("b" * 40, True))
        self.notify(data("b" * 40, True))
        self.assertEqual(1, len(self.api.comments))

    def test_wrong_repository_and_original_upstream_can_never_receive_writes(self):
        with patch.dict(os.environ, {"GITHUB_REPOSITORY": "original/app"}):
            with self.assertRaises(ValueError):
                self.notify(data("b" * 40, True))
            wrong = {**STATE, "fork_repository": "original/app"}
            with self.assertRaises(ValueError):
                tracker.notify(wrong, data(), "report", self.api)
        self.assertEqual([], self.api.calls)

    def test_duplicate_dashboards_fail_instead_of_spamming(self):
        self.notify(data())
        self.api.issues.append(deepcopy(self.api.issues[0]))
        with self.assertRaises(ValueError):
            self.notify(data("b" * 40, True))
        self.assertEqual([], self.api.comments)

    def test_missing_markers_append_but_damaged_markers_never_erase_notes(self):
        self.assertIn("Manual notes", tracker.managed_body("Manual notes", "report"))
        for body in ("Manual notes " + tracker.START, tracker.END + tracker.START, tracker.START * 2 + tracker.END):
            with self.assertRaises(ValueError):
                tracker.managed_body(body, "report")

    def test_report_includes_doc_mapping_critical_paths_and_no_injected_mentions(self):
        result = data("b" * 40, True)
        result["commits"] = [result["tip"] + "\tfix @unrelated-user <tag> `code`\x1b[31m"]
        report = tracker.render(STATE, result, "fixture-time")
        self.assertIn("docs/en/new-feature.md", report)
        self.assertIn("Trading/data/API compatibility", report)
        self.assertNotIn("@unrelated-user", report)
        self.assertNotIn("<tag>", report)
        self.assertNotIn("\x1b", report)
        self.assertEqual("README.es.md + README.md", tracker.english_doc("README.md"))
        self.assertEqual("docs/en/new-feature.md", tracker.english_doc("docs/en/new-feature.md"))

    def test_report_is_bounded_without_hiding_that_more_changes_exist(self):
        result = data("b" * 40, True)
        result["commits"] *= 200
        result["files"] = ["docs/" + str(i) + "x" * 1000 + ".md" for i in range(300)]
        report = tracker.render(STATE, result, "fixture-time")
        self.assertLess(len(report), 60_000)
        self.assertIn("More commits", report)
        self.assertIn("More files", report)


class GitDetectionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.git("init", "--initial-branch", STATE["english_branch"])
        self.git("config", "user.name", "Tracker Test")
        self.git("config", "user.email", "tracker@example.invalid")
        self.git("config", "core.autocrlf", "false")
        self.commit("base.txt", "base")
        self.base = self.git("rev-parse", "HEAD").strip()
        self.state = {**STATE, "integrated_commit": self.base}

    def git(self, *args):
        return tracker.git(self.root, *args)

    def commit(self, path, content):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8")
        self.git("add", "--", path)
        self.git("commit", "-m", "Fixture change")

    def test_no_change_and_new_upstream_commit_and_later_review_checkpoint(self):
        result = tracker.inspect(self.root, self.state, self.base)
        self.assertEqual([], result["commits"])
        self.assertFalse(result["diverged"])
        self.git("switch", "-c", "incoming")
        self.commit("docs/feature.md", "# New feature")
        tip = self.git("rev-parse", "HEAD").strip()
        self.git("switch", STATE["english_branch"])
        result = tracker.inspect(self.root, self.state, tip)
        self.assertEqual(1, len(result["commits"]))
        self.assertEqual(["docs/feature.md"], result["files"])
        self.assertFalse(result["code_present"])
        self.git("merge", "--ff-only", "incoming")
        self.assertTrue(tracker.inspect(self.root, self.state, tip)["code_present"])
        self.assertEqual([], tracker.inspect(self.root, {**self.state, "integrated_commit": tip}, tip)["commits"])
        self.assertEqual(self.base, self.state["integrated_commit"], "Detection must never advance the checkpoint")

    def test_upstream_history_rewrite_is_flagged_not_reset(self):
        self.git("switch", "--orphan", "rewritten")
        self.commit("new-root.txt", "new history")
        tip = self.git("rev-parse", "HEAD").strip()
        self.git("switch", STATE["english_branch"])
        self.assertTrue(tracker.inspect(self.root, self.state, tip)["diverged"])
        self.assertEqual(self.base, self.git("rev-parse", "HEAD").strip())

    def test_checkpoint_must_be_incorporated_in_english_branch(self):
        self.git("switch", "-c", "incoming")
        self.commit("extra.txt", "unmerged")
        tip = self.git("rev-parse", "HEAD").strip()
        self.git("switch", STATE["english_branch"])
        with self.assertRaises(ValueError):
            tracker.inspect(self.root, {**self.state, "integrated_commit": tip}, tip)

    def test_ledger_validation_rejects_target_confusion_and_invalid_revisions(self):
        target = self.root / ".github/upstream-english.json"
        target.parent.mkdir()
        target.write_text(json.dumps(self.state), encoding="utf-8")
        self.assertEqual(self.state, tracker.load_state(self.root))
        for change in ({"fork_repository": "original/app"}, {"integrated_commit": "main"}, {"upstream_repository": "../.."}, {"english_branch": "--bad"}):
            target.write_text(json.dumps({**self.state, **change}), encoding="utf-8")
            with self.assertRaises((ValueError, subprocess.CalledProcessError)):
                tracker.load_state(self.root)

    def test_actual_repository_checkpoint_is_valid_and_in_current_history(self):
        state = tracker.load_state(tracker.ROOT)
        self.assertTrue(tracker.ancestor(tracker.ROOT, state["integrated_commit"], "HEAD"))
        workflow = (tracker.ROOT / ".github/workflows/upstream-english-tracking.yml").read_text(encoding="utf-8")
        self.assertIn("ref: " + state["english_branch"], workflow)
        self.assertIn("github.repository == '" + state["fork_repository"] + "'", workflow)
        self.assertIn("contents: read", workflow)
        self.assertIn("issues: write", workflow)
        self.assertNotIn("contents: write", workflow)


if __name__ == "__main__":
    unittest.main()
