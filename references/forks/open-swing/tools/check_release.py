"""Fail closed on private paths, character assets, and captured data."""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
TEXT_SUFFIXES = {".cs", ".py", ".json", ".md", ".txt", ".asmdef"}
ALLOWED_SPECIAL = {"LICENSE", ".gitignore"}
FORBIDDEN_SUFFIXES = {
    ".fbx", ".blend", ".prefab", ".unity", ".asset", ".bundle", ".ab",
    ".png", ".jpg", ".jpeg", ".webp", ".tga", ".psd", ".wav",
    ".ogg", ".mp3", ".mp4", ".mov", ".anim", ".controller", ".mat",
    ".dll", ".exe", ".pdb", ".zip", ".7z",
}
CONTENT_PATTERNS = [
    ("absolute drive path", re.compile(r"(?i)(?<![a-z])[a-z]:[/\\]")),
    ("absolute home path", re.compile(r"(?i)/(?:home|Users|mnt)/[^\s'\"<>]+")),
    ("revision-style label", re.compile(r"(?i)(?<![a-z0-9])v[0-9]+(?![0-9])")),
    ("credential marker", re.compile(r"(?i)(?:ghp_[A-Za-z0-9]{20}|github_pat_|-----BEGIN (?:RSA |OPENSSH )?PRIVATE KEY-----)")),
    ("email address", re.compile(r"(?i)\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b")),
]


def main() -> int:
    problems = []
    files = [p for p in ROOT.rglob("*") if p.is_file() and ".git" not in p.parts]
    for path in files:
        rel = path.relative_to(ROOT)
        if path.is_symlink():
            problems.append(f"symlink: {rel}")
            continue
        if path.suffix.lower() in FORBIDDEN_SUFFIXES:
            problems.append(f"asset/binary file: {rel}")
            continue
        if path.suffix.lower() not in TEXT_SUFFIXES and path.name not in ALLOWED_SPECIAL:
            problems.append(f"unexpected file type: {rel}")
            continue
        if path.stat().st_size > 200_000:
            problems.append(f"large file: {rel}")
        if rel.as_posix() == "tools/check_release.py":
            continue  # This file deliberately contains the search patterns.
        try:
            content = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            problems.append(f"non-UTF-8 text: {rel}")
            continue
        for label, pattern in CONTENT_PATTERNS:
            if pattern.search(content) or pattern.search(rel.as_posix()):
                problems.append(f"{label}: {rel}")
        if path.suffix.lower() == ".json" and path.stat().st_size > 50_000:
            problems.append(f"large JSON may contain extracted data: {rel}")
    if problems:
        print("RELEASE CHECK FAILED")
        for issue in problems:
            print("-", issue)
        return 1
    print(f"PASS: {len(files)} text files; no disallowed media, local paths, or credentials")
    return 0


if __name__ == "__main__":
    sys.exit(main())
