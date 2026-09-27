#!/usr/bin/env python3
"""Stage and run the synthetic Open Swing versus toolkit Unity replay.

The workspace is caller-owned and should be placed on a drive with enough room
for Unity's Library cache.  Only source, synthetic JSON, and generated project
metadata are copied; no game assets or private configuration are involved.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[1]
REFERENCE = ROOT / "references" / "forks" / "open-swing"
TOOLKIT = ROOT / "packages" / "com.digital-kotone.toolkit"
UNITY_TEMPLATE = ROOT / "unity"
PROBE = ROOT / "tools" / "replay" / "RuntimeDynamicsReplayProbe.cs"


def copy_tree_without_meta(source: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    for item in source.rglob("*"):
        if item.name.endswith(".meta"):
            continue
        relative = item.relative_to(source)
        target = destination / relative
        if item.is_dir():
            target.mkdir(parents=True, exist_ok=True)
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(item, target)


def stage_project(workspace: Path) -> None:
    workspace.mkdir(parents=True, exist_ok=True)
    copy_tree_without_meta(UNITY_TEMPLATE / "Assets", workspace / "Assets")
    copy_tree_without_meta(UNITY_TEMPLATE / "ProjectSettings", workspace / "ProjectSettings")

    package_root = workspace / "Packages" / "com.digital-kotone.toolkit"
    copy_tree_without_meta(TOOLKIT, package_root)
    manifest = json.loads((UNITY_TEMPLATE / "Packages" / "manifest.json").read_text(encoding="utf-8"))
    manifest["dependencies"]["com.digital-kotone.toolkit"] = "file:com.digital-kotone.toolkit"
    packages = workspace / "Packages"
    packages.mkdir(parents=True, exist_ok=True)
    (packages / "manifest.json").write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )

    project_version = workspace / "ProjectSettings" / "ProjectVersion.txt"
    project_version.write_text(
        "m_EditorVersion: 2022.3.57f1\n"
        "m_EditorVersionWithRevision: 2022.3.57f1 (4e1b0f82c39a)\n",
        encoding="utf-8",
    )
    copy_tree_without_meta(REFERENCE / "Runtime", workspace / "Assets" / "OpenSwing")
    shutil.copy2(REFERENCE / "Samples~" / "Hair" / "ExampleChain.json", workspace / "Assets" / "ExampleChain.json")
    editor = workspace / "Assets" / "Editor"
    editor.mkdir(parents=True, exist_ok=True)
    shutil.copy2(PROBE, editor / PROBE.name)


def parse_runtime_receipt(log_text: str) -> dict:
    match = re.search(
        r"RUNTIME_DYNAMICS_REPLAY_OK\s+"
        r"referenceNodes=(?P<reference_nodes>\d+)\s+"
        r"oursEntries=(?P<independent_entries>\d+)\s+"
        r"oursSegments=(?P<independent_segments>\d+)\s+"
        r"terminalCandidates=(?P<terminal_candidates>\d+)\s+"
        r"terminalProxies=(?P<terminal_proxies>\d+)\s+"
        r"steps=(?P<steps>\d+)\s+"
        r"tipDelta0=(?P<tip_delta_0>[0-9.eE+-]+)\s+"
        r"tipDelta1=(?P<tip_delta_1>[0-9.eE+-]+)\s+"
        r"tipDelta2=(?P<tip_delta_2>[0-9.eE+-]+)\s+"
        r"maxTipDelta=(?P<max_tip_delta>[0-9.eE+-]+)\s+"
        r"step=(?P<fixed_step>[0-9.eE+-]+)\s+"
        r"capsuleInvalidAxis=(?P<capsule_invalid_axis>[^\s]+)\s+"
        r"skirtRootMathAngleDelta=(?P<skirt_root_math_angle_delta>[0-9.eE+-]+)\s+"
        r"resetFinite=(?P<reset_finite>True|False)\s+"
        r"resetMaxTipDelta=(?P<reset_max_tip_delta>[0-9.eE+-]+)",
        log_text,
    )
    if match is None:
        raise ValueError("Unity log has no runtime dynamics replay receipt")
    values = match.groupdict()
    return {
        "schema": "photo-studio.runtime-comparison.v1",
        "status": "passed",
        "reference_nodes": int(values["reference_nodes"]),
        "independent_entries": int(values["independent_entries"]),
        "independent_segments": int(values["independent_segments"]),
        "terminal_candidates": int(values["terminal_candidates"]),
        "terminal_proxies": int(values["terminal_proxies"]),
        "steps": int(values["steps"]),
        "tip_deltas": [
            float(values["tip_delta_0"]),
            float(values["tip_delta_1"]),
            float(values["tip_delta_2"]),
        ],
        "max_tip_delta": float(values["max_tip_delta"]),
        "fixed_step": float(values["fixed_step"]),
        "capsule_invalid_axis": values["capsule_invalid_axis"],
        "skirt_root_math_angle_delta": float(values["skirt_root_math_angle_delta"]),
        "reset_finite": values["reset_finite"] == "True",
        "reset_max_tip_delta": float(values["reset_max_tip_delta"]),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--unity-editor", type=Path, required=True)
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--log-file", type=Path)
    args = parser.parse_args()
    if not args.unity_editor.is_file():
        parser.error(f"Unity editor not found: {args.unity_editor}")
    stage_project(args.workspace)
    log_file = args.log_file or args.workspace / "runtime-replay.log"
    command = [
        str(args.unity_editor),
        "-batchmode",
        "-nographics",
        "-quit",
        "-projectPath",
        str(args.workspace),
        "-executeMethod",
        "RuntimeDynamicsReplayProbe.Run",
        "-logFile",
        str(log_file),
    ]
    completed = subprocess.run(command, cwd=ROOT, check=False)
    if completed.returncode != 0:
        return completed.returncode
    if not log_file.is_file():
        print("Unity exited without creating a runtime replay log", file=sys.stderr)
        return 1
    try:
        receipt = parse_runtime_receipt(log_file.read_text(encoding="utf-8", errors="replace"))
    except ValueError as error:
        print(str(error), file=sys.stderr)
        return 1
    print(json.dumps(receipt, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
