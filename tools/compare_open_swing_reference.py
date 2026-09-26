#!/usr/bin/env python3
"""Compare declared dynamics semantics without claiming behavioral equivalence.

This is a source-level checkpoint for the reproduction playground. It deliberately
reports divergences instead of silently adapting one solver to the other.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
REFERENCE = ROOT / "references/forks/open-swing/Runtime/Hair/ActorAnimationSwingSolver.cs"
OUR_SOLVER = ROOT / "packages/com.digital-kotone.toolkit/Runtime/HairDynamicsSystem.cs"


def require(pattern: str, text: str) -> bool:
    return re.search(pattern, text, re.MULTILINE) is not None


def number(pattern: str, text: str) -> float | None:
    match = re.search(pattern, text, re.MULTILINE)
    return float(match.group(1)) if match else None


def build_report() -> dict:
    reference = REFERENCE.read_text(encoding="utf-8")
    ours = OUR_SOLVER.read_text(encoding="utf-8")
    ref_step = number(r"public\s+const\s+float\s+NativeStep\s*=\s*([0-9.]+)f", reference)
    our_step = number(r"private\s+const\s+float\s+FixedDt\s*=\s*([0-9.]+)f", ours)
    ref_prewarm = number(r"prewarmSteps\s*=\s*([0-9]+)", reference)
    our_prewarm = number(r"private\s+const\s+int\s+PrewarmSteps\s*=\s*([0-9]+)", ours)

    checks = [
        {
            "id": "fixed-step",
            "status": "match" if ref_step == our_step == 0.01667 else "divergence",
            "reference": ref_step,
            "ours": our_step,
            "note": "The constants match textually; this does not prove identical integration order.",
        },
        {
            "id": "prewarm-count",
            "status": "match" if ref_prewarm == our_prewarm == 30 else "divergence",
            "reference": ref_prewarm,
            "ours": our_prewarm,
            "note": "Both declare thirty warm-up steps; state initialization still needs a replay.",
        },
        {
            "id": "caller-owned-step",
            "status": "match" if require(r"public\s+void\s+Step\s*\(", reference)
            and require(r"public\s+void\s+AdvanceSimulation\s*\(", ours)
            and require(r"automaticSimulation", ours) else "not-proven",
            "reference": "Step(deltaTime)",
            "ours": "AdvanceSimulation(deltaSeconds, timeSeconds)",
            "note": "Both expose an explicit stepping path; API and state contracts differ.",
        },
        {
            "id": "reset-boundary",
            "status": "match" if require(r"public\s+void\s+RequestReset\s*\(", reference)
            and require(r"public\s+void\s+ResetSimulation\s*\(", ours) else "not-proven",
            "reference": "RequestReset()",
            "ours": "ResetSimulation()",
            "note": "A matching method shape is not a proof that cached state is reset identically.",
        },
        {
            "id": "capsule-invalid-axis",
            "status": "divergence" if require(r"throw\s+new\s+InvalidOperationException\(\"Invalid native capsule axis", reference)
            and require(r"Mathf\.Clamp\(Mathf\.RoundToInt\(collider\.vector3_B\.x\)", ours)
            else "not-proven",
            "reference": "invalid axis throws",
            "ours": "axis is clamped to 0..2",
            "note": "This is an intentional reproduction gap; do not hide it behind the hook.",
        },
    ]
    return {
        "schema": "photo-studio.reference-comparison.v1",
        "reference": str(REFERENCE.relative_to(ROOT)).replace("\\", "/"),
        "ours": str(OUR_SOLVER.relative_to(ROOT)).replace("\\", "/"),
        "accepted": True,
        "equivalent": all(item["status"] == "match" for item in checks),
        "checks": checks,
        "limitations": [
            "This report is source-level and cannot replace a Unity runtime replay.",
            "The fork is a reference implementation, not an authority for the original game.",
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    report = build_report()
    encoded = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        args.output.write_text(encoded, encoding="utf-8")
    else:
        print(encoded, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
