"""Actual owned child-tree watchdog regression for run-samples' HTTP branch."""
from pathlib import Path
import json, os, shutil, signal, subprocess, sys, tempfile, time, unittest

ROOT = Path(__file__).resolve().parents[2]
PWSH = os.environ.get('MERIDIAN_TEST_PWSH') or shutil.which('pwsh') or '/opt/homebrew/bin/pwsh'

class HttpRunnerTimeoutTests(unittest.TestCase):
    def test_http_child_timeout_terminates_owned_tree_and_observes_drains(self):
        with tempfile.TemporaryDirectory(prefix='meridian-http-watchdog-') as directory:
            work = Path(directory); shim = work/'dotnet'
            shim.write_text('#!'+sys.executable+'\n'+'''import json, os, subprocess, sys, time
from pathlib import Path
p=Path(os.environ['MERIDIAN_WATCHDOG_PID_FILE'])
if len(sys.argv)>1 and sys.argv[1]=='test':
 child=subprocess.Popen([sys.executable,'-c','import time;time.sleep(60)'])
 p.write_text(json.dumps({'parent':os.getpid(),'child':child.pid}))
 print('http-owned-stdout',flush=True);print('http-owned-stderr',file=sys.stderr,flush=True)
 child.wait()
else: print('console-fixture-passed',flush=True)
''');shim.chmod(0o755)
            env=os.environ.copy();env.update(PATH=str(work)+os.pathsep+env.get('PATH',''),MERIDIAN_TEST_REDIS='owned-fixture-not-contacted',MERIDIAN_WATCHDOG_PID_FILE=str(work/'pids.json'))
            start=time.monotonic()
            process=subprocess.Popen([PWSH,'-NoProfile','-File',str(ROOT/'eng/run-samples.ps1'),'-Framework','net10.0','-NoBuild','-IncludeHttp','-TimeoutSeconds','1','-LogDirectory',str(work/'logs')],env=env,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,start_new_session=True)
            diagnosis=None
            try:
                try: output,_=process.communicate(timeout=8)
                except subprocess.TimeoutExpired:
                    diagnosis='outer safety watchdog: original HTTP runner did not return'
                    os.killpg(process.pid,signal.SIGKILL);output,_=process.communicate(timeout=5)
                elapsed=time.monotonic()-start
                self.assertEqual(124,process.returncode,output)
                self.assertLess(elapsed,7,output)
                ids=json.loads((work/'pids.json').read_text())
                statuses = {}
                for owned in ids.values():
                    status=subprocess.run(['/bin/ps','-p',str(owned),'-o','stat='],capture_output=True,text=True).stdout.strip()
                    statuses[str(owned)] = status
                    self.assertTrue(not status or status.startswith('Z'),f'owned process still live: {owned} {status}')
                log=(work/'logs/http.log').read_text()
                print(json.dumps({'elapsedSeconds': elapsed, 'exit': process.returncode, 'ownedPids': ids, 'joinedStatuses': statuses, 'httpLog': log}), flush=True)
                self.assertIsNone(diagnosis,diagnosis)
                self.assertIn('timeout=True',log);self.assertIn('exit=124',log)
                self.assertIn('http-owned-stdout',log);self.assertIn('http-owned-stderr',log)
                self.assertIn('exitObserved=True',log);self.assertIn('drainsObserved=True',log)
            finally:
                if process.poll() is None:os.killpg(process.pid,signal.SIGKILL);process.communicate(timeout=5)

if __name__=='__main__':unittest.main()
