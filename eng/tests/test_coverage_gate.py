"""Behavioral tests for the coverage ratchet and hot-path gate (eng/coverage_gate.py).

Fixtures are real Cobertura documents in the shape coverlet.collector writes (per-method line and condition data).
"""

import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "eng"))

import coverage_gate as cov  # noqa: E402

REPO = "/work/repo"


def cobertura(files):
    """files: {relative path: [(method, [(line, hits, [condition hits...])...])...]}"""
    classes = []
    for path, methods in files.items():
        method_xml, class_lines = [], []
        for name, lines in methods:
            line_xml = []
            for number, hits, conditions in lines:
                if conditions:
                    covered = sum(1 for c in conditions if c)
                    pct = int(100 * covered / len(conditions))
                    cond = "".join(f'<condition number="{i}" type="jump" coverage="{100 if c else 0}%" />' for i, c in enumerate(conditions))
                    xml = (f'<line number="{number}" hits="{hits}" branch="True" condition-coverage="{pct}% ({covered}/{len(conditions)})">'
                           f"<conditions>{cond}</conditions></line>")
                else:
                    xml = f'<line number="{number}" hits="{hits}" branch="False" />'
                line_xml.append(xml)
            method_xml.append(f'<method name="{name}" signature="()" line-rate="1" branch-rate="1"><lines>{"".join(line_xml)}</lines></method>')
            class_lines.extend(line_xml)
        classes.append(f'<class name="C" filename="{REPO}/{path}" line-rate="1" branch-rate="1"><methods>{"".join(method_xml)}</methods>'
                       f'<lines>{"".join(class_lines)}</lines></class>')
    return ('<?xml version="1.0" encoding="utf-8"?><coverage line-rate="1" branch-rate="1"><sources><source>' + REPO +
            '</source></sources><packages><package name="P"><classes>' + "".join(classes) + "</classes></package></packages></coverage>")


def write(directory, name, files):
    path = Path(directory) / name
    path.write_text(cobertura(files))
    return path


class Base(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="meridian-cov-"))
        self.addCleanup(shutil.rmtree, self.dir, ignore_errors=True)

    def measure(self, *docs):
        return cov.measure([Path(d) for d in docs], REPO)


class MeasureTests(Base):
    def test_line_and_branch_percentages_per_source_file(self):
        doc = write(self.dir, "a.xml", {"src/Lib/A.cs": [("M", [(1, 1, []), (2, 0, []), (3, 4, [True, False]), (4, 1, [True, True])])]})
        files = self.measure(doc)["files"]
        a = files["src/Lib/A.cs"]
        self.assertEqual((a["lines"]["covered"], a["lines"]["total"]), (3, 4))
        self.assertEqual((a["branches"]["covered"], a["branches"]["total"]), (3, 4))
        self.assertAlmostEqual(a["lines"]["percent"], 75.0)
        self.assertAlmostEqual(a["branches"]["percent"], 75.0)

    def test_only_repository_source_files_are_reported(self):
        doc = write(self.dir, "a.xml", {"src/Lib/A.cs": [("M", [(1, 1, [])])], "tests/T/T.cs": [("M", [(1, 1, [])])], "../other/X.cs": [("M", [(1, 1, [])])]})
        self.assertEqual(sorted(self.measure(doc)["files"]), ["src/Lib/A.cs"])

    def test_build_output_is_not_repository_source(self):
        doc = write(self.dir, "a.xml", {"src/Lib/A.cs": [("M", [(1, 1, [])])], "src/Lib/obj/Release/net10.0/Gen/Attr.g.cs": [("M", [(1, 0, [])])]})
        self.assertEqual(sorted(self.measure(doc)["files"]), ["src/Lib/A.cs"])

    def test_gaps_are_listed_per_file(self):
        doc = write(self.dir, "a.xml", {"src/Lib/A.cs": [("M", [(1, 1, []), (2, 0, []), (3, 1, [True, False]), (4, 1, [True, True])])]})
        a = self.measure(doc)["files"]["src/Lib/A.cs"]
        self.assertEqual(a["uncoveredLines"], [2])
        self.assertEqual(a["partialBranchLines"], [3])

    def test_relative_class_filenames_resolve_against_the_declared_source_root(self):
        text = ('<coverage><sources><source>' + REPO + '/src/Lib/</source></sources><packages><package name="P"><classes>'
                '<class name="C" filename="Sub/A.cs"><methods><method name="M" signature="()"><lines>'
                '<line number="1" hits="1" branch="True" condition-coverage="50% (1/2)"><conditions><condition number="0" type="jump" coverage="50%" /></conditions></line>'
                '</lines></method></methods></class></classes></package></packages></coverage>')
        path = Path(self.dir) / "rel.xml"
        path.write_text(text)
        a = self.measure(path)["files"]["src/Lib/Sub/A.cs"]
        self.assertEqual((a["branches"]["covered"], a["branches"]["total"]), (1, 2))

    def test_runs_are_merged_by_union_of_what_was_hit(self):
        one = write(self.dir, "one.xml", {"src/Lib/A.cs": [("M", [(1, 1, []), (2, 0, []), (3, 1, [True, False])])]})
        two = write(self.dir, "two.xml", {"src/Lib/A.cs": [("M", [(1, 0, []), (2, 3, []), (3, 1, [False, True])])]})
        a = self.measure(one, two)["files"]["src/Lib/A.cs"]
        self.assertEqual((a["lines"]["covered"], a["lines"]["total"]), (3, 3))
        self.assertEqual((a["branches"]["covered"], a["branches"]["total"]), (2, 2))

    def test_a_line_is_covered_if_any_method_instance_hit_it(self):
        doc = write(self.dir, "a.xml", {"src/Lib/A.cs": [("Lambda1", [(5, 0, [])]), ("Lambda2", [(5, 2, [])])]})
        a = self.measure(doc)["files"]["src/Lib/A.cs"]
        self.assertEqual((a["lines"]["covered"], a["lines"]["total"]), (1, 1))

    def test_file_without_branches_reports_no_branch_percentage(self):
        doc = write(self.dir, "a.xml", {"src/Lib/A.cs": [("M", [(1, 1, [])])]})
        a = self.measure(doc)["files"]["src/Lib/A.cs"]
        self.assertEqual(a["branches"]["total"], 0)
        self.assertIsNone(a["branches"]["percent"])

    def test_aggregate_is_computed_from_counts_not_from_percent_means(self):
        doc = write(self.dir, "a.xml", {"src/Lib/A.cs": [("M", [(1, 1, [])])], "src/Lib/B.cs": [("M", [(1, 0, []), (2, 0, []), (3, 0, []), (4, 1, [])])]})
        aggregate = self.measure(doc)["aggregate"]
        self.assertEqual((aggregate["lines"]["covered"], aggregate["lines"]["total"]), (2, 5))
        self.assertAlmostEqual(aggregate["lines"]["percent"], 40.0)


def baseline(files, hot=(), waivers=None, aggregate=None):
    return {"schema": "meridian-coverage-baseline/1", "targets": {"line": 90.0, "branch": 80.0},
            "aggregate": aggregate or {"line": 0.0, "branch": 0.0},
            "files": files, "hotPaths": list(hot), "waivers": waivers or {}}


class CheckTests(Base):
    def run_check(self, base, files, tolerance=0.0):
        doc = write(self.dir, "m.xml", files)
        return cov.check(base, self.measure(doc))

    def test_ratchet_passes_when_equal_and_when_improved(self):
        base = baseline({"src/Lib/A.cs": {"line": 50.0, "branch": 50.0}})
        result = self.run_check(base, {"src/Lib/A.cs": [("M", [(1, 1, [True, False]), (2, 0, [])])]})
        self.assertEqual(result["status"], "PASS", result)

    def test_line_coverage_decrease_fails_the_ratchet(self):
        base = baseline({"src/Lib/A.cs": {"line": 75.0, "branch": None}})
        result = self.run_check(base, {"src/Lib/A.cs": [("M", [(1, 1, []), (2, 0, []), (3, 0, []), (4, 1, [])])]})
        self.assertEqual(result["status"], "FAIL")
        self.assertEqual(result["ratchetRegressions"][0]["file"], "src/Lib/A.cs")
        self.assertEqual(result["ratchetRegressions"][0]["metric"], "line")

    def test_branch_coverage_decrease_fails_the_ratchet(self):
        base = baseline({"src/Lib/A.cs": {"line": 100.0, "branch": 100.0}})
        result = self.run_check(base, {"src/Lib/A.cs": [("M", [(1, 1, [True, False])])]})
        self.assertEqual(result["ratchetRegressions"][0]["metric"], "branch")

    def test_a_baseline_file_missing_from_measurement_fails(self):
        base = baseline({"src/Lib/A.cs": {"line": 10.0, "branch": None}, "src/Lib/Gone.cs": {"line": 10.0, "branch": None}})
        result = self.run_check(base, {"src/Lib/A.cs": [("M", [(1, 1, [])])]})
        self.assertEqual(result["status"], "FAIL")
        self.assertIn("src/Lib/Gone.cs", result["missingFiles"])

    def test_new_files_are_reported_and_do_not_fail_unless_hot(self):
        base = baseline({"src/Lib/A.cs": {"line": 10.0, "branch": None}})
        result = self.run_check(base, {"src/Lib/A.cs": [("M", [(1, 1, [])])], "src/Lib/New.cs": [("M", [(1, 0, [])])]})
        self.assertEqual(result["status"], "PASS")
        self.assertEqual(result["newFiles"], ["src/Lib/New.cs"])

    def test_a_declared_tolerance_absorbs_scheduling_noise_but_not_real_regressions(self):
        base = baseline({"src/Lib/A.cs": {"line": 75.0, "branch": None}})
        base["tolerance"] = 0.5
        noise = [(n, 1, []) for n in range(1, 748)] + [(n, 0, []) for n in range(748, 1001)]  # 74.7%: below the floor, inside the tolerance
        self.assertEqual(self.run_check(base, {"src/Lib/A.cs": [("M", noise)]})["status"], "PASS")
        regression = [(n, 1, []) for n in range(1, 74)] + [(n, 0, []) for n in range(74, 101)]  # 73%
        self.assertEqual(self.run_check(base, {"src/Lib/A.cs": [("M", regression)]})["status"], "FAIL")
        base["tolerance"] = 0.0
        self.assertEqual(self.run_check(base, {"src/Lib/A.cs": [("M", noise)]})["status"], "FAIL")
        slight = [(n, 1, []) for n in range(1, 75)] + [(n, 0, []) for n in range(75, 101)]  # 74%
        self.assertEqual(self.run_check(base, {"src/Lib/A.cs": [("M", slight)]})["status"], "FAIL")

    def test_a_negative_or_oversized_tolerance_is_rejected(self):
        for bad in (-0.1, 5.1):
            base = baseline({}, aggregate={"line": 0.0, "branch": 0.0})
            base["tolerance"] = bad
            with self.assertRaises(cov.BaselineError):
                self.run_check(base, {"src/Lib/A.cs": [("M", [(1, 1, [])])]})

    def test_aggregate_floor_is_enforced(self):
        base = baseline({"src/Lib/A.cs": {"line": 0.0, "branch": None}}, aggregate={"line": 80.0, "branch": 0.0})
        result = self.run_check(base, {"src/Lib/A.cs": [("M", [(1, 1, []), (2, 0, []), (3, 0, []), (4, 0, [])])]})
        self.assertEqual(result["status"], "FAIL")
        self.assertEqual(result["ratchetRegressions"][-1]["file"], "aggregate")

    def test_hot_path_below_targets_fails_without_a_waiver(self):
        base = baseline({"src/Lib/H.cs": {"line": 0.0, "branch": 0.0}}, hot=["src/Lib/H.cs"])
        result = self.run_check(base, {"src/Lib/H.cs": [("M", [(1, 1, []), (2, 0, []), (3, 1, [True, False])])]})
        self.assertEqual(result["status"], "FAIL")
        metrics = {(f["file"], f["metric"]) for f in result["hotPathFailures"]}
        self.assertEqual(metrics, {("src/Lib/H.cs", "line"), ("src/Lib/H.cs", "branch")})

    def test_hot_path_meeting_targets_passes(self):
        lines = [(n, 1, []) for n in range(1, 10)] + [(10, 0, [])]
        base = baseline({"src/Lib/H.cs": {"line": 0.0, "branch": 0.0}}, hot=["src/Lib/H.cs"])
        lines.append((11, 1, [True, True, True, True, False]))
        result = self.run_check(base, {"src/Lib/H.cs": [("M", lines)]})
        self.assertEqual(result["status"], "PASS", result)

    def test_waivers_are_rejected_even_with_a_written_reason(self):
        waivers = {"src/Lib/H.cs": {"branch": "unreachable defensive branch"}}
        base = baseline({"src/Lib/H.cs": {"line": 0.0, "branch": 0.0}}, hot=["src/Lib/H.cs"], waivers=waivers)
        with self.assertRaises(cov.BaselineError):
            self.run_check(base, {"src/Lib/H.cs": [("M", [(1, 1, []), (2, 0, []), (3, 1, [True, False])])]})

    def test_hot_path_targets_cannot_be_lowered_by_the_baseline(self):
        for metric, value in (("line", 89.9), ("branch", 79.0)):
            base = baseline({"src/Lib/H.cs": {"line": 0.0, "branch": None}}, hot=["src/Lib/H.cs"])
            base["targets"] = {metric: value}
            with self.subTest(metric=metric), self.assertRaises(cov.BaselineError):
                self.run_check(base, {"src/Lib/H.cs": [("M", [(n, 1, []) for n in range(1, 11)])]})
        raised = baseline({"src/Lib/H.cs": {"line": 0.0, "branch": None}}, hot=["src/Lib/H.cs"])
        raised["targets"] = {"line": 95.0}
        result = self.run_check(raised, {"src/Lib/H.cs": [("M", [(n, 1, []) for n in range(1, 11)] + [(11, 0, [])])]})
        self.assertEqual(result["hotPathFailures"], [{"file": "src/Lib/H.cs", "metric": "line", "percent": 90.91, "target": 95.0}])

    def test_a_file_measured_without_lines_or_branches_fails_its_recorded_floor(self):
        base = baseline({"src/Lib/A.cs": {"line": 50.0, "branch": 40.0}})
        result = self.run_check(base, {"src/Lib/A.cs": [("M", [(1, 1, [])])]})
        self.assertEqual([(r["file"], r["metric"], r["current"]) for r in result["ratchetRegressions"]], [("src/Lib/A.cs", "branch", None)])
        empty = write(self.dir, "empty-class.xml", {"src/Lib/A.cs": []})
        result = cov.check(base, self.measure(empty))
        self.assertEqual(result["status"], "FAIL")
        self.assertEqual(sorted(r["metric"] for r in result["ratchetRegressions"] if r["file"] == "src/Lib/A.cs"), ["branch", "line"])

    def test_a_hot_path_with_no_measured_lines_fails(self):
        base = baseline({"src/Lib/H.cs": {"line": None, "branch": None}}, hot=["src/Lib/H.cs"])
        result = cov.check(base, self.measure(write(self.dir, "empty-hot.xml", {"src/Lib/H.cs": []})))
        self.assertEqual(result["status"], "FAIL")
        self.assertEqual(result["hotPathFailures"], [{"file": "src/Lib/H.cs", "metric": "line", "percent": None, "target": 90.0}])

    def test_an_aggregate_without_measured_lines_fails_its_floor(self):
        base = baseline({}, aggregate={"line": 10.0, "branch": 0.0})
        result = cov.check(base, self.measure(write(self.dir, "empty-all.xml", {"src/Lib/A.cs": []})))
        self.assertEqual([r["file"] for r in result["ratchetRegressions"]], ["aggregate"])

    def test_a_hot_path_absent_from_the_coverage_data_fails(self):
        base = baseline({}, hot=["src/Lib/H.cs"])
        result = self.run_check(base, {"src/Lib/A.cs": [("M", [(1, 1, [])])]})
        self.assertEqual(result["status"], "FAIL")
        self.assertEqual(result["hotPathFailures"][0]["metric"], "present")

    def test_hot_path_without_branches_needs_only_the_line_target(self):
        lines = [(n, 1, []) for n in range(1, 11)]
        base = baseline({"src/Lib/H.cs": {"line": 0.0, "branch": None}}, hot=["src/Lib/H.cs"])
        self.assertEqual(self.run_check(base, {"src/Lib/H.cs": [("M", lines)]})["status"], "PASS")


class RaiseBaselineTests(Base):
    def test_update_never_lowers_a_floor_and_adds_new_files(self):
        existing = baseline({"src/Lib/A.cs": {"line": 90.0, "branch": 50.0}}, aggregate={"line": 90.0, "branch": 50.0})
        doc = write(self.dir, "m.xml", {"src/Lib/A.cs": [("M", [(1, 1, []), (2, 0, [])])], "src/Lib/B.cs": [("M", [(1, 1, [])])]})
        updated = cov.raise_baseline(existing, self.measure(doc))
        self.assertEqual(updated["files"]["src/Lib/A.cs"]["line"], 90.0)
        self.assertEqual(updated["files"]["src/Lib/B.cs"]["line"], 100.0)
        self.assertEqual(updated["aggregate"]["line"], 90.0)

    def test_update_raises_floors_when_coverage_improved_and_keeps_hot_paths(self):
        existing = baseline({"src/Lib/A.cs": {"line": 40.0, "branch": None}}, hot=["src/Lib/A.cs"])
        doc = write(self.dir, "m.xml", {"src/Lib/A.cs": [("M", [(1, 1, []), (2, 1, [])])]})
        updated = cov.raise_baseline(existing, self.measure(doc))
        self.assertEqual(updated["files"]["src/Lib/A.cs"]["line"], 100.0)
        self.assertEqual(updated["hotPaths"], ["src/Lib/A.cs"])
        self.assertNotIn("waivers", updated)

    def test_update_refuses_a_baseline_with_waivers(self):
        existing = baseline({"src/Lib/A.cs": {"line": 40.0, "branch": None}}, hot=["src/Lib/A.cs"], waivers={"src/Lib/A.cs": {"line": "why"}})
        doc = write(self.dir, "m.xml", {"src/Lib/A.cs": [("M", [(1, 1, [])])]})
        with self.assertRaises(cov.BaselineError):
            cov.raise_baseline(existing, self.measure(doc))

    def test_floors_are_rounded_down_so_unchanged_coverage_never_regresses(self):
        doc = write(self.dir, "m.xml", {"src/Lib/A.cs": [("M", [(1, 1, []), (2, 1, []), (3, 0, [])])]})
        updated = cov.raise_baseline(baseline({}), self.measure(doc))
        self.assertEqual(updated["files"]["src/Lib/A.cs"]["line"], 66.6)
        self.assertEqual(cov.check(updated, self.measure(doc))["status"], "PASS")


class CommandLineTests(Base):
    def run_cli(self, *args):
        return subprocess.run([sys.executable, "-B", str(ROOT / "eng/coverage_gate.py"), *args], capture_output=True, text=True)

    def test_check_exit_codes(self):
        doc = write(self.dir, "m.xml", {"src/Lib/A.cs": [("M", [(1, 1, []), (2, 0, [])])]})
        good = self.dir / "good.json"
        good.write_text(json.dumps(baseline({"src/Lib/A.cs": {"line": 50.0, "branch": None}})))
        bad = self.dir / "bad.json"
        bad.write_text(json.dumps(baseline({"src/Lib/A.cs": {"line": 51.0, "branch": None}})))
        out = self.dir / "result.json"
        ok = self.run_cli("check", "--baseline", str(good), "--repo", REPO, "--output", str(out), str(doc))
        self.assertEqual(ok.returncode, 0, ok.stdout + ok.stderr)
        self.assertEqual(json.loads(out.read_text())["status"], "PASS")
        self.assertEqual(json.loads(out.read_text())["repo"], REPO)
        failed = self.run_cli("check", "--baseline", str(bad), "--repo", REPO, "--output", str(out), str(doc))
        self.assertEqual(failed.returncode, 1)
        self.assertEqual(json.loads(out.read_text())["status"], "FAIL")

    def test_no_cobertura_input_fails_closed(self):
        good = self.dir / "good.json"
        good.write_text(json.dumps(baseline({})))
        result = self.run_cli("check", "--baseline", str(good), "--repo", REPO)
        self.assertNotEqual(result.returncode, 0)

    def test_empty_coverage_document_fails_closed(self):
        empty = self.dir / "empty.xml"
        empty.write_text(cobertura({}))
        good = self.dir / "good.json"
        good.write_text(json.dumps(baseline({"src/Lib/A.cs": {"line": 1.0, "branch": None}})))
        result = self.run_cli("check", "--baseline", str(good), "--repo", REPO, str(empty))
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
