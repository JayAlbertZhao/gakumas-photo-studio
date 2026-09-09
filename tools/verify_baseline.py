#!/usr/bin/env python3
"""Check the historical baseline plus explicitly recorded runtime revisions."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def digest(data: bytes) -> str:
    # Git's CRLF/LF conversion is the only permitted normalization.
    return hashlib.sha256(data.replace(b'\r\n', b'\n')).hexdigest()


def resolve_expected(baseline: dict, revisions: dict | None = None) -> tuple[dict, list]:
    """Resolve reviewed hashes without changing the historical lock."""
    findings = []
    expected_files = dict(baseline.get('files', {}))
    if revisions is not None:
        if revisions.get('base_archive_sha256') != baseline.get('archive_sha256'):
            findings.append({'file': 'config/runtime-revisions.json', 'rule': 'wrong_revision_base'})
        for name, change in revisions.get('replacements', {}).items():
            if name not in expected_files or change.get('baseline_sha256') != expected_files[name]:
                findings.append({'file': name, 'rule': 'invalid_revision_parent'})
            else:
                expected_files[name] = change['sha256']
        for name, expected in revisions.get('additions', {}).items():
            if name in baseline['files']:
                findings.append({'file': name, 'rule': 'revision_addition_already_exists'})
            else:
                expected_files[name] = expected
    return expected_files, findings


def verify(root: Path = ROOT) -> dict:
    baseline = json.loads((root / 'config/source-baseline.json').read_text(encoding='utf-8'))
    revision_path = root / 'config/runtime-revisions.json'
    revisions = json.loads(revision_path.read_text(encoding='utf-8')) if revision_path.exists() else None
    expected_files, findings = resolve_expected(baseline, revisions)
    for name, expected in expected_files.items():
        # The lock is data, not permission to read outside the repository.
        if not name.startswith('unity/') or '..' in Path(name).parts or ':' in name or '\\' in name:
            findings.append({'file': name, 'rule': 'unsafe_revision_path'})
            continue
        path = root / name
        if not path.is_file():
            findings.append({'file': name, 'rule': 'missing_baseline_file'})
        elif digest(path.read_bytes()) != expected:
            findings.append({'file': name, 'rule': 'baseline_mismatch'})
    for path in (root / 'unity/Assets').rglob('*'):
        if path.suffix in {'.cs', '.shader', '.cginc', '.hlsl', '.asmdef'}:
            name = path.relative_to(root).as_posix()
            if name not in expected_files:
                findings.append({'file': name, 'rule': 'unexpected_unity_source'})
    return {'schema': 'photo-studio.baseline-check.v1', 'accepted': not findings,
            'files': len(expected_files), 'historical_files': len(baseline['files']), 'findings': findings}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT)
    args = parser.parse_args()
    report = verify(args.root.resolve())
    print(json.dumps(report, indent=2))
    return 0 if report['accepted'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
