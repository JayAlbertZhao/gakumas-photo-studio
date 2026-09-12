#!/usr/bin/env python3
"""Fail-closed source allowlist, redacted audit, reproducible source archive."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
_baseline_spec = importlib.util.spec_from_file_location('release_baseline', Path(__file__).with_name('verify_baseline.py'))
_baseline_module = importlib.util.module_from_spec(_baseline_spec)
_baseline_spec.loader.exec_module(_baseline_module)
ALLOWED_SUFFIXES = {'.cs', '.shader', '.compute', '.cginc', '.hlsl', '.asmdef', '.meta', '.json', '.md', '.txt',
                    '.py', '.yml', '.asset', '.unity'}
ALLOWED_DOTFILES = {'.gitignore', '.gitattributes', '.editorconfig', '.githooks/pre-commit'}
PRIVATE_PARTS = {'private-reference', 'localassets', 'privateresources', 'research',
                 'library', 'temp', 'obj', 'logs', 'output', 'build', 'builds',
                 'usersettings', 'dist', '.git', '__pycache__'}
SOURCE_PROJECT_ASSETS = {
    'unity/Assets/Resources/ResearchOriginalShaderPipeline.asset',
    'unity/Assets/UniversalRenderPipelineGlobalSettings.asset',
    'unity/Assets/Scenes/PhotoMode.unity',
}
SOURCE_FOLDERS = {
    'unity/Assets/ActorAnimationStub', 'unity/Assets/CampusCommonStub',
    'unity/Assets/Resources', 'unity/Assets/Scenes', 'unity/Assets/Scripts',
    'unity/Assets/VLStub',
}
PATTERNS = {
    'windows_absolute_path': re.compile(r'\b[A-Za-z]:[\\/]'),
    'home_path': re.compile(r'/(?:home|Users)/[A-Za-z0-9_.-]+/'),
    'credential_assignment': re.compile(
        r'''(?i)(?:["']?(?:pf_access_token|open_id|viewer_id|api_key|password|secret|decrypt_key)["']?)\s*[:=]\s*["'][^"'\r\n]{4,}["']'''),
    'service_token': re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9]{25,}|sk-[A-Za-z0-9_-]{24,})'),
    'private_key': re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
    'binary_literal': re.compile(r'(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{300,}={0,2}'),
}


def validate_name(name: str) -> None:
    path = PurePosixPath(name)
    if (not name or '\\' in name or ':' in name or path.is_absolute()
            or '..' in path.parts or path.as_posix() != name):
        raise ValueError('Unsafe relative name')
    lower = name.lower()
    if (any(part.lower() in PRIVATE_PARTS for part in path.parts) and
            name != 'LocalAssets/README.md') or '.local.' in lower:
        raise ValueError('Private path')
    if path.suffix.lower() not in ALLOWED_SUFFIXES and name not in ALLOWED_DOTFILES:
        raise ValueError('Non-source extension')
    if (path.suffix.lower() == '.asset' and not name.startswith('unity/ProjectSettings/')
            and name not in SOURCE_PROJECT_ASSETS):
        raise ValueError('Serialized assets outside reviewed ProjectSettings')
    if path.suffix.lower() == '.unity' and name not in SOURCE_PROJECT_ASSETS:
        raise ValueError('Unreviewed scene')
    if path.suffix.lower() == '.meta':
        underlying = PurePosixPath(name[:-5]).suffix.lower()
        if (underlying not in {'.cs', '.shader', '.compute', '.cginc', '.hlsl', '.asmdef'} and
                name[:-5] not in SOURCE_PROJECT_ASSETS | SOURCE_FOLDERS):
            raise ValueError('Only source metadata may be published')


def safe_path(root: Path, name: str) -> Path:
    validate_name(name)
    candidate = root.joinpath(*PurePosixPath(name).parts)
    current = root
    for part in PurePosixPath(name).parts:
        current = current / part
        if current.is_symlink() or (current.exists() and
                getattr(current.lstat(), 'st_file_attributes', 0) & 0x400):
            raise ValueError('Reparse point/symlink is not publishable')
    if not candidate.resolve().is_relative_to(root.resolve()):
        raise ValueError('Path escapes release root')
    return candidate


def render_gitignore(names: list[str]) -> str:
    """Ignore everything by default; permit only reviewed files and their parents."""
    directories = set()
    for name in names:
        validate_name(name)
        if any(character in name for character in '*?[]'):
            raise ValueError('Gitignore wildcard in source path')
        for parent in PurePosixPath(name).parents:
            if str(parent) != '.':
                directories.add(parent.as_posix())
    lines = ['# Generated from public-files.json by tools/release.py sync-ignore.',
             '# Unknown files (including private inputs and builds) are ignored by default.', '*']
    lines.extend('!/' + directory + '/' for directory in
                 sorted(directories, key=lambda value: (value.count('/'), value)))
    lines.extend('!/' + name for name in sorted(names))
    return '\n'.join(lines) + '\n'


def audit_content(name: str, data: bytes) -> list[dict]:
    findings = []
    if len(data) > 1024 * 1024 or b'\x00' in data:
        return [{'file': name, 'rule': 'binary_or_oversized'}]
    try:
        text = data.decode('utf-8-sig')
    except UnicodeDecodeError:
        return [{'file': name, 'rule': 'not_utf8'}]
    for rule, pattern in PATTERNS.items():
        for match in pattern.finditer(text):
            # Never echo the matched credential or personal path.
            findings.append({'file': name, 'line': text.count('\n', 0, match.start()) + 1,
                             'rule': rule})
    return findings


def collect(root: Path, tracked: bool = False) -> tuple[dict, dict[str, bytes]]:
    policy = json.loads((root / 'public-files.json').read_text(encoding='utf-8-sig'))
    names = policy['files']
    if names != sorted(set(names)):
        raise ValueError('Allowlist must be sorted and unique')
    findings, payloads = [], {}
    baseline_path = root / 'config/source-baseline.json'
    baseline = json.loads(baseline_path.read_text(encoding='utf-8')) if baseline_path.exists() else {}
    frozen = baseline.get('files', {})
    revision_path = root / 'config/runtime-revisions.json'
    revisions = json.loads(revision_path.read_text(encoding='utf-8')) if revision_path.exists() else None
    reviewed, revision_findings = _baseline_module.resolve_expected(baseline, revisions)
    findings.extend(revision_findings)
    historical_names = {destination: original for original, destination in
                        (revisions or {}).get('relocations', {}).items()}

    def inspect(name: str, data: bytes) -> list[dict]:
        issues = audit_content(name, data)
        actual = hashlib.sha256(data.replace(b'\r\n', b'\n')).hexdigest()
        # Two archived defaults are non-personal project paths, not secrets.
        # Exempt only the path rule AND only an exactly frozen source file.
        historical_name = historical_names.get(name, name)
        if historical_name in baseline.get('nonpersonal_legacy_path_files', []) and actual == frozen.get(historical_name):
            issues = [issue for issue in issues if issue['rule'] != 'windows_absolute_path']
        if name in reviewed and actual != reviewed[name]:
            issues.append({'file': name, 'rule': 'baseline_mismatch'})
        return issues

    for name in reviewed:
        if name not in names:
            findings.append({'file': name, 'rule': 'baseline_not_allowlisted'})
    for name in names:
        try:
            path = safe_path(root, name)
            data = path.read_bytes()
            if (name.lower().endswith('.meta') and name[:-5] not in names
                    and name[:-5] not in SOURCE_FOLDERS):
                raise ValueError('Orphaned metadata')
            if name in SOURCE_PROJECT_ASSETS and name not in reviewed:
                raise ValueError('Authored scene/settings require a frozen content hash')
            if (baseline and name.startswith(('unity/', 'packages/com.digital-kotone.toolkit/')) and
                    PurePosixPath(name).suffix in {'.cs', '.shader', '.compute', '.cginc', '.hlsl', '.asmdef'} and name not in reviewed):
                raise ValueError('New Unity source requires an explicit baseline review')
            findings.extend(inspect(name, data))
            payloads[name] = data
        except (ValueError, OSError) as error:
            findings.append({'file': name, 'rule': type(error).__name__})
    if tracked:
        result = subprocess.run(['git', '-C', str(root), 'rev-parse', '--show-toplevel'],
                                capture_output=True, text=True, check=True)
        if Path(result.stdout.strip()).resolve() != root.resolve():
            raise ValueError('Use an independent Git repository, not the historical parent repository')
        result = subprocess.run(['git', '-C', str(root), 'ls-files', '--stage', '-z'],
                                capture_output=True, check=True)
        indexed = set()
        for record in result.stdout.decode().split('\x00'):
            if not record:
                continue
            metadata, name = record.split('\t', 1)
            indexed.add(name)
            mode, blob, stage = metadata.split()
            if name not in names:
                findings.append({'file': name, 'rule': 'tracked_not_allowlisted'})
                continue
            if mode not in {'100644', '100755'} or stage != '0':
                findings.append({'file': name, 'rule': 'unsafe_index_entry'})
                continue
            staged = subprocess.run(['git', '-C', str(root), 'cat-file', 'blob', blob],
                                    capture_output=True, check=True).stdout
            findings.extend(inspect(name, staged))
            # Git may normalize line endings. Any other staged/worktree mismatch
            # blocks release, including "stage a secret, then clean only the file".
            working = payloads.get(name, b'')
            if staged.replace(b'\r\n', b'\n') != working.replace(b'\r\n', b'\n'):
                findings.append({'file': name, 'rule': 'index_worktree_mismatch'})
        for name in sorted(set(names) - indexed):
            findings.append({'file': name, 'rule': 'allowlisted_not_tracked'})
    expected_ignore = render_gitignore(names).encode()
    if '.gitignore' in payloads and payloads['.gitignore'].replace(b'\r\n', b'\n') != expected_ignore:
        findings.append({'file': '.gitignore', 'rule': 'stale_default_deny_ignore'})
    return ({'schema': 'photo-studio.source-audit.v1', 'accepted': not findings,
             'files': len(payloads), 'bytes': sum(map(len, payloads.values())),
             'reviewed_legacy_path_files': [name for name in payloads
                 if historical_names.get(name, name) in baseline.get('nonpersonal_legacy_path_files', [])
                 and hashlib.sha256(payloads[name].replace(b'\r\n', b'\n')).hexdigest() == frozen.get(historical_names.get(name, name))],
             'findings': findings}, payloads)


def pack(output: Path, payloads: dict[str, bytes]) -> None:
    manifest = {name: {'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
                for name, data in sorted(payloads.items())}
    with output.open('xb') as stream:
        with zipfile.ZipFile(stream, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
            entries = dict(payloads)
            entries['SOURCE-MANIFEST.json'] = (json.dumps(manifest, indent=2) + '\n').encode()
            for name, data in sorted(entries.items()):
                info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
                mode = 0o755 if name == '.githooks/pre-commit' else 0o644
                info.external_attr = (stat.S_IFREG | mode) << 16
                info.compress_type = zipfile.ZIP_DEFLATED
                archive.writestr(info, data)
    with zipfile.ZipFile(output) as archive:
        if archive.testzip() is not None or set(archive.namelist()) != set(manifest) | {'SOURCE-MANIFEST.json'}:
            raise ValueError('Archive verification failed')
        for name, metadata in manifest.items():
            if hashlib.sha256(archive.read(name)).hexdigest() != metadata['sha256']:
                raise ValueError('Archive content hash mismatch')


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['audit', 'pack', 'sync-ignore'])
    parser.add_argument('--tracked', action='store_true')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if args.command == 'sync-ignore':
        policy = json.loads((ROOT / 'public-files.json').read_text(encoding='utf-8-sig'))
        safe_path(ROOT, '.gitignore').write_text(render_gitignore(policy['files']), encoding='utf-8')
        print('Regenerated default-deny .gitignore from the explicit source allowlist.')
        return 0
    report, payloads = collect(ROOT, args.tracked)
    print(json.dumps(report, indent=2))
    if not report['accepted']:
        return 2
    if args.command == 'pack':
        if args.output is None:
            parser.error('pack requires --output')
        pack(args.output.resolve(), payloads)
        print(json.dumps({'archive_bytes': args.output.stat().st_size,
                          'sha256': hashlib.sha256(args.output.read_bytes()).hexdigest()}))
    return 0


if __name__ == '__main__':
    sys.exit(main())
