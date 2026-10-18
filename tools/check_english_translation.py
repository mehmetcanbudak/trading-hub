#!/usr/bin/env python3
"""Check the working tree's English guides, source candidates and reviewed C# structure.

Run from any directory: python3 tools/check_english_translation.py
Uses the existing UiSnapshot/Roslyn dependencies, without starting the app or reading secrets.
Language detection is a regression heuristic, not proof that arbitrary text is English.
"""
from __future__ import annotations

from pathlib import Path
import re
import shutil
import subprocess
import sys
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]
LINK_RE = re.compile(r"!?\[[^\]\n]*\]\(([^)\n]+)\)")


def without_fences(content: str) -> str:
    return re.sub(r"^```[^\n]*\n.*?^```[ \t]*$", "", content, flags=re.MULTILINE | re.DOTALL)


def headings(content: str) -> list[str]:
    return re.findall(r"^#{1,6}\s+(.+?)\s*#*\s*$", without_fences(content), re.MULTILINE)


def slug(title: str) -> str:
    title = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", title)
    title = re.sub(r"<[^>]*>", "", title)
    return re.sub(r"\s", "-", re.sub(r"[^\w\s-]", "", title).strip().lower())


def anchors(content: str) -> set[str]:
    seen: dict[str, int] = {}
    result = set()
    for title in headings(content):
        key = slug(title)
        count = seen.get(key, 0)
        result.add(key if not count else f"{key}-{count}")
        seen[key] = count + 1
    result.update(re.findall(r'<a\s+(?:id|name)=[\"\']([^\"\']+)', content))
    return result


def validate_documentation() -> int:
    files = [ROOT / "README.md", ROOT / "CLAUDE.md", ROOT / "docs/README.md", *sorted((ROOT / "docs/en").glob("*.md"))]
    errors = []
    links = 0
    for file in files:
        for raw in LINK_RE.findall(without_fences(file.read_text(encoding="utf-8"))):
            href = raw.strip().split(" ", 1)[0].strip("<>")
            parsed = urlsplit(href)
            if parsed.scheme or parsed.netloc:
                continue
            target = file.parent / unquote(parsed.path) if parsed.path else file
            links += 1
            if not target.exists():
                errors.append(f"{file.relative_to(ROOT)} -> {href}: missing target")
            elif parsed.fragment and target.suffix == ".md" and unquote(parsed.fragment) not in anchors(target.read_text(encoding="utf-8")):
                errors.append(f"{file.relative_to(ROOT)} -> {href}: missing heading")
    pairs = [(p, ROOT / "docs/en" / p.name) for p in sorted((ROOT / "docs").glob("*.md")) if p.name not in {"README.md", "README.es.md"}]
    pairs += [(ROOT / "INSTALAR_CON_CLAUDE_CODE.md", ROOT / "docs/en/instalar-con-claude-code.md"),
              (ROOT / "PORTAR_A_WINDOWS.md", ROOT / "docs/en/portar-a-windows.md")]
    for original, english in pairs:
        if not english.exists():
            errors.append(f"Missing English guide: {english.relative_to(ROOT)}")
        elif len(headings(english.read_text(encoding="utf-8"))) < len(headings(original.read_text(encoding="utf-8"))):
            errors.append(f"{english.relative_to(ROOT)}: fewer sections than its source guide")
    print(f"English documentation: {len(files)} files, {links} local links/anchors, {len(pairs)} guide section checks.", flush=True)
    for error in errors:
        print("FAIL", error, flush=True)
    return len(errors)


def validate_source() -> int:
    dotnet = shutil.which("dotnet") or str(Path.home() / ".dotnet/dotnet")
    result = subprocess.run([dotnet, "run", "--project", str(ROOT / "tools/TradingHub.UiSnapshot"),
                             "--configuration", "Release", "--", "--check-english", str(ROOT)], cwd=ROOT, check=False)
    return int(result.returncode != 0)


def self_check() -> None:
    assert headings("# One\n```csharp\n# not a heading\n```\n## Two\n") == ["One", "Two"]
    assert slug("Claude Code (optional)") == "claude-code-optional"
    assert anchors("# Same\n## Same\n") == {"same", "same-1"}
    assert LINK_RE.findall("![Image](img/test.png) [Link](guide.md#section)") == ["img/test.png", "guide.md#section"]


if __name__ == "__main__":
    try:
        self_check()
        failures = validate_documentation() + validate_source()
        print("English localization checks: " + ("PASS" if not failures else "FAIL"), flush=True)
        sys.exit(1 if failures else 0)
    except (OSError, ValueError, AssertionError) as exc:
        print(f"Validation unavailable: {exc}", file=sys.stderr)
        sys.exit(2)
