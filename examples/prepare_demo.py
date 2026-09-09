#!/usr/bin/env python3
"""Instantiate an original timeline template using a local dataset. Never overwrite."""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('studio', ROOT / 'studio.py')
studio = importlib.util.module_from_spec(spec)
spec.loader.exec_module(studio)
TEMPLATES = {
    'single': '01-single-motion.timeline.json',
    'transition': '02-motion-transition.timeline.json',
    'sequence': '03-motion-sequence.timeline.json',
}


def instantiate(template: str, first: str, second: str | None, manifest: dict) -> dict:
    if template not in TEMPLATES:
        raise ValueError('Unknown template')
    if template != 'single' and not second:
        raise ValueError('--next-motion is required for transition and sequence')
    if template == 'single' and second:
        raise ValueError('single uses only --motion')
    available = {record['name'] for record in manifest['bundles']
                 if record.get('role') == 'motion'}
    for motion in (first, second):
        if motion is not None and motion not in available:
            raise ValueError('Choose an exact name with role motion from studio.py catalog')
    timeline = studio.read_object(ROOT / 'examples' / TEMPLATES[template])
    replacements = {'YOUR_MOTION_ID': first, 'YOUR_FIRST_MOTION_ID': first,
                    'YOUR_SECOND_MOTION_ID': second}
    for event in timeline['body_motions']:
        event['motion'] = replacements[event['motion']]
    return timeline


def write_new(path: Path, timeline: dict) -> None:
    path = path.expanduser().absolute()
    if path.suffix.lower() != '.json':
        raise ValueError('--output must end in .json')
    for part in (path, *path.parents):
        if os.path.lexists(part) and (part.is_symlink() or
                getattr(part.lstat(), 'st_file_attributes', 0) & 0x400):
            raise ValueError('Output path must not contain links')
    if path.exists():
        raise ValueError('Output already exists; back it up or choose another path. Nothing overwritten.')
    payload = json.dumps(timeline, ensure_ascii=False, indent=2, allow_nan=False) + '\n'
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('x', encoding='utf-8', newline='\n') as handle:
        handle.write(payload)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--template', choices=TEMPLATES, required=True)
    parser.add_argument('--data', help='dataset directory; otherwise use studio local settings')
    parser.add_argument('--motion', required=True, help='exact name of first role=motion bundle')
    parser.add_argument('--next-motion', help='exact name of second role=motion bundle')
    parser.add_argument('--output', type=Path, required=True, help='new JSON file; no overwrite option')
    parser.add_argument('--dry-run', action='store_true', help='print JSON without creating files')
    args = parser.parse_args(argv)
    try:
        config = studio.settings(args)
        report, manifest = studio.inspect_dataset(config['dataRoot'])
        if report['errors']:
            raise ValueError('; '.join(report['errors']))
        for warning in report['warnings']:
            print(f'WARNING: {warning}', file=sys.stderr)
        timeline = instantiate(args.template, args.motion, args.next_motion, manifest)
        if args.dry_run:
            print(json.dumps(timeline, indent=2, ensure_ascii=False))
        else:
            write_new(args.output, timeline)
            print(f'Created {args.output}. No assets copied and no Player started.')
            print('The Player reads only <dataRoot>/story-timeline.json; see examples/README.md.')
        return 0
    except (OSError, ValueError) as error:
        print(f'ERROR: {error}', file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
