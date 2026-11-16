import hashlib
import json
import os
import pathlib
import subprocess
import tempfile
import unittest
SCRIPT = pathlib.Path(__file__).resolve().parents[1] / 'capture-baseline.sh'

class CaptureTests(unittest.TestCase):

    def capture(self, fail=False, missing=False):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        root = pathlib.Path(temp.name) / 'repo'
        root.mkdir()
        subprocess.run(['git', 'init', '-q', str(root)], check=True)
        (root / 'tracked.txt').write_bytes(b'known tracked bytes\n')
        subprocess.run(['git', '-C', str(root), 'add', 'tracked.txt'], check=True)
        subprocess.run(['git', '-C', str(root), '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '-qm', 'Fixture'], check=True)
        (root / 'untracked.txt').write_bytes(b'known untracked bytes\n')
        (root / '.gitignore').write_text('ignored/\n')
        (root / 'ignored').mkdir()
        (root / 'ignored/noise').write_text('not source')
        fake = pathlib.Path(temp.name) / 'bin'
        fake.mkdir()
        dotnet = fake / 'dotnet'
        dotnet.write_text("""#!/usr/bin/env python3
import os,sys
a=sys.argv[1:]
if a==['--list-runtimes']:
 print('Microsoft.NETCore.App 8.0.27 [/fixture]'); print('Microsoft.NETCore.App 10.0.8 [/fixture]')
 if not os.environ.get('MISSING'): print('Microsoft.NETCore.App 9.0.14 [/fixture]')
elif a==['--list-sdks']: print('10.0.300 [/fixture]')
elif a==['--info']: print('Fixture SDK metadata')
elif 'test' in a:
 if os.environ.get('FAIL') or (os.environ.get('MISSING') and 'net9.0' in a):
  print('Intentional failing test' if os.environ.get('FAIL') else 'Required runtime absent'); sys.exit(1)
 print('Passed: 1')
else: print('Build succeeded')
""")
        dotnet.chmod(0o755)
        output = pathlib.Path(temp.name) / 'capture'
        env = dict(os.environ, PATH=str(fake) + os.pathsep + os.environ['PATH'], FAIL='1' if fail else '', MISSING='1' if missing else '')
        result = subprocess.run(['bash', str(SCRIPT), '--output', str(output)], cwd=root, env=env, text=True, capture_output=True)
        self.assertTrue((output / 'summary.json').is_file(), result.stderr)
        return (root, output, result, json.loads((output / 'summary.json').read_text()))

    def test_preserves_source_and_captures_tracked_and_untracked_hashes(self):
        root, out, result, summary = self.capture()
        self.assertEqual(result.returncode, 0, result.stderr)
        files = {x['path']: x for x in json.loads((out / 'source-manifest.json').read_text())['files']}
        self.assertEqual(files['tracked.txt']['sha256'], hashlib.sha256(b'known tracked bytes\n').hexdigest())
        self.assertEqual(files['untracked.txt']['sha256'], hashlib.sha256(b'known untracked bytes\n').hexdigest())
        self.assertNotIn('ignored/noise', files)
        self.assertTrue(summary['source_unchanged'])
        self.assertEqual((root / 'tracked.txt').read_bytes(), b'known tracked bytes\n')
        self.assertEqual((root / 'untracked.txt').read_bytes(), b'known untracked bytes\n')
        self.assertEqual(len(summary['commands']), 7)
        self.assertTrue((out / 'git-status.txt').is_file())
        self.assertTrue((out / 'dotnet-info.log').is_file())

    def test_test_failure_remains_failure_and_has_a_log(self):
        root, out, result, summary = self.capture(fail=True)
        self.assertEqual(result.returncode, 1)
        failures = [x for x in summary['commands'] if x['exit_code'] != 0]
        self.assertEqual(len(failures), 6)
        self.assertIn('Intentional failing test', (out / failures[0]['log']).read_text())

    def test_missing_runtime_is_unverified(self):
        root, out, result, summary = self.capture(missing=True)
        self.assertEqual(result.returncode, 1)
        rows = [x for x in summary['commands'] if x.get('framework') == 'net9.0']
        self.assertEqual(len(rows), 2)
        self.assertTrue(all((x['verification'] == 'unverified' for x in rows)))
    def test_ignored_untracked_output_preserves_source(self):
        root, previous_output, _, _ = self.capture()
        output = root / 'ignored/capture'
        status_before = subprocess.check_output(['git', '-C', str(root), 'status', '--porcelain=v1', '-z'])
        env = dict(os.environ, PATH=str(previous_output.parent / 'bin') + os.pathsep + os.environ['PATH'])
        result = subprocess.run(['bash', str(SCRIPT), '--output', str(output)],
                                cwd=root, env=env, text=True, capture_output=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(json.loads((output / 'summary.json').read_text())['source_unchanged'])
        self.assertEqual(subprocess.check_output(['git', '-C', str(root), 'status', '--porcelain=v1', '-z']), status_before)

    def test_rejects_deleted_tracked_evidence_under_ignored_output(self):
        root, previous_output, _, _ = self.capture()
        output = root / 'ignored/capture'
        output.mkdir()
        summary = output / 'summary.json'
        summary.write_text('preserve inherited tracked evidence\n')
        subprocess.run(['git', '-C', str(root), 'add', '.gitignore'], check=True)
        subprocess.run(['git', '-C', str(root), 'add', '-f', str(summary)], check=True)
        subprocess.run(['git', '-C', str(root), '-c', 'user.name=Fixture',
                        '-c', 'user.email=fixture@example.invalid', '-c',
                        'commit.gpgsign=false', 'commit', '-qm', 'Tracked evidence'], check=True)
        summary.unlink()
        output.rmdir()
        status_before = subprocess.check_output(['git', '-C', str(root), 'status', '--porcelain=v1', '-z'])
        env = dict(os.environ, PATH=str(previous_output.parent / 'bin') + os.pathsep + os.environ['PATH'])
        result = subprocess.run(['bash', str(SCRIPT), '--output', str(output)],
                                cwd=root, env=env, text=True, capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(output.exists(), 'rejected capture recreated inherited deleted output')
        self.assertEqual(subprocess.check_output(['git', '-C', str(root), 'status', '--porcelain=v1', '-z']), status_before)

    def test_rejects_staged_deleted_evidence_under_ignored_output(self):
        root, previous_output, _, _ = self.capture()
        output = root / 'ignored/capture'
        output.mkdir()
        summary = output / 'summary.json'
        summary.write_text('preserve inherited tracked evidence\n')
        subprocess.run(['git', '-C', str(root), 'add', '.gitignore'], check=True)
        subprocess.run(['git', '-C', str(root), 'add', '-f', str(summary)], check=True)
        subprocess.run(['git', '-C', str(root), '-c', 'user.name=Fixture',
                        '-c', 'user.email=fixture@example.invalid', '-c',
                        'commit.gpgsign=false', 'commit', '-qm', 'Tracked evidence'], check=True)
        subprocess.run(['git', '-C', str(root), 'rm', str(summary)], check=True, capture_output=True)
        if output.exists():
            output.rmdir()
        status_before = subprocess.check_output(['git', '-C', str(root), 'status', '--porcelain=v1', '-z'])
        env = dict(os.environ, PATH=str(previous_output.parent / 'bin') + os.pathsep + os.environ['PATH'])
        result = subprocess.run(['bash', str(SCRIPT), '--output', str(output)],
                                cwd=root, env=env, text=True, capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(output.exists(), 'rejected capture recreated inherited deleted output')
        self.assertEqual(subprocess.check_output(['git', '-C', str(root), 'status', '--porcelain=v1', '-z']), status_before)

    def test_rejects_individually_unignored_evidence_destination(self):
        root, previous_output, _, _ = self.capture()
        (root / '.gitignore').write_text(
            'ignored/*\n!ignored/capture/\nignored/capture/*\n!ignored/capture/summary.json\n')
        output = root / 'ignored/capture'
        status_before = subprocess.check_output(['git', '-C', str(root), 'status', '--porcelain=v1', '-z'])
        env = dict(os.environ, PATH=str(previous_output.parent / 'bin') + os.pathsep + os.environ['PATH'])
        result = subprocess.run(['bash', str(SCRIPT), '--output', str(output)],
                                cwd=root, env=env, text=True, capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(output.exists(), 'rejected capture wrote unignored evidence')
        self.assertEqual(subprocess.check_output(['git', '-C', str(root), 'status', '--porcelain=v1', '-z']), status_before)

if __name__ == '__main__':
    unittest.main()
