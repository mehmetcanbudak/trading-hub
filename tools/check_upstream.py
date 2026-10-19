#!/usr/bin/env python3
"""Report upstream changes; --notify maintains one issue in this fork, never merges code."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
TITLE = "[English] Upstream synchronization tracker"
START = "<!-- english-upstream:begin -->"
END = "<!-- english-upstream:end -->"
REF = "refs/remotes/english-tracker/upstream"
CHECKLIST = """## Human review checklist

- [ ] Review all pending upstream commits, including trading/data/security changes.
- [ ] Merge into a separate sync branch based on `feat/english-localization`; resolve conflicts manually.
- [ ] Translate new/changed UI, errors, prompts, reports, SDK labels and generated sample code.
- [ ] Update English guides and screenshots; preserve the Spanish originals and attribution.
- [ ] Preserve account/order IDs, broker payloads, storage formats and existing personal scripts.
- [ ] Run build/tests, English/source-structure audits and isolated UI/safety scenarios; inspect renders.
- [ ] Record actual evidence in TRANSLATION_STATUS and advance integrated_commit only after review.
- [ ] Open a PR into this fork's English branch, review/merge it, then install separately after backup.

Checklist items are not proof of review: the ledger checkpoint remains authoritative.
Recheck completed items whenever a new upstream tip is reported.

## Maintainer notes

This section and the checklist are preserved when the bot refreshes its managed report.
"""


def command(args: list[str], root: Path, **kwargs) -> str:
    return subprocess.run(args, cwd=root, text=True, encoding="utf-8", check=True, timeout=120,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, **kwargs).stdout


def git(root: Path, *args: str) -> str:
    return command(["git", *args], root)


def ancestor(root: Path, older: str, newer: str) -> bool:
    result = subprocess.run(["git", "merge-base", "--is-ancestor", older, newer], cwd=root,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode not in (0, 1):
        raise ValueError("Cannot verify commit ancestry; fetch full history and check the ledger.")
    return result.returncode == 0


def load_state(root: Path) -> dict[str, str]:
    state = json.loads((root / ".github/upstream-english.json").read_text(encoding="utf-8"))
    required = {"fork_repository", "upstream_repository", "upstream_branch", "english_branch", "integrated_commit"}
    if set(state) != required or not all(isinstance(value, str) for value in state.values()):
        raise ValueError("Invalid upstream ledger fields.")
    for key in ("fork_repository", "upstream_repository"):
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}", state[key]) or state[key].split("/")[1] in {".", ".."}:
            raise ValueError("Invalid repository identity in upstream ledger.")
    if state["fork_repository"] == state["upstream_repository"]:
        raise ValueError("Notifications must target a fork, not the upstream repository.")
    if not re.fullmatch(r"[0-9a-f]{40}", state["integrated_commit"]):
        raise ValueError("integrated_commit must be a full upstream SHA-1.")
    for key in ("upstream_branch", "english_branch"):
        git(root, "check-ref-format", "--branch", state[key])
    return state


def inspect(root: Path, state: dict[str, str], tip: str) -> dict:
    cursor = state["integrated_commit"]
    if not ancestor(root, cursor, state["english_branch"]):
        raise ValueError("Ledger checkpoint is not incorporated in the English branch.")
    commits = [record.strip("\r\n") for record in git(root, "log", "--reverse", "--format=%H%x09%s%x00", f"{cursor}..{tip}").split("\0") if record.strip("\r\n")]
    files = git(root, "diff", "--no-renames", "--name-only", "-z", cursor, tip).split("\0")
    return {"tip": git(root, "rev-parse", tip).strip(), "commits": commits,
            "files": sorted(path for path in files if path),
            "diverged": not ancestor(root, cursor, tip),
            "code_present": ancestor(root, tip, state["english_branch"])}


def inline(value: str) -> str:
    value = " ".join(re.sub(r"[\x00-\x1f\x7f]", " ", value).split())
    value = (value[:157] + "…" if len(value) > 160 else value).replace("@", "@\u200b").replace("<", "&lt;").replace(">", "&gt;")
    fence = "`" * (max((len(m.group()) for m in re.finditer(r"`+", value)), default=0) + 1)
    return f"{fence} {value} {fence}"


def english_doc(path: str) -> str:
    special = {"README.md": "README.es.md + README.md", "CLAUDE.md": "CLAUDE.es.md + CLAUDE.md",
               "INSTALAR_CON_CLAUDE_CODE.md": "docs/en/instalar-con-claude-code.md",
               "PORTAR_A_WINDOWS.md": "docs/en/portar-a-windows.md"}
    return special.get(path, "docs/en/" + path[5:] if path.startswith("docs/") and not path.startswith("docs/en/") else path)


def render(state: dict[str, str], data: dict, checked: str) -> str:
    upstream, cursor, tip = state["upstream_repository"], state["integrated_commit"], data["tip"]
    status = "HISTORY DIVERGED — check the ledger/upstream rewrite; do not reset automatically" if data["diverged"] else (
        f"{len(data['commits'])} upstream commits need English review" if data["commits"] else "Up to date")
    lines = ["# Upstream → English review queue", "", f"**Status:** {status}", f"**Checked (UTC):** {checked}",
             f"**Upstream:** {upstream}:{state['upstream_branch']}", f"**English branch:** {state['english_branch']}",
             f"**Last incorporated and reviewed:** `{cursor}`", f"**Latest upstream:** `{tip}`",
             f"<!-- english-upstream-tip:{tip} -->", "",
             f"[Complete upstream comparison](https://github.com/{upstream}/compare/{cursor}...{tip})", "",
             "The monitor fetches/reports only. It never merges, translates, changes the checkpoint or deploys."]
    if data["commits"] and data["code_present"]:
        lines += ["", "Upstream code is already in the English branch, but its translation-review checkpoint has not advanced."]
    lines += ["", f"## Pending commits ({len(data['commits'])})"]
    for commit in data["commits"][:50]:
        sha, subject = commit.split("\t", 1)
        lines.append(f"- [{sha[:10]}](https://github.com/{upstream}/commit/{sha}) {inline(subject)}")
    if len(data["commits"]) > 50:
        lines.append("- More commits are included in the complete comparison above.")
    groups = {
        "Code/text review — inspect labels, errors, prompts, SDK and sample generators": [p for p in data["files"] if p.startswith("src/")],
        "Documentation — update the corresponding English surface": [p for p in data["files"] if p.endswith(".md")],
        "Trading/data/API compatibility — review behavior, not just wording": [p for p in data["files"] if p.startswith(("src/TradingHub.Core/", "src/TradingHub.Strategies/", "src/TradingHub.Sdk/"))],
        "Tests, tooling, packaging and other changes": [p for p in data["files"] if not p.startswith("src/") and not p.endswith(".md")],
    }
    for title, paths in groups.items():
        lines += ["", f"## {title} ({len(paths)})"]
        lines += [f"- {inline(p)}" + (f" → {inline(english_doc(p))}" if p.endswith(".md") else "") for p in paths[:30]] or ["- None."]
        if len(paths) > 30:
            lines.append("- More files are included in the complete comparison above.")
    lines += ["", "These are review candidates, not proof that a line needs translation. Never translate persisted identifiers blindly.",
              "Do not replace the historical source-audit baseline just to silence a new upstream change."]
    return "\n".join(lines) + "\n"


def managed_body(body: str, report: str) -> str:
    section = START + "\n" + report + END
    if START not in body and END not in body:
        return body.rstrip() + "\n\n" + section + "\n"
    if body.count(START) != 1 or body.count(END) != 1 or body.index(START) > body.index(END):
        raise ValueError("Tracker issue markers are damaged; refusing to overwrite maintainer notes.")
    return body[:body.index(START)] + section + body[body.index(END) + len(END):]


def github(method: str, path: str, payload=None, paginate=False):
    args = ["gh", "api", "--method", method, path]
    if paginate:
        args += ["--paginate", "--slurp"]
    if payload is not None:
        args += ["--input", "-"]
    return json.loads(command(args, ROOT, input=json.dumps(payload) if payload is not None else None))


def notify(state: dict[str, str], data: dict, report: str, api=github) -> str:
    fork = state["fork_repository"]
    if fork == state["upstream_repository"] or os.environ.get("GITHUB_REPOSITORY") != fork:
        raise ValueError("Notification writes require GITHUB_REPOSITORY to match a separate fork ledger.")
    route = f"repos/{fork}/issues"
    issues = [issue for page in api("GET", route + "?state=all&per_page=100", paginate=True) for issue in page]
    matches = [issue for issue in issues if "pull_request" not in issue and issue["title"] == TITLE]
    if len(matches) > 1:
        raise ValueError("Multiple tracker issues found; resolve duplicates before notifying.")
    if not matches:
        issue = api("POST", route, {"title": TITLE, "body": managed_body("", report).lstrip() + "\n" + CHECKLIST, "assignees": [fork.split("/")[0]]})
        return issue["html_url"]
    issue = matches[0]
    path = route + "/" + str(issue["number"])
    pending = bool(data["commits"]) or data["diverged"]
    marker = f"<!-- english-upstream-tip:{data['tip']} -->"
    if pending and marker not in (issue.get("body") or ""):
        comments = [comment for page in api("GET", path + "/comments?per_page=100", paginate=True) for comment in page]
        if not any(marker in comment["body"] for comment in comments):
            # Comment first: a failed body refresh must not lose or duplicate the notification on retry.
            api("POST", path + "/comments", {"body": f"@{fork.split('/')[0]} new upstream work needs English review. Recheck the checklist for this tip.\n\n" + report})
    payload = {"body": managed_body(issue.get("body") or "", report)}
    if pending and issue.get("state") == "closed":
        payload["state"] = "open"
    api("PATCH", path, payload)
    return issue["html_url"]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--notify", action="store_true", help="Maintain this fork's GitHub tracker issue (requires gh and its scoped token).")
    args = parser.parse_args()
    state = load_state(ROOT)
    if git(ROOT, "branch", "--show-current").strip() != state["english_branch"]:
        raise ValueError("Run the monitor from the English branch, not a port/main checkout.")
    git(ROOT, "fetch", "--no-tags", f"https://github.com/{state['upstream_repository']}.git",
        f"+refs/heads/{state['upstream_branch']}:{REF}")
    data = inspect(ROOT, state, REF)
    report = render(state, data, datetime.now(timezone.utc).isoformat(timespec="seconds"))
    print(report, flush=True)
    if args.notify:
        print("Tracker issue: " + notify(state, data, report))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, subprocess.CalledProcessError, subprocess.TimeoutExpired) as exc:
        detail = exc.stderr.strip() if isinstance(exc, subprocess.CalledProcessError) and exc.stderr else str(exc)
        print(f"Upstream tracking failed: {detail}", file=sys.stderr)
        sys.exit(2)
