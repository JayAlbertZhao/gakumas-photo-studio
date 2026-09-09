#!/usr/bin/env python3
"""Import private Resources into an ignored, local-only Unity folder."""
import argparse
import hashlib
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]
ENVIRONMENT_NAMES = {'CapturedActorEnvironment.bytes', 'CapturedEyeEnvironment.bytes'}


def supported(relative: Path) -> bool:
    name = relative.name.removesuffix('.meta')
    return (len(relative.parts) == 1 and name in ENVIRONMENT_NAMES) or (
        len(relative.parts) == 2 and relative.parts[0] == 'ReadableBodyMeshes'
        and name.endswith('.asset'))


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    args = parser.parse_args()
    source = args.source.resolve(strict=True)
    destination = ROOT / 'unity/Assets/PrivateResources/Resources'
    planned = []
    for path in sorted(source.rglob('*')):
        if path.is_symlink() or getattr(path.lstat(), 'st_file_attributes', 0) & 0x400:
            raise ValueError('Private resource tree contains a link; refusing traversal')
        if not path.is_file():
            continue
        relative = path.relative_to(source)
        if not supported(relative):
            continue
        target = destination / relative
        for parent in (target, *target.parents):
            if parent == ROOT.parent:
                break
            if parent.exists() and (parent.is_symlink() or
                    getattr(parent.lstat(), 'st_file_attributes', 0) & 0x400):
                raise ValueError('Destination contains a link')
        if target.exists() and digest(target) != digest(path):
            raise ValueError('Different private resource already exists; refusing overwrite')
        planned.append((path, target))
    if not planned:
        raise ValueError('No supported private resource files found')
    for path, target in planned:
        target.parent.mkdir(parents=True, exist_ok=True)
        if not target.exists():
            shutil.copy2(path, target)
        if digest(path) != digest(target):
            raise ValueError('Private resource copy hash mismatch')
    print(f'Imported/verified {len(planned)} private resource files; do not publish Player builds.')


if __name__ == '__main__':
    main()
