#!/usr/bin/env python3
"""Prepare a standard-Unity package mirror without Tuanjie-encrypted metadata."""
from __future__ import annotations

import shutil
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "packages" / "com.digital-kotone.toolkit"
LOCAL_PACKAGES = ROOT / "apps" / "ar-photo-unity" / "LocalPackages"
TARGET = LOCAL_PACKAGES / "com.digital-kotone.toolkit"


def ignore_metadata(_directory: str, names: list[str]) -> set[str]:
    return {name for name in names if name.endswith(".meta")}


def main() -> int:
    expected_parent = LOCAL_PACKAGES.resolve()
    target = TARGET.resolve()
    if target.parent != expected_parent:
        raise RuntimeError(f"Refusing to replace unexpected path: {target}")
    if not (SOURCE / "package.json").is_file():
        raise RuntimeError(f"Toolkit package is missing: {SOURCE}")
    if TARGET.exists():
        shutil.rmtree(TARGET)
    LOCAL_PACKAGES.mkdir(parents=True, exist_ok=True)
    shutil.copytree(SOURCE, TARGET, ignore=ignore_metadata)
    source_count = sum(1 for path in SOURCE.rglob("*") if path.is_file() and path.suffix != ".meta")
    target_count = sum(1 for path in TARGET.rglob("*") if path.is_file())
    if target_count != source_count or any(TARGET.rglob("*.meta")):
        raise RuntimeError("Standard-Unity package mirror verification failed")
    print(f"Prepared {target_count} source files at {TARGET}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
