#!/usr/bin/env bash
# Run inside the checkout being measured. Requires Git, Python 3 and .NET.
# Output must be new/empty and outside the checkout or in an ignored directory.
set -euo pipefail
exec python3 - "$@" <<'PY'
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
from datetime import datetime, timezone

parser = argparse.ArgumentParser(description='Capture source ownership and the legacy runtime baseline without editing source.')
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
root = Path(subprocess.check_output(['git', 'rev-parse', '--show-toplevel'], text=True).strip()).resolve()
out = args.output.resolve()
try:
    relative = out.relative_to(root)
except ValueError:
    relative = None
if relative is not None:
    if not relative.parts or relative.parts[0] == '.git':
        parser.error('output cannot be the checkout root or its Git metadata')
    ignored = subprocess.run(['git', '-C', str(root), 'check-ignore', '-q', str(out / 'capture-probe')]).returncode == 0
    if not ignored:
        parser.error('output inside the checkout must be Git-ignored; use a directory outside the checkout')
if out.exists() and (not out.is_dir() or any(out.iterdir())):
    parser.error('output must be a new or empty directory; existing evidence is never overwritten')
out.mkdir(parents=True, exist_ok=True)

def git(*arguments):
    return subprocess.check_output(['git', '-C', str(root), *arguments])

def manifest():
    tracked = set(git('ls-files', '--cached', '-z').split(b'\0')) - {b''}
    untracked = set(git('ls-files', '--others', '--exclude-standard', '-z').split(b'\0')) - {b''}
    files = []
    for raw in sorted(tracked | untracked):
        path = os.fsdecode(raw)
        source = root / path
        if source.is_symlink():
            kind, data = 'symlink', os.fsencode(os.readlink(source))
        elif source.is_file():
            kind, data = 'file', source.read_bytes()
        elif not source.exists():
            kind, data = 'missing', None
        else:
            kind, data = 'directory', None
        files.append({'path': path, 'ownership': 'tracked' if raw in tracked else 'untracked',
                      'kind': kind, 'sha256': hashlib.sha256(data).hexdigest() if data is not None else None})
    return {'root': str(root), 'files': files}

def write_json(name, value):
    (out / name).write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')

before = manifest()
write_json('source-manifest.json', before)
status_before = git('status', '--porcelain=v1', '-z')
(out / 'git-status.z').write_bytes(status_before)
(out / 'git-status.txt').write_bytes(git('status', '--short'))
write_json('metadata.json', {'captured_at_utc': datetime.now(timezone.utc).isoformat(),
                            'head': git('rev-parse', 'HEAD').decode().strip(),
                            'platform': platform.platform(), 'machine': platform.machine()})
env = dict(os.environ, DOTNET_ROLL_FORWARD='LatestPatch', DOTNET_ROLL_FORWARD_TO_PRERELEASE='0',
           MeridianIncludeNet11Samples='false')

def run(command, log):
    with (out / log).open('wb') as stream:
        try:
            return subprocess.run(command, cwd=root, env=env, stdout=stream, stderr=subprocess.STDOUT).returncode
        except OSError as error:
            stream.write((str(error) + '\n').encode())
            return 127

metadata_codes = {}
for option, log in [('--info', 'dotnet-info.log'), ('--list-sdks', 'dotnet-sdks.log'), ('--list-runtimes', 'dotnet-runtimes.log')]:
    metadata_codes[option] = run(['dotnet', option], log)
runtimes = (out / 'dotnet-runtimes.log').read_text(errors='replace')
commands = []
command = ['dotnet', 'build', 'Meridian.sln', '-c', 'Release']
code = run(command, 'build-release.log')
commands.append({'command': command, 'log': 'build-release.log', 'exit_code': code,
                 'verification': 'passed' if code == 0 else 'failed'})
for framework in ['net8.0', 'net9.0', 'net10.0']:
    major = framework[3:].split('.')[0]
    installed = any(line.startswith('Microsoft.NETCore.App ' + major + '.') for line in runtimes.splitlines())
    for suite in ['Mapping', 'Mediator']:
        command = ['dotnet', 'test', f'tests/Meridian.{suite}.Tests/Meridian.{suite}.Tests.csproj',
                   '-c', 'Release', '-f', framework]
        log = f'test-{suite.lower()}-{framework}.log'
        code = run(command, log)
        verification = ('passed' if code == 0 else 'failed') if installed else 'unverified'
        commands.append({'command': command, 'framework': framework, 'runtime_installed': installed,
                         'log': log, 'exit_code': code, 'verification': verification})
after = manifest()
write_json('source-manifest-after.json', after)
status_after = git('status', '--porcelain=v1', '-z')
(out / 'git-status-after.z').write_bytes(status_after)
source_unchanged = before == after and status_before == status_after
summary = {'source_unchanged': source_unchanged, 'metadata_exit_codes': metadata_codes,
           'runtime_roll_forward': 'LatestPatch (same runtime family only)', 'commands': commands}
write_json('summary.json', summary)
print(f'Evidence: {out}')
for result in commands:
    print(f"{result['verification']}: {' '.join(result['command'])} (exit {result['exit_code']})")
if not source_unchanged:
    print('Source hashes or Git status changed during capture; inspect before/after evidence.', file=sys.stderr)
sys.exit(0 if source_unchanged and all(c == 0 for c in metadata_codes.values()) and
         all(c['verification'] == 'passed' for c in commands) else 1)
PY
