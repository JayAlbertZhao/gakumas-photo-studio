#!/usr/bin/env python3
"""Source-preview launcher. Configures and checks inputs; never edits Unity code."""
from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime
import importlib.util
import json
import math
import os
from pathlib import Path, PureWindowsPath
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parent
CONFIG_NAME = 'studio.local.json'


def read_object(path: Path) -> dict:
    value = json.loads(path.read_text(encoding='utf-8-sig'))
    if not isinstance(value, dict):
        raise ValueError(f'{path.name}: expected a JSON object')
    return value


def settings(args, root: Path = ROOT) -> dict:
    config = read_object(root / CONFIG_NAME) if (root / CONFIG_NAME).exists() else {}
    if set(config) - {'dataRoot', 'editor', 'riverbedRoot'}:
        raise ValueError('Unknown local config fields; use config/studio.example.json')
    resolved = {}
    for key, option, env, default in (
        ('dataRoot', 'data', 'GAKUMAS_PHOTO_STAGING', 'LocalAssets/runtime'),
        ('editor', 'editor', 'TUANJIE_EDITOR', None),
        ('riverbedRoot', 'riverbed', 'GAKUMAS_RIVERBED_STAGING', None),
    ):
        explicit = getattr(args, option, None)
        value = explicit or config.get(key) or os.environ.get(env) or default
        if value is not None and not isinstance(value, str):
            raise ValueError(f'{key}: expected a path string')
        if value:
            path = Path(value).expanduser()
            resolved[key] = path.resolve() if explicit or path.is_absolute() else (root / path).resolve()
        else:
            resolved[key] = None
    return resolved


def local_file(root: Path, relative: str) -> Path:
    if not isinstance(relative, str) or not relative or '\\' in relative or ':' in relative:
        raise ValueError('expected a nonempty relative path with forward slashes')
    path = Path(relative)
    if path.is_absolute() or PureWindowsPath(relative).is_absolute() or '..' in path.parts:
        raise ValueError('path must stay inside the dataset')
    result = (root / path).resolve()
    if not result.is_relative_to(root.resolve()):
        raise ValueError('path or link escapes the dataset')
    if not result.is_file():
        raise ValueError('file is missing')
    return result


def owner(name: str) -> str:
    match = re.match(r'^mdl_chr_([^-]+)-', name, re.IGNORECASE)
    return match.group(1).lower() if match else ''


def inspect_dataset(data: Path) -> tuple[dict, dict]:
    report = {'errors': [], 'warnings': [], 'roles': {}, 'characters': []}
    errors, warnings = report['errors'], report['warnings']
    path = data / 'staging-manifest.json'
    if not path.is_file():
        errors.append('staging-manifest.json is missing. Supply a prepared dataset; see docs/assets.md.')
        return report, {}
    manifest = read_object(path)
    records = manifest.get('bundles')
    if not isinstance(records, list) or not records:
        errors.append('bundles must be a nonempty list; the empty example is not a usable dataset.')
        return report, manifest
    names, roles, characters = set(), Counter(), set()
    for index, record in enumerate(records):
        if not isinstance(record, dict):
            errors.append(f'bundles[{index}] must be an object')
            continue
        name, role = record.get('name'), record.get('role')
        if not isinstance(name, str) or not name or name in names:
            errors.append(f'bundles[{index}]: name must be nonempty and unique')
            continue
        names.add(name)
        if not isinstance(role, str) or not role:
            errors.append(f'bundles[{index}]: role is missing')
        else:
            roles[role] += 1
            if role == 'face' and owner(name):
                characters.add(owner(name))
        try:
            local_file(data, record.get('output_relative_path'))
        except ValueError as error:
            errors.append(f'bundles[{index}].output_relative_path: {error}')
        dependencies = record.get('dependencies')
        if dependencies is not None and (not isinstance(dependencies, list) or
                any(not isinstance(item, str) for item in dependencies)):
            errors.append(f'bundles[{index}].dependencies must be a string list')
    for role in ('face', 'costume', 'hair', 'motion'):
        if not roles[role]:
            errors.append(f'missing required bundle role: {role}')
    if not characters:
        errors.append('No face ID matches the runtime naming convention mdl_chr_<character>-<variant>_face.')
    missing_dependencies = set()
    for record in records:
        if isinstance(record, dict) and isinstance(record.get('dependencies'), list):
            missing_dependencies.update(item for item in record['dependencies']
                                        if isinstance(item, str) and item not in names)
    if missing_dependencies:
        warnings.append(f'{len(missing_dependencies)} dependency IDs are not listed; affected bundles may not render correctly.')
    voices = manifest.get('voices')
    if voices is None:
        voices = []
    if not isinstance(voices, list):
        errors.append('voices must be a list')
    else:
        for index, voice in enumerate(voices):
            try:
                local_file(data, voice.get('output_relative_path') if isinstance(voice, dict) else None)
            except ValueError as error:
                errors.append(f'voices[{index}]: {error}')
    report['roles'] = dict(roles)
    report['characters'] = sorted(characters)
    warnings.append('File checks do not validate bundle contents, skeleton compatibility, or visual fidelity.')
    return report, manifest


def inspect_story(data: Path, manifest: dict) -> list[str]:
    path = data / 'story-timeline.json'
    if not path.is_file():
        return ['story-timeline.json is missing. Only converted timeline JSON is supported, not raw Lua.']
    timeline = read_object(path)
    duration = timeline.get('duration')
    if (not isinstance(duration, (int, float)) or isinstance(duration, bool) or
            not math.isfinite(duration) or duration <= 0):
        return ['timeline.duration must be a positive finite number']
    motions = timeline.get('body_motions')
    if not isinstance(motions, list) or not motions:
        return ['timeline.body_motions must be a nonempty list']
    names = {r.get('name') for r in manifest.get('bundles', []) if isinstance(r, dict)}
    errors = []
    for index, event in enumerate(motions):
        if not isinstance(event, dict):
            errors.append(f'body_motions[{index}] must be an object')
            continue
        motion = event.get('motion')
        if not isinstance(motion, str) or motion not in names:
            errors.append(f'body_motions[{index}].motion does not identify a listed bundle')
        for field in ('time', 'duration'):
            value = event.get(field, 0)
            if (not isinstance(value, (int, float)) or isinstance(value, bool) or
                    not math.isfinite(value) or value < 0):
                errors.append(f'body_motions[{index}].{field} must be finite and nonnegative')
    return errors


def check_baseline(root: Path) -> None:
    spec = importlib.util.spec_from_file_location('source_baseline', root / 'tools/verify_baseline.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    report = module.verify(root)
    if not report['accepted']:
        raise ValueError('Unity source baseline differs. Run python -I tools/verify_baseline.py before proceeding.')


def launch_plan(mode: str, config: dict, character: str | None, root: Path = ROOT) -> tuple[list[str], dict]:
    player = root / 'unity/output/KotonePhotoStudio.exe'
    if not player.is_file():
        raise ValueError('Player is missing. Run: python -I studio.py build')
    argv = [str(player)]
    if mode == 'photo':
        argv.append('--photo-mode')
    if character:
        argv += ['--character-id', character]
    log = root / 'unity' / (mode + '-' + datetime.now().strftime('%Y%m%d-%H%M%S-%f') + '.local.log')
    argv += ['-logFile', str(log)]
    environment = os.environ.copy()
    environment['GAKUMAS_PHOTO_STAGING'] = str(config['dataRoot'])
    if config['riverbedRoot']:
        environment['GAKUMAS_RIVERBED_STAGING'] = str(config['riverbedRoot'])
    return argv, environment


def main(argv=None, root: Path = ROOT) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    for name in ('configure', 'doctor', 'catalog', 'build', 'photo', 'story'):
        command = commands.add_parser(name)
        command.add_argument('--data', help='prepared dataset directory; does not copy or download assets')
        command.add_argument('--editor', help='Tuanjie editor executable (required for build)')
        command.add_argument('--riverbed', help='optional existing riverbed dataset directory')
        if name in ('photo', 'story', 'doctor'):
            command.add_argument('--character', help='character ID already present in your dataset')
        if name in ('photo', 'story', 'build'):
            command.add_argument('--dry-run', action='store_true', help='validate and print command without launching')
        if name == 'build':
            command.add_argument('--timeout', type=int, default=1200, help='build timeout in seconds (default: 1200)')
    args = parser.parse_args(argv)
    try:
        config = settings(args, root)
        if args.command == 'configure':
            if not any((args.data, args.editor, args.riverbed)):
                raise ValueError('configure requires --data, --editor or --riverbed; see config/studio.example.json')
            if args.editor and not config['editor'].is_file():
                raise ValueError('Editor executable does not exist')
            if args.data and not config['dataRoot'].is_dir():
                raise ValueError('Dataset directory does not exist')
            if args.riverbed and not config['riverbedRoot'].is_dir():
                raise ValueError('Riverbed directory does not exist')
            path = root / CONFIG_NAME
            if os.path.lexists(path) and (path.is_symlink() or
                    getattr(path.lstat(), 'st_file_attributes', 0) & 0x400):
                raise ValueError('Local config must not be a link')
            path.write_text(json.dumps({k: str(v) if v else None for k, v in config.items()}, indent=2) + '\n', encoding='utf-8')
            print(f'Saved {CONFIG_NAME} (ignored by Git). Next: python -I studio.py doctor')
            return 0
        if args.command == 'build':
            check_baseline(root)
            editor = config['editor']
            if editor is None or not editor.is_file():
                raise ValueError('Set the editor first: python -I studio.py configure --editor <executable>')
            if args.timeout <= 0:
                raise ValueError('--timeout must be positive')
            log = root / 'unity' / ('build-' + datetime.now().strftime('%Y%m%d-%H%M%S-%f') + '.local.log')
            command = [str(editor), '-batchmode', '-quit', '-projectPath', str(root / 'unity'),
                       '-executeMethod', 'GakumasPhotoMode.Editor.PhotoStudioBuilder.BuildPlayerOnly', '-logFile', str(log)]
            if args.dry_run:
                print(json.dumps({'argv': command}, indent=2))
                return 0
            print(f'Building with existing Unity entrypoint. Progress log: {log}', flush=True)
            startup = None
            if os.name == 'nt':
                startup = subprocess.STARTUPINFO()
                startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
                startup.wShowWindow = 0
            result = subprocess.run(command, cwd=root, timeout=args.timeout, startupinfo=startup)
            if result.returncode != 0 or not log.is_file() or '[PhotoMode] Build succeeded:' not in log.read_text(encoding='utf-8-sig', errors='replace'):
                raise ValueError(f'Editor did not report a successful build. Inspect {log}')
            check_baseline(root)
            print('Built unity/output/KotonePhotoStudio.exe. Local builds may embed private Resources; do not upload them.')
            return 0
        report, manifest = inspect_dataset(config['dataRoot'])
        character = getattr(args, 'character', None)
        if character and character.lower() not in report['characters']:
            report['errors'].append('Requested character is not listed; use studio.py catalog.')
        if args.command in ('photo', 'story') and not character and 'fktn' not in report['characters']:
            report['errors'].append('The unchanged runtime defaults to fktn. Specify --character for another dataset.')
        if args.command == 'story' and not report['errors']:
            report['errors'].extend(inspect_story(config['dataRoot'], manifest))
            report['warnings'].append('The unchanged story entrypoint starts at 8.45 seconds. Use RESTART in the UI to start at zero.')
        if args.command == 'doctor':
            report['editor_configured'] = bool(config['editor'] and config['editor'].is_file())
            report['player_built'] = (root / 'unity/output/KotonePhotoStudio.exe').is_file()
            report['required_editor'] = 'Tuanjie 2022.3.62t12 / URP 14.2.0-t1'
            if not report['editor_configured']:
                report['warnings'].append('Editor not configured; configure --editor before building.')
            if not report['player_built']:
                report['warnings'].append('Player not built; run studio.py build before launching.')
        if args.command in ('photo', 'story', 'doctor') or report['errors']:
            print(json.dumps(report, indent=2, ensure_ascii=True))
        if report['errors']:
            return 2
        if args.command == 'catalog':
            print(json.dumps({'characters': report['characters'], 'bundles': [
                {k: record.get(k) for k in ('name', 'role', 'label')}
                for record in manifest['bundles'] if record.get('role') != 'dependency']}, indent=2))
            return 0
        if args.command == 'doctor':
            return 0
        check_baseline(root)
        command, environment = launch_plan(args.command, config, character, root)
        if args.dry_run:
            print(json.dumps({'argv': command, 'dataRoot': str(config['dataRoot'])}, indent=2))
            return 0
        # Interactive Player launch is explicitly requested by photo/story.
        process = subprocess.Popen(command, cwd=root, env=environment)
        print(f'Player started (PID {process.pid}). Log: {command[-1]}')
        print('No runtime defaults or source files were rewritten; startup is not visual acceptance.')
        return 0
    except (OSError, ValueError, subprocess.TimeoutExpired) as error:
        print(f'ERROR: {error}', file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
