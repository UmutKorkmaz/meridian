"""Actual owned OS-tree regressions for the HTTP smoke runner (POSIX fixture)."""
from pathlib import Path
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
PWSH = os.environ.get('MERIDIAN_TEST_PWSH') or shutil.which('pwsh') or '/opt/homebrew/bin/pwsh'


def status(pid):
    return subprocess.run(['/bin/ps', '-p', str(pid), '-o', 'stat='],
                          capture_output=True, text=True, check=False).stdout.strip()


def kill_group(group):
    try:
        os.killpg(group, signal.SIGKILL)
    except ProcessLookupError:
        pass


def owned_groups(work, runner_group):
    groups = {runner_group}
    pids = work / 'pids.json'
    if pids.exists():
        ids = json.loads(pids.read_text())
        for name in ['parent', 'child']:
            try:
                if os.getpgid(ids[name]) == ids['group']:
                    groups.add(ids['group'])
            except ProcessLookupError:
                pass
    # Only private fixture receipts, never a machine-wide PID/name inventory.
    for path in (work / 'logs').glob('.http-owned-*/ownership.json'):
        receipt = json.loads(path.read_text())
        if path.parent.name != '.http-owned-' + receipt['token'] or receipt['kind'] != 'unix-session':
            raise RuntimeError('Fixture ownership receipt does not match its private directory.')
        if receipt['pid'] <= 1:
            raise RuntimeError('Invalid fixture supervisor PID.')
        groups.add(receipt['pid'])
    return groups


def cleanup_session(process, work):
    failures = []
    groups = {process.pid}
    try:
        groups = owned_groups(work, process.pid)
    except Exception as error:
        failures.append(error)
    # Each group is signalled once; an exited leader never gates cleanup.
    for group in groups:
        try:
            kill_group(group)
        except Exception as error:
            failures.append(error)
    try:
        process.communicate(timeout=5)
    except Exception as error:
        failures.append(error)
    if failures:
        raise ExceptionGroup('Owned fixture cleanup failed.', failures)
    (work / 'cleanup-complete.json').write_text(json.dumps({'groups': sorted(groups)}))


def cleanup_outer(processes, work, primary):
    failures = []
    complete = False
    try:
        complete = (work / 'cleanup-complete.json').exists()
    except Exception as error:
        failures.append(error)
    for process in processes:
        if not complete:
            try:
                groups = owned_groups(work, process.pid)
            except Exception as error:
                failures.append(error)
                groups = {process.pid}
            for group in groups:
                try:
                    kill_group(group)
                except Exception as error:
                    failures.append(error)
        try:
            process.communicate(timeout=5)
        except Exception as error:
            failures.append(error)
    if failures:
        error = ExceptionGroup('Independent fixture cleanup failed.', failures)
        if primary is None:
            raise error
        primary.outer_cleanup_error = error
        primary.add_note(f'Independent fixture cleanup also failed: {error!r}')


def broken_launcher(work, detached=False):
    fake = work / 'broken-pwsh'
    fake.write_text('#!' + sys.executable + '\n' + f'''import os, subprocess, time
from pathlib import Path
with open(os.devnull, 'w') as output:
    subprocess.Popen(['dotnet', 'test'], stdout=output, stderr=output, start_new_session={detached!r})
receipt = Path(os.environ['MERIDIAN_WATCHDOG_PID_FILE'])
while not receipt.exists():
    time.sleep(.01)
raise SystemExit(124)
''')
    fake.chmod(0o755)
    return fake


class HttpRunnerTimeoutTests(unittest.TestCase):
    def test_live_launcher_timeout_terminates_owned_tree_and_observes_drains(self):
        with tempfile.TemporaryDirectory(prefix='meridian-http-live-') as directory:
            self.run_watchdog(Path(directory), False)

    def test_exited_launcher_with_inherited_pipes_is_bounded_and_joined(self):
        with tempfile.TemporaryDirectory(prefix='meridian-http-exited-') as directory:
            self.run_watchdog(Path(directory), True)

    def run_watchdog(self, work, exited_launcher, executable=PWSH, owned_processes=None):
        shim = work / 'dotnet'
        shim.write_text('#!' + sys.executable + '\n' + '''import json, os, subprocess, sys
from pathlib import Path
if len(sys.argv) > 1 and sys.argv[1] == 'test':
    child = subprocess.Popen([sys.executable, '-c', 'import time;time.sleep(60)'])
    receipt = Path(os.environ['MERIDIAN_WATCHDOG_PID_FILE'])
    receipt.with_suffix('.tmp').write_text(json.dumps({'parent': os.getpid(), 'child': child.pid, 'group': os.getpgrp(), 'session': os.getsid(0)}))
    receipt.with_suffix('.tmp').replace(receipt)
    print('http-owned-stdout', flush=True)
    print('http-owned-stderr', file=sys.stderr, flush=True)
    if os.environ.get('MERIDIAN_WATCHDOG_EARLY_EXIT') != '1':
        child.wait()
else:
    print('console-fixture-passed', flush=True)
''')
        shim.chmod(0o755)
        env = os.environ.copy()
        env.update(PATH=str(work) + os.pathsep + env.get('PATH', ''),
                   MERIDIAN_TEST_REDIS='owned-fixture-not-contacted',
                   MERIDIAN_WATCHDOG_PID_FILE=str(work / 'pids.json'),
                   MERIDIAN_WATCHDOG_EARLY_EXIT='1' if exited_launcher else '0')
        started = time.monotonic()
        diagnosis = None
        primary = None
        process = subprocess.Popen(
            [executable, '-NoProfile', '-File', str(ROOT / 'eng/run-samples.ps1'),
             '-Framework', 'net10.0', '-NoBuild', '-IncludeHttp', '-TimeoutSeconds', '1',
             '-LogDirectory', str(work / 'logs')], env=env, stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT, text=True, start_new_session=True)
        try:
            if owned_processes is not None:
                owned_processes.append(process)
            (work / 'session.json').write_text(json.dumps({'group': process.pid}))
            try:
                output, _ = process.communicate(timeout=8)
            except subprocess.TimeoutExpired:
                diagnosis = 'outer safety watchdog: HTTP runner did not return'
                kill_group(process.pid)
                output, _ = process.communicate(timeout=5)
            elapsed = time.monotonic() - started
            self.assertEqual(124, process.returncode, output)
            self.assertLess(elapsed, 7, output)
            ids = json.loads((work / 'pids.json').read_text())
            statuses = {str(ids[name]): status(ids[name]) for name in ['parent', 'child']}
            print(json.dumps({'shape': 'exited' if exited_launcher else 'live',
                              'elapsedSeconds': elapsed, 'exit': process.returncode,
                              'ownedPids': ids, 'statuses': statuses}), flush=True)
            for pid, state in statuses.items():
                self.assertTrue(not state or state.startswith('Z'), f'owned process still live: {pid} {state}')
            log = (work / 'logs/http.log').read_text()
            print(json.dumps({'httpLog': log}), flush=True)
            ownership = json.loads((work / 'logs/http-ownership.json').read_text())
            self.assertEqual(ownership['pid'], ids['group'])
            self.assertEqual(ownership['pid'], ids['session'])
            self.assertNotEqual(process.pid, ownership['pid'])
            self.assertEqual('unix-session', ownership['kind'])
            self.assertEqual(32, len(ownership['token']))
            self.assertIsNone(diagnosis, diagnosis)
            for marker in ['timeout=True', 'exit=124', 'http-owned-stdout', 'http-owned-stderr',
                           'exitObserved=True', 'drainsObserved=True', 'ownershipValidated=True']:
                self.assertIn(marker, log)
        except BaseException as error:
            primary = error
            raise
        finally:
            try:
                cleanup_session(process, work)
            except Exception as cleanup:
                if primary is None:
                    raise
                primary.cleanup_error = cleanup
                primary.add_note(f'Owned fixture cleanup also failed: {cleanup!r}')

    def test_failed_pid_assertion_cleans_fixture_after_runner_already_exited(self):
        with tempfile.TemporaryDirectory(prefix='meridian-http-broken-runner-') as directory:
            work = Path(directory)
            fake = broken_launcher(work, detached=True)
            processes = []
            primary = None
            try:
                with self.assertRaisesRegex(AssertionError, 'owned process still live'):
                    self.run_watchdog(work, False, str(fake), processes)
                ids = json.loads((work / 'pids.json').read_text())
                statuses = {str(ids[name]): status(ids[name]) for name in ['parent', 'child']}
                print(json.dumps({'failureCleanupStatuses': statuses}), flush=True)
                self.assertTrue(all(not state or state.startswith('Z') for state in statuses.values()), statuses)
            except BaseException as error:
                primary = error
                raise
            finally:
                # Independent outer owner keeps this regression's expected RED from stranding anything.
                cleanup_outer(processes, work, primary)


    def test_cleanup_failure_retains_actual_primary_assertion_and_cleanup_object(self):
        with tempfile.TemporaryDirectory(prefix='meridian-http-cleanup-error-') as directory:
            work = Path(directory)
            fake = broken_launcher(work)
            cleanup_error = OSError('selected cleanup error')
            actual_cleanup = cleanup_session
            processes = []
            primary = None

            def cleanup_then_fail(process, fixture):
                actual_cleanup(process, fixture)
                raise cleanup_error

            try:
                with mock.patch(__name__ + '.cleanup_session', side_effect=cleanup_then_fail):
                    with self.assertRaisesRegex(AssertionError, 'owned process still live') as caught:
                        self.run_watchdog(work, False, str(fake), processes)
                self.assertIs(cleanup_error, caught.exception.cleanup_error)
                ids = json.loads((work / 'pids.json').read_text())
                self.assertTrue(all(not status(ids[name]) or status(ids[name]).startswith('Z') for name in ['parent', 'child']))
            except BaseException as error:
                primary = error
                raise
            finally:
                cleanup_outer(processes, work, primary)

    def test_session_receipt_write_failure_joins_actual_fixture(self):
        self.check_session_write_failure(False)

    def test_session_receipt_write_failure_retains_primary_and_cleanup_error(self):
        self.check_session_write_failure(True)

    def check_session_write_failure(self, secondary_failure):
        with tempfile.TemporaryDirectory(prefix='meridian-http-session-write-') as directory:
            work = Path(directory)
            launcher = work / 'private-launcher'
            launcher.write_text('#!' + sys.executable + '\nimport subprocess\n'
                                "subprocess.Popen(['dotnet', 'test']).wait()\n")
            launcher.chmod(0o755)
            processes = []
            selected = OSError('selected session receipt write failure')
            cleanup_error = OSError('selected secondary cleanup failure')
            real_popen = subprocess.Popen
            real_write = Path.write_text
            actual_cleanup = cleanup_session
            ids = None
            before = None
            primary = None

            def observe_popen(*args, **kwargs):
                process = real_popen(*args, **kwargs)
                if kwargs.get('start_new_session') and args[0][0] == str(launcher):
                    processes.append(process)
                return process

            def fail_session_write(path, *args, **kwargs):
                nonlocal ids, before
                if path == work / 'session.json':
                    deadline = time.monotonic() + 3
                    while not (work / 'pids.json').exists():
                        if time.monotonic() >= deadline:
                            raise TimeoutError('Independent fixture readiness watchdog expired.')
                        time.sleep(.01)
                    ids = json.loads((work / 'pids.json').read_text())
                    before = {str(pid): status(pid) for pid in
                              [processes[0].pid, ids['parent'], ids['child']]}
                    raise selected
                return real_write(path, *args, **kwargs)

            def cleanup_then_maybe_fail(process, fixture):
                actual_cleanup(process, fixture)
                if secondary_failure:
                    raise cleanup_error

            try:
                with mock.patch.object(subprocess, 'Popen', side_effect=observe_popen), \
                        mock.patch.object(Path, 'write_text', new=fail_session_write), \
                        mock.patch(__name__ + '.cleanup_session', side_effect=cleanup_then_maybe_fail) as cleanup:
                    with self.assertRaises(OSError) as caught:
                        self.run_watchdog(work, False, str(launcher))
                process = processes[0]
                after = {str(pid): status(pid) for pid in
                         [process.pid, ids['parent'], ids['child']]}
                print(json.dumps({'sessionWriteFailure': True, 'secondaryFailure': secondary_failure,
                                  'runnerPid': process.pid, 'ownedPids': ids, 'statusesBefore': before,
                                  'statusesAfterUnwind': after, 'directRunnerJoined': process.returncode is not None,
                                  'cleanupCalls': cleanup.call_count,
                                  'primaryIdentityPreserved': caught.exception is selected,
                                  'secondaryIdentityPreserved': (getattr(caught.exception, 'cleanup_error', None)
                                                                is cleanup_error) if secondary_failure else None,
                                  'receiptPersisted': (work / 'session.json').exists()}), flush=True)
                self.assertIs(selected, caught.exception)
                self.assertTrue(all(before.values()), before)
                self.assertEqual(process.pid, ids['group'])
                self.assertEqual(1, cleanup.call_count)
                self.assertIsNotNone(process.returncode)
                self.assertTrue(all(not state or state.startswith('Z') for state in after.values()), after)
                self.assertFalse((work / 'session.json').exists())
                if secondary_failure:
                    self.assertIs(cleanup_error, caught.exception.cleanup_error)
                else:
                    self.assertFalse(hasattr(caught.exception, 'cleanup_error'))
            except BaseException as error:
                primary = error
                raise
            finally:
                # Independent in-memory owner also works when session.json cannot be written.
                cleanup_outer(processes, work, primary)
                try:
                    if ids is not None:
                        after_outer = {str(pid): status(pid) for pid in
                                       [processes[0].pid, ids['parent'], ids['child']]}
                        print(json.dumps({'sessionWriteOuterJoined': all(process.returncode is not None
                                                                         for process in processes),
                                          'statusesAfterIndependentCleanup': after_outer}), flush=True)
                        self.assertTrue(all(not state or state.startswith('Z')
                                            for state in after_outer.values()), after_outer)
                except Exception as cleanup:
                    if primary is None:
                        raise
                    primary.add_note(f'Independent fixture cleanup also failed: {cleanup!r}')


if __name__ == '__main__':
    unittest.main()
