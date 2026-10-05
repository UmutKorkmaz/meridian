"""Behavioral tests for eng/warning-inventory.py: parsing, multi-target collapsing, the ratchet check and its refusals.

The tool is exercised through its command line on synthetic MSBuild output with known counts, and the committed
eng/warning-baseline.json is validated structurally. No test builds the solution; the E4b/E4c gates do that.
"""

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "eng" / "warning-inventory.py"
BASELINE = ROOT / "eng" / "warning-baseline.json"
REPO = "/repo"


def warning(project: str, file: str, line: int, code: str, message: str = "msg", tfm: str | None = None, col: int = 5) -> str:
    suffix = f"::TargetFramework={tfm}" if tfm else ""
    return f"{REPO}/{file}({line},{col}): warning {code}: {message} (https://learn.microsoft.com/x) [{REPO}/{project}{suffix}]"


class WarningInventoryTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def run_tool(self, *args):
        return subprocess.run([sys.executable, "-B", str(SCRIPT), *map(str, args)], capture_output=True, text=True)

    def log(self, name: str, lines: list[str], summary: bool = True) -> Path:
        body = list(lines)
        if summary:
            body += [f"    {len(lines)} Warning(s)", "    0 Error(s)"]
        path = self.tmp / name
        path.write_text("\n".join(body) + "\n", encoding="utf-8")
        return path

    def write_baseline(self, logs: list[Path]) -> Path:
        out = self.tmp / "baseline.json"
        result = self.run_tool("baseline", "--repo", REPO, "--output", out, *logs)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return out

    def check(self, baseline: Path, logs: list[Path]):
        return self.run_tool("check", "--repo", REPO, "--baseline", baseline, "--output", self.tmp / "report.json", *logs)

    LIB = "src/Lib/Lib.csproj"
    TESTS = "tests/Lib.Tests/Lib.Tests.csproj"

    def base_lines(self):
        return [
            # The same location printed twice (MSBuild does this) and once per target framework collapses to one.
            warning(self.LIB, "src/Lib/A.cs", 10, "CA1305", tfm="net8.0"),
            warning(self.LIB, "src/Lib/A.cs", 10, "CA1305", tfm="net8.0"),
            warning(self.LIB, "src/Lib/A.cs", 10, "CA1305", tfm="net9.0"),
            warning(self.LIB, "src/Lib/A.cs", 22, "CA1305", tfm="net8.0"),
            warning(self.LIB, "src/Lib/B.cs", 3, "CA1711", tfm="net8.0"),
            warning(self.TESTS, "tests/Lib.Tests/T.cs", 7, "CA1861"),
        ]

    def test_inventory_counts_unique_locations_not_repetitions(self):
        out = self.tmp / "inventory.json"
        result = self.run_tool("inventory", "--repo", REPO, "--output", out, self.log("b.log", self.base_lines()))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        document = json.loads(out.read_text())
        self.assertEqual(document["total"], 4)
        self.assertEqual(document["byCode"], {"CA1305": 2, "CA1711": 1, "CA1861": 1})
        self.assertEqual(document["projects"][self.LIB]["codes"], {"CA1305": 2, "CA1711": 1})
        first = next(l for l in document["locations"] if l["line"] == 10)
        self.assertEqual(first["tfms"], ["net8.0", "net9.0"])
        self.assertEqual(first["file"], "src/Lib/A.cs")

    def test_identical_log_passes_and_fewer_warnings_pass_as_improvement(self):
        baseline = self.write_baseline([self.log("b.log", self.base_lines())])
        self.assertEqual(self.check(baseline, [self.log("same.log", self.base_lines())]).returncode, 0)
        fewer = self.check(baseline, [self.log("fewer.log", self.base_lines()[:4])])
        self.assertEqual(fewer.returncode, 0, fewer.stdout)
        report = json.loads((self.tmp / "report.json").read_text())
        improved = {(i["project"], i["code"]): (i["baseline"], i["current"]) for i in report["improved"]}
        self.assertEqual(improved[(self.TESTS, "CA1861")], (1, 0))
        self.assertEqual(improved[(self.LIB, "CA1711")], (1, 0))

    def test_line_shifts_do_not_fail_but_a_new_location_does(self):
        baseline = self.write_baseline([self.log("b.log", self.base_lines())])
        shifted = [l.replace("A.cs(10,", "A.cs(40,") for l in self.base_lines()]
        self.assertEqual(self.check(baseline, [self.log("shift.log", shifted)]).returncode, 0)
        grown = self.base_lines() + [warning(self.LIB, "src/Lib/C.cs", 1, "CA1305", tfm="net8.0")]
        result = self.check(baseline, [self.log("grown.log", grown)])
        self.assertEqual(result.returncode, 1)
        self.assertIn(f"INCREASE CA1305 in {self.LIB}: 3 > baseline 2", result.stdout)

    def test_new_code_and_new_project_fail(self):
        baseline = self.write_baseline([self.log("b.log", self.base_lines())])
        new_code = self.base_lines() + [warning(self.LIB, "src/Lib/A.cs", 50, "CA1822")]
        result = self.check(baseline, [self.log("code.log", new_code)])
        self.assertEqual(result.returncode, 1)
        self.assertIn(f"NEW code CA1822 in {self.LIB}", result.stdout)
        new_project = self.base_lines() + [warning("src/Other/Other.csproj", "src/Other/O.cs", 1, "CA1305")]
        result = self.check(baseline, [self.log("project.log", new_project)])
        self.assertEqual(result.returncode, 1)
        self.assertIn("NEW project with warnings: src/Other/Other.csproj", result.stdout)

    def test_unreadable_warning_lines_and_vanished_output_are_failures_not_success(self):
        baseline = self.write_baseline([self.log("b.log", self.base_lines())])
        garbled = self.base_lines() + ["Foo.cs: warning CA1305: format changed without a location or project"]
        result = self.check(baseline, [self.log("garbled.log", garbled)])
        self.assertEqual(result.returncode, 1)
        self.assertIn("unparsed warning line", result.stdout)
        empty = self.check(baseline, [self.log("empty.log", [])])
        self.assertEqual(empty.returncode, 1)
        self.assertIn("refusing to treat a vanished warning set as success", empty.stdout)

    def test_baseline_refuses_to_hide_unreadable_lines(self):
        garbled = self.base_lines() + ["Foo.cs: warning CA1305: unreadable"]
        result = self.run_tool("baseline", "--repo", REPO, "--output", self.tmp / "x.json", self.log("g.log", garbled))
        self.assertEqual(result.returncode, 1)
        self.assertFalse((self.tmp / "x.json").exists())

    def test_committed_baseline_is_well_formed_and_internally_consistent(self):
        baseline = json.loads(BASELINE.read_text(encoding="utf-8"))
        self.assertEqual(baseline["schemaVersion"], 1)
        total_by_project = 0
        by_code: dict[str, int] = {}
        for project, entry in baseline["projects"].items():
            self.assertTrue(project.endswith(".csproj"), project)
            self.assertEqual(entry["total"], sum(entry["codes"].values()), project)
            total_by_project += entry["total"]
            for code, count in entry["codes"].items():
                by_code[code] = by_code.get(code, 0) + count
        self.assertEqual(baseline["total"], total_by_project)
        self.assertEqual(baseline["byCode"], dict(sorted(by_code.items())))
        # The rules E4b established must not silently regress in the committed ratchet.
        self.assertNotIn("RS2008", baseline["byCode"])
        self.assertNotIn("RS2001", baseline["byCode"])
        self.assertNotIn("CA1822", baseline["projects"].get("src/Meridian.Mapping/Meridian.Mapping.csproj", {}).get("codes", {}))
        for project in ("src/Meridian.Mapping.Generators/Meridian.Mapping.Generators.csproj",):
            self.assertNotIn(project, baseline["projects"])


if __name__ == "__main__":
    unittest.main()
