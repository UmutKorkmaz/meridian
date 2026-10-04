"""Execute the package verifier's consumer-selection function with actual inventory-shaped values."""
import json
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


class ConsumerSelectionTests(unittest.TestCase):
    def invoke(self, excluded):
        expression = "@(" + ",".join("'" + x + "'" for x in excluded) + ")"
        script = """
$ErrorActionPreference='Stop'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile('eng/verify-packages.ps1',[ref]$tokens,[ref]$errors)
$function=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Select-PackageConsumers'},$true)
if(-not $function){throw 'Missing consumer selection implementation'}
Invoke-Expression $function.Extent.Text
$items=@([pscustomobject]@{id='old';baselineComparison=$true;packageIds=@('core')},[pscustomobject]@{id='redis';baselineComparison=$false;packageIds=@('redis')})
$result=Select-PackageConsumers $items EXCLUDED
ConvertTo-Json -InputObject $result -Depth 8 -Compress
""".replace("EXCLUDED", expression)
        return subprocess.run(["pwsh", "-NoProfile", "-Command", script], cwd=ROOT, capture_output=True, text=True)

    def test_default_keeps_all_and_baseline_closure_inputs(self):
        result = self.invoke([])
        self.assertEqual(result.returncode, 0, result.stderr)
        data = json.loads(result.stdout)
        self.assertEqual(data["SelectedIds"], ["old", "redis"])
        self.assertEqual(data["OmittedIds"], [])
        self.assertEqual(data["Scope"], "all")
        self.assertEqual([c["packageIds"] for c in data["Consumers"] if c["baselineComparison"]], [["core"]])

    def test_explicit_partial_selection_preserves_only_selected_baselines(self):
        result = self.invoke(["old"])
        self.assertEqual(result.returncode, 0, result.stderr)
        data = json.loads(result.stdout)
        self.assertEqual(data["SelectedIds"], ["redis"])
        self.assertEqual(data["OmittedIds"], ["old"])
        self.assertEqual(data["Scope"], "partial")
        self.assertFalse(any(c["baselineComparison"] for c in data["Consumers"]))

    def test_unknown_duplicate_and_empty_choices_fail(self):
        for excluded in [["unknown"], ["redis", "redis"], ["old", "redis"]]:
            with self.subTest(excluded=excluded):
                result = self.invoke(excluded)
                self.assertNotEqual(result.returncode, 0)
                self.assertNotIn("Missing consumer selection", result.stderr)


if __name__ == "__main__":
    unittest.main()
