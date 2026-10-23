"""Required workflows must retain immutable rerun evidence before updating current views."""
import unittest
import json
import os
import shutil
import subprocess
import tempfile
from unittest import mock
from pathlib import Path
import yaml
ROOT = Path(__file__).resolve().parents[2]

def overwrites(options):
    value = options.get("overwrite", False)
    if type(value) is bool:
        return value
    if isinstance(value, str) and value in ("true", "True", "TRUE", "false", "False", "FALSE"):
        return value.lower() == "true"
    raise AssertionError("Invalid upload-artifact overwrite input")


class WorkflowHistoryTests(unittest.TestCase):

    def test_quoted_true_cannot_bypass_missing_history(self):
        with tempfile.TemporaryDirectory(prefix="meridian-ci-oracle-") as temporary:
            root = Path(temporary)
            workflows = root / ".github/workflows"
            workflows.mkdir(parents=True)
            (workflows / "ci.yml").write_text(yaml.safe_dump({"jobs": {"example": {"steps": [{
                "uses": "actions/upload-artifact@v4",
                "with": {"name": "current", "path": "results", "overwrite": "true"}
            }]}}}))
            (workflows / "fuzz.yml").write_text("jobs: {}\n")
            with mock.patch(__name__ + ".ROOT", root):
                with self.assertRaises(AssertionError):
                    self.test_each_overwritten_ci_artifact_has_a_prior_immutable_attempt_snapshot()

    def test_required_workflows_have_no_duplicate_yaml_keys(self):

        class UniqueLoader(yaml.SafeLoader):
            pass

        def mapping(loader, node, deep=False):
            keys = [loader.construct_object(key, deep=deep) for key, _ in node.value]
            if len(keys) != len(set(keys)):
                raise ValueError('duplicate YAML keys')
            return yaml.SafeLoader.construct_mapping(loader, node, deep=deep)
        UniqueLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, mapping)
        for name in ('ci.yml', 'fuzz.yml'):
            yaml.load((ROOT / '.github/workflows' / name).read_text(), Loader=UniqueLoader)

    def test_each_overwritten_ci_artifact_has_a_prior_immutable_attempt_snapshot(self):
        for name in ('ci.yml', 'fuzz.yml'):
            workflow = yaml.safe_load((ROOT / '.github/workflows' / name).read_text())
            for job_name, job in workflow['jobs'].items():
                steps = job.get('steps', [])
                for index, step in enumerate(steps):
                    if 'upload-artifact' not in str(step.get('uses', '')):
                        continue
                    options = step['with']
                    if not overwrites(options):
                        continue
                    expected = 'history-' + options['name'] + '-attempt-${{ github.run_attempt }}'
                    prior = [s for s in steps[:index] if 'upload-artifact' in str(s.get('uses', '')) and s.get('with', {}).get('name') == expected]
                    self.assertEqual(len(prior), 1, f"{name}:{job_name}:{options['name']}")
                    self.assertEqual(prior[0]['with']['path'], options['path'])
                    self.assertFalse(overwrites(prior[0]['with']))

    def test_canonical_uploads_require_the_successful_snapshot(self):
        for name in ('ci.yml', 'fuzz.yml'):
            for job in yaml.safe_load((ROOT / '.github/workflows' / name).read_text())['jobs'].values():
                steps = job.get('steps', [])
                for index, step in enumerate(steps):
                    if 'upload-artifact' not in str(step.get('uses', '')) or not overwrites(step.get('with', {})):
                        continue
                    prior = steps[index - 1]
                    self.assertTrue(prior.get('id'))
                    self.assertEqual(prior.get('with', {}).get('name'), 'history-' + step['with']['name'] + '-attempt-${{ github.run_attempt }}')
                    self.assertEqual(step.get('if'), 'always() && steps.' + prior['id'] + ".outcome == 'success'")

    def test_aggregate_checks_exact_required_dependency_results_before_evaluation(self):
        workflow = yaml.safe_load((ROOT / '.github/workflows/ci.yml').read_text())
        job = workflow['jobs']['candidate-gate']
        policy = json.loads((ROOT / 'eng/required-checks.json').read_text())
        expected = {l['ci']['job'] for l in policy['lanes'] if l.get('ci') and l['ci']['workflow'] == 'ci.yml'}
        expected |= {r['job'] for r in policy['infrastructureChecks'] if r['workflow'] == 'ci.yml' and r['job'] != 'candidate-gate'}
        self.assertEqual(set(job['needs']), expected)
        guard = next((s for s in job['steps'] if s.get('name') == 'Require all dependency jobs to succeed'))
        self.assertEqual(guard['env']['MERIDIAN_CI_NEEDS'], '${{ toJSON(needs) }}')
        evaluate = next((i for i, s in enumerate(job['steps']) if '-Mode Aggregate' in s.get('run', '')))
        self.assertLess(job['steps'].index(guard), evaluate)
        pwsh = shutil.which('pwsh')
        self.assertTrue(pwsh)
        valid = {key: {'result': 'success'} for key in expected}
        controls = [(valid, 0), ({}, 1), ([], 1), (dict(valid, extra={'result': 'success'}), 1)]
        missing = dict(valid)
        missing.pop(next(iter(expected)))
        controls.append((missing, 1))
        for status in ('failure', 'cancelled', 'skipped', None, [], ['success'], True, {}, False, 42):
            bad = dict(valid)
            bad[next(iter(expected))] = {'result': status}
            controls.append((bad, 1))
        for payload, wanted in controls:
            with self.subTest(payload=payload):
                env = dict(os.environ, MERIDIAN_CI_NEEDS=json.dumps(payload))
                result = subprocess.run([pwsh, '-NoProfile', '-Command', guard['run']], cwd=ROOT, env=env, capture_output=True, text=True, timeout=60)
                self.assertEqual(result.returncode, wanted, result.stdout + result.stderr)

    def test_candidate_feed_is_immutable_and_downloaded_by_pack_job_output(self):
        jobs = yaml.safe_load((ROOT / '.github/workflows/ci.yml').read_text())['jobs']
        pack = jobs['candidate-pack']
        self.assertIn('feedArtifact', pack['outputs'])
        for job in jobs.values():
            for step in job.get('steps', []):
                if 'download-artifact' in str(step.get('uses', '')) and step.get('with', {}).get('path') == 'artifacts/candidate-feed':
                    self.assertEqual(step['with']['name'], '${{ needs.candidate-pack.outputs.feedArtifact }}')
        uploads = [s['with'] for s in pack['steps'] if 'upload-artifact' in str(s.get('uses', ''))]
        self.assertEqual(len(uploads), 1)
        self.assertIn('steps.version.outputs.candidate', uploads[0]['name'])
        self.assertEqual(pack['outputs']['feedArtifact'], uploads[0]['name'])
        self.assertFalse(overwrites(uploads[0]))

    def test_fuzz_uses_private_sdk_and_checks_actual_selection_before_build(self):
        job = yaml.safe_load((ROOT / '.github/workflows/fuzz.yml').read_text())['jobs']['property-tests-high-count']
        steps = job['steps']
        setup = next((s for s in steps if 'setup-dotnet' in str(s.get('uses', ''))))
        self.assertIn('runner.temp', setup.get('env', {}).get('DOTNET_INSTALL_DIR', ''))
        build = next((i for i, s in enumerate(steps) if 'dotnet build' in s.get('run', '')))
        probes = [s for s in steps[:build] if s.get('run', '').strip() == 'test "$(dotnet --version)" = "10.0.300"']
        self.assertEqual(len(probes), 1)

    def test_summary_finalizer_binds_this_attempt_and_records_skipped_or_failed_evaluations(self):
        job = yaml.safe_load((ROOT / '.github/workflows/ci.yml').read_text())['jobs']['candidate-gate']
        finalizer = next((s for s in job['steps'] if s.get('id') == 'finalize-summary'), None)
        self.assertIsNotNone(finalizer)
        self.assertEqual(finalizer['if'], 'always()')
        self.assertEqual(finalizer['env']['EVALUATION_OUTCOME'], '${{ steps.evaluate-candidate.outcome }}')
        for outcome, supplied, expected in [('failure', 'SCOPED-PASS', 'FAIL'), ('skipped', None, 'FAIL'),
                                            ('success', None, 'FAIL'), ('success', 'SCOPED-PASS', 'SCOPED-PASS')]:
            with self.subTest(outcome=outcome, supplied=supplied), tempfile.TemporaryDirectory() as directory:
                work = Path(directory) / 'artifacts/evidence'
                work.mkdir(parents=True)
                if supplied:
                    (work / 'summary.json').write_text(json.dumps({'schema': 'meridian-candidate-summary/2', 'overall': supplied, 'releaseEligible': False}))
                env = dict(os.environ, EVALUATION_OUTCOME=outcome, GITHUB_RUN_ID='123', GITHUB_RUN_ATTEMPT='4')
                result = subprocess.run([shutil.which('pwsh'), '-NoProfile', '-Command', finalizer['run']], cwd=directory,
                                        env=env, capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode, 1 if expected == 'FAIL' else 0, result.stdout + result.stderr)
                summary = json.loads((work / 'summary.json').read_text())
                self.assertEqual(summary['overall'], expected)
                self.assertEqual(summary['schema'], 'meridian-candidate-summary/2')
                self.assertFalse(summary['releaseEligible'])
                self.assertEqual(summary['ciAttempt'], {'runId': '123', 'runAttempt': '4'})
                self.assertTrue((work / 'summary.md').is_file())
        snapshots = [s for s in job['steps'] if 'upload-artifact' in s.get('uses', '')]
        self.assertTrue(all(s['with'].get('if-no-files-found') == 'error' for s in snapshots))
if __name__ == '__main__':
    unittest.main()
