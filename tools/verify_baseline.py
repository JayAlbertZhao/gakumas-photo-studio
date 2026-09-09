#!/usr/bin/env python3
"""Check the frozen pre-cleanup Unity sources, without changing any runtime file."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def digest(data: bytes) -> str:
    # Git's CRLF/LF conversion is the only permitted normalization.
    return hashlib.sha256(data.replace(b'\r\n', b'\n')).hexdigest()


def verify(root: Path = ROOT) -> dict:
    baseline = json.loads((root / 'config/source-baseline.json').read_text(encoding='utf-8'))
    findings = []
    for name, expected in baseline['files'].items():
        path = root / name
        if not path.is_file():
            findings.append({'file': name, 'rule': 'missing_baseline_file'})
        elif digest(path.read_bytes()) != expected:
            findings.append({'file': name, 'rule': 'baseline_mismatch'})
    for path in (root / 'unity/Assets').rglob('*'):
        if path.suffix in {'.cs', '.shader', '.asmdef'}:
            name = path.relative_to(root).as_posix()
            if name not in baseline['files']:
                findings.append({'file': name, 'rule': 'unexpected_unity_source'})
    return {'schema': 'photo-studio.baseline-check.v1', 'accepted': not findings,
            'files': len(baseline['files']), 'findings': findings}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT)
    args = parser.parse_args()
    report = verify(args.root.resolve())
    print(json.dumps(report, indent=2))
    return 0 if report['accepted'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
