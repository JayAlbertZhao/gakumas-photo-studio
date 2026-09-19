#!/usr/bin/env python3
"""Create a minimal private character-data directory for the Unity AR host.

The output intentionally stays outside Git. It contains only the selected actor,
motion, and their recursive AssetBundle dependencies plus optional face metadata.
"""
from __future__ import annotations

import argparse
import json
import shutil
from pathlib import Path


DEFAULT_SELECTION = (
    "mdl_chr_fktn-base-0000_face",
    "mdl_chr_fktn-cstm-0000_body",
    "mdl_chr_fktn-cstm-0000_hair",
    "mot_photo_chr_cmmn_stand-idle-001_lp",
)
OPTIONAL_METADATA = ("face-motions.json", "photo-facial-motions.json", "face-decals.json")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="private staging root containing staging-manifest.json")
    parser.add_argument("output", type=Path, help="new private character-data directory")
    parser.add_argument("--bundle", action="append", dest="bundles", help="bundle name; repeat to override defaults")
    return parser.parse_args()


def dependency_closure(records: dict[str, dict], selected: list[str]) -> set[str]:
    pending = list(selected)
    result: set[str] = set()
    while pending:
        name = pending.pop()
        if name in result:
            continue
        if name not in records:
            raise ValueError(f"bundle not found in staging manifest: {name}")
        result.add(name)
        pending.extend(records[name].get("dependencies") or ())
    return result


def main() -> int:
    args = parse_args()
    source = args.source.resolve()
    output = args.output.resolve()
    manifest_path = source / "staging-manifest.json"
    if not manifest_path.is_file():
        raise FileNotFoundError(manifest_path)
    if output == source or source in output.parents or output in source.parents:
        raise ValueError("source and output must be independent directories")
    if output.exists() and any(output.iterdir()):
        raise FileExistsError(f"output directory is not empty: {output}")

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    records = {record["name"]: record for record in manifest["bundles"]}
    selected = list(args.bundles or DEFAULT_SELECTION)
    names = dependency_closure(records, selected)
    ordered = [record for record in manifest["bundles"] if record["name"] in names]

    output.mkdir(parents=True, exist_ok=True)
    copied_bytes = 0
    for record in ordered:
        relative = Path(record["output_relative_path"])
        source_file = source / relative
        if not source_file.is_file():
            raise FileNotFoundError(source_file)
        target_file = output / relative
        target_file.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source_file, target_file)
        copied_bytes += target_file.stat().st_size

    for name in OPTIONAL_METADATA:
        source_file = source / name
        if source_file.is_file():
            shutil.copy2(source_file, output / name)

    private_manifest = {
        "schema_version": manifest.get("schema_version", "gakumas-photo-mode-assets/v1"),
        "character_id": manifest.get("character_id", "fktn"),
        "unity_version": manifest.get("unity_version"),
        "bundles": ordered,
        "missing_bundles": [],
        "voices": [],
    }
    (output / "staging-manifest.json").write_text(
        json.dumps(private_manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(f"Prepared {len(ordered)} bundles ({copied_bytes / 1024 / 1024:.1f} MiB) at {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
