#!/usr/bin/env python3
"""Read-only integrity check for a privately transferred local asset pack."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re

ROOT = Path(__file__).resolve().parents[1]


def digest(path: Path) -> str:
    result = hashlib.sha256()
    with path.open('rb') as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b''):
            result.update(block)
    return result.hexdigest()


def local_path(root: Path, name: str) -> Path:
    if not isinstance(name, str) or not name or '\\' in name or ':' in name:
        raise ValueError('Invalid pack path')
    parts = PurePosixPath(name).parts
    if name.startswith('/') or '..' in parts or str(PurePosixPath(name)) != name:
        raise ValueError('Pack path must be a normalized relative path')
    if not (name.startswith('LocalAssets/') or
            name.startswith('unity/Assets/PrivateResources/') or name == 'studio.local.json'):
        raise ValueError('Pack path outside local asset locations')
    path = root.joinpath(*parts)
    for part in (path, *path.parents):
        if part == root:
            break
        if os.path.lexists(part) and (part.is_symlink() or
                getattr(part.lstat(), 'st_file_attributes', 0) & 0x400):
            raise ValueError('Pack path contains a link')
    if not path.resolve().is_relative_to(root.resolve()):
        raise ValueError('Pack path escapes the project')
    return path


def verify(root: Path = ROOT) -> dict:
    report = {'schema': 'photo-studio.local-assets-check.v1', 'accepted': False,
              'checked_files': 0, 'findings': []}
    try:
        path = local_path(root, 'LocalAssets/PACKAGE.json')
        manifest = json.loads(path.read_text(encoding='utf-8-sig'))
        if not isinstance(manifest, dict) or manifest.get('schema') != 'photo-studio.local-assets.v1':
            raise ValueError('Unsupported local asset manifest')
        records = manifest.get('files')
        if not isinstance(records, list) or not records:
            raise ValueError('Asset manifest needs a nonempty files list')
        baseline = (root / 'config/source-baseline.json').read_bytes().replace(b'\r\n', b'\n')
        if hashlib.sha256(baseline).hexdigest() != manifest.get('source_baseline_sha256'):
            raise ValueError('Asset pack targets a different frozen source baseline')
        seen = set()
        for record in records:
            if not isinstance(record, dict):
                raise ValueError('Invalid asset record')
            name = record.get('path')
            path = local_path(root, name)
            if name.casefold() in seen or name == 'LocalAssets/PACKAGE.json':
                raise ValueError('Duplicate or self-referential asset record')
            seen.add(name.casefold())
            size, expected = record.get('bytes'), record.get('sha256')
            if (not isinstance(size, int) or isinstance(size, bool) or size < 0 or
                    not isinstance(expected, str) or not re.fullmatch('[0-9a-f]{64}', expected)):
                raise ValueError('Invalid asset hash or size')
            if not path.is_file():
                report['findings'].append({'path': name, 'rule': 'missing_file'})
            elif path.stat().st_size != size or digest(path) != expected:
                report['findings'].append({'path': name, 'rule': 'content_mismatch'})
            report['checked_files'] += 1
        report['accepted'] = not report['findings']
    except (OSError, ValueError) as error:
        report['findings'].append({'rule': 'invalid_pack', 'detail': str(error)})
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT, help='clone root, not LocalAssets')
    args = parser.parse_args()
    report = verify(args.root.resolve())
    print(json.dumps(report, ensure_ascii=True, indent=2))
    print('Checks transport integrity only; not asset rights, bundle compatibility or visual fidelity.')
    return 0 if report['accepted'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
