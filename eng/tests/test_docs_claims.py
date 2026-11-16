"""Static claim checks for published documentation and package metadata (E1 fix round 1).

These tests keep the stable-versus-candidate qualifiers, neutral package metadata and the limit/option wording from
regressing. Executable behavior is asserted by tests/consumers/Meridian.DocumentationConsumer."""

import re
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# Public names that do not exist in the published 2.1.3 packages (verified by metadata name search of the 2.1.3
# assemblies): they only exist in the unpublished 2.1.4 candidate.
CANDIDATE_ONLY = (
    "CacheKeyComposer",
    "MissingAuthorizationHandlerException",
    "AuthorizationBehaviorOptions",
    "IdempotencyBehaviorOptions",
    "UseRequestTypeNamespacedKeys",
    "MediatorMetricsOptions",
    "MeridianEnumMappingException",
    "AllowUndefinedEnumValues",
    "UseGeneratedMappings",
    "GeneratedMappingOptions",
)


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


def prose_paragraphs(text: str):
    """Blank-line separated paragraphs outside code fences."""
    paragraphs, current, fenced = [], [], False
    for line in text.splitlines():
        if line.lstrip().startswith(("```", "~~~")):
            fenced = not fenced
            continue
        if fenced:
            continue
        if line.strip():
            current.append(line)
        elif current:
            paragraphs.append("\n".join(current))
            current = []
    if current:
        paragraphs.append("\n".join(current))
    return paragraphs


class MigrationGuideVersionQualifierTests(unittest.TestCase):
    GUIDES = {
        "docs/migrate-from-mediatr.md": ("authorization", "cache"),
        "docs/migrate-from-automapper.md": ("enum",),
    }

    def test_banner_states_stable_versus_candidate_before_the_install_command(self):
        for guide, topics in self.GUIDES.items():
            with self.subTest(guide=guide):
                text = read(guide)
                head = text.split("## Install", 1)[0]
                self.assertIn("2.1.4 candidate", head, "banner must name the 2.1.4 candidate before the install command")
                self.assertIn("2.1.3", head)
                self.assertIn("capabilities.md", head, "banner must link the capabilities ledger")
                lowered = head.lower()
                for topic in topics:
                    self.assertIn(topic, lowered, f"banner must say how stable 2.1.3 differs for {topic}")

    def test_every_paragraph_naming_a_candidate_only_api_carries_the_qualifier(self):
        for guide in self.GUIDES:
            with self.subTest(guide=guide):
                for paragraph in prose_paragraphs(read(guide)):
                    names = [n for n in CANDIDATE_ONLY if re.search(r"\b" + n + r"\b", paragraph)]
                    if names:
                        self.assertTrue("2.1.4" in paragraph, f"unqualified candidate-only API {names}: {paragraph[:80]!r}")

    def test_behavior_lists_mark_candidate_only_items_inline(self):
        mediatr = read("docs/migrate-from-mediatr.md")
        section = mediatr.split('<a id="behavioral-differences"></a>', 1)[1].split("## Non-goals", 1)[0]
        for item in re.split(r"\n(?=\d+\. )", section.strip()):
            if any(re.search(r"\b" + n + r"\b", item) for n in CANDIDATE_ONLY):
                self.assertIn("2.1.4", item, item[:60])
        automapper = read("docs/migrate-from-automapper.md")
        for line in automapper.splitlines():
            if line.startswith(">"):
                continue  # the version banner is checked separately
            if "MeridianEnumMappingException" in line or "AllowUndefinedEnumValues" in line:
                self.assertIn("2.1.4", line)


class PackageMetadataTests(unittest.TestCase):
    RUNTIME = ("src/Meridian.Mediator/Meridian.Mediator.csproj", "src/Meridian.Mapping/Meridian.Mapping.csproj")

    def properties(self, project: str):
        values = {}
        for group in ET.parse(ROOT / project).getroot().iter("PropertyGroup"):
            for child in group:
                values[child.tag] = (child.text or "").strip()
        return values

    def test_descriptions_make_no_performance_claim(self):
        for project in self.RUNTIME:
            with self.subTest(project=project):
                description = self.properties(project)["Description"].lower()
                for word in ("high-performance", "fastest", "zero-allocation", "blazing"):
                    self.assertNotIn(word, description)

    def test_mapping_tags_make_no_blanket_aot_claim(self):
        tags = self.properties("src/Meridian.Mapping/Meridian.Mapping.csproj")["PackageTags"].split(";")
        self.assertNotIn("aot", [t.lower() for t in tags])


class SafetyLimitWordingTests(unittest.TestCase):
    def test_compatibility_policy_does_not_offer_a_disabled_limit(self):
        row = next(l for l in read("docs/compatibility-policy.md").splitlines() if l.startswith("| `DefaultMaxCollectionItems`"))
        self.assertNotRegex(row.lower(), r"or disable|disable explicitly")
        self.assertIn("no disabled setting", row)
        self.assertIn("positive", row)

    def test_generated_options_wording_matches_emitted_override_semantics(self):
        text = read("docs/generated-mapping.md")
        self.assertNotRegex(text, r"can only tighten", "direct entry methods let options replace the attribute limits")
        limits = text.split('<a id="limits"></a>', 1)[1].split("## Diagnostics", 1)[0]
        self.assertIn("replace", limits)
        self.assertIn("positive", limits)
        self.assertIn("resolver", limits.lower())
        self.assertIn("minimum", limits)


if __name__ == "__main__":
    unittest.main()
