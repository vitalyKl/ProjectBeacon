#!/usr/bin/env python3
"""Unit tests for release manifest tag-pattern validation."""
from __future__ import annotations

import json
import subprocess
import tempfile
import tomllib
import unittest
from pathlib import Path
from contextlib import redirect_stderr
from io import StringIO
from unittest.mock import patch

from verify_release_manifest import (
    dotnet_regex_to_python,
    fail,
    is_safe_git_tag_name,
    read_pointer,
    require_positive_json_integer,
    safe_manifest_path,
    read_regex_version,
    read_toml_version,
    read_xml_version,
    validate_portable_dotnet_regex,
    validate_release_snapshot,
    validate_tag_pattern,
    validate_version,
)


class ReleaseManifestSafetyTests(unittest.TestCase):
    def test_regex_execution_timeout_fails_closed(self) -> None:
        timeout = subprocess.TimeoutExpired(cmd="regex-worker", timeout=2.0)
        stderr = StringIO()
        with patch("verify_release_manifest.subprocess.run", side_effect=timeout) as run:
            with redirect_stderr(stderr), self.assertRaises(SystemExit):
                read_regex_version("version=1.2.3", r"version=(?<version>.+)")

        self.assertEqual(2.0, run.call_args.kwargs["timeout"])
        self.assertIn("execution limit", stderr.getvalue())

    def test_expected_validation_failures_do_not_emit_github_error_annotations(self) -> None:
        stderr = StringIO()
        with redirect_stderr(stderr), self.assertRaises(SystemExit):
            fail("expected invalid test input")

        self.assertIn("expected invalid test input", stderr.getvalue())
        self.assertNotIn("::error::", stderr.getvalue())


class ManifestPathSafetyTests(unittest.TestCase):
    def test_accepts_regular_manifest_inside_regular_beacon_directory(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            beacon_dir = root / ".beacon"
            beacon_dir.mkdir()
            manifest = beacon_dir / "release.json"
            manifest.write_text("{}", encoding="utf-8")

            self.assertEqual(manifest, safe_manifest_path(root))

    def test_rejects_beacon_directory_symlink_even_when_target_contains_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "checkout"
            root.mkdir()
            outside = Path(temporary) / "outside"
            outside.mkdir()
            (outside / "release.json").write_text("{}", encoding="utf-8")
            (root / ".beacon").symlink_to(outside, target_is_directory=True)

            stderr = StringIO()
            with redirect_stderr(stderr), self.assertRaises(SystemExit):
                safe_manifest_path(root)

            self.assertIn("regular .beacon directory", stderr.getvalue())

    def test_rejects_release_manifest_file_symlink(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "checkout"
            beacon_dir = root / ".beacon"
            beacon_dir.mkdir(parents=True)
            outside_manifest = Path(temporary) / "release.json"
            outside_manifest.write_text("{}", encoding="utf-8")
            (beacon_dir / "release.json").symlink_to(outside_manifest)

            stderr = StringIO()
            with redirect_stderr(stderr), self.assertRaises(SystemExit):
                safe_manifest_path(root)

            self.assertIn("regular .beacon/release.json file", stderr.getvalue())


class ReleaseSnapshotIntegrityTests(unittest.TestCase):
    project_id = "11111111-1111-4111-8111-111111111111"
    release_id = "22222222-2222-4222-8222-222222222222"
    version = "0.7.5.10"
    target_branch = "release/0.7.5"

    def snapshot(self, *, schema_version=1, release_revision=4, project_id=None):
        return json.dumps({
            "schemaVersion": schema_version,
            "projectId": project_id or self.project_id,
            "versionPolicyRevision": 3,
            "versionPolicy": {
                "scheme": "NumericComponents",
                "componentCount": 4,
                "calendarFormat": None,
                "tagPattern": "v{version}",
                "allowPrerelease": False,
                "sources": [],
            },
            "release": {
                "releaseId": self.release_id,
                "targetVersion": self.version,
                "targetBranch": self.target_branch,
                "releaseRevision": release_revision,
            },
        })

    def validate(self, snapshot_json):
        validate_release_snapshot(
            snapshot_json,
            self.project_id,
            self.release_id,
            self.version,
            self.target_branch,
            3,
            4,
            {
                "scheme": "NumericComponents",
                "componentCount": 4,
                "calendarFormat": None,
                "tagPattern": "v{version}",
                "allowPrerelease": False,
            },
        )

    def test_accepts_snapshot_bound_to_exact_manifest_identity_and_revisions(self) -> None:
        self.validate(self.snapshot())

    def test_rejects_snapshot_with_different_release_revision(self) -> None:
        with self.assertRaises(SystemExit):
            self.validate(self.snapshot(release_revision=5))

    def test_rejects_boolean_snapshot_schema_version(self) -> None:
        with self.assertRaises(SystemExit):
            self.validate(self.snapshot(schema_version=True))

    def test_rejects_snapshot_from_another_project(self) -> None:
        other_project = "33333333-3333-4333-8333-333333333333"
        with self.assertRaises(SystemExit):
            self.validate(self.snapshot(project_id=other_project))

    def test_positive_revision_must_be_an_integer_json_number_not_a_boolean_or_float(self) -> None:
        self.assertEqual(4, require_positive_json_integer(4, "revision"))
        for invalid in (True, False, 1.0, "4", 0, -1):
            with self.subTest(value=invalid), self.assertRaises(SystemExit):
                require_positive_json_integer(invalid, "revision")


class ReleasePolicyTypeSafetyTests(unittest.TestCase):
    def test_numeric_component_count_must_be_a_json_integer_not_a_boolean(self) -> None:
        for invalid in (True, False, 4.0, "4", None):
            with self.subTest(value=invalid), self.assertRaises(SystemExit):
                validate_version(
                    {"scheme": "NumericComponents", "componentCount": invalid, "allowPrerelease": False},
                    "0.7.5.10",
                )

    def test_semver_policy_requires_exactly_three_components(self) -> None:
        with self.assertRaises(SystemExit):
            validate_version(
                {"scheme": "SemVer", "componentCount": 4, "allowPrerelease": False},
                "1.2.3",
            )

    def test_semver_numeric_prerelease_identifiers_cannot_have_leading_zeroes(self) -> None:
        policy = {"scheme": "SemVer", "componentCount": 3, "allowPrerelease": True}
        valid = (
            "1.2.3-0",
            "1.2.3-rc.1",
            "1.2.3-01alpha",
            "1.2.3-rc.1+build.0001",
        )
        invalid = (
            "1.2.3-01",
            "1.2.3-rc.01",
            "1.2.3-01+build.1",
        )
        for version in valid:
            with self.subTest(version=version):
                validate_version(policy, version)
        for version in invalid:
            with self.subTest(version=version), self.assertRaises(SystemExit):
                validate_version(policy, version)

    def test_allow_prerelease_must_be_a_json_boolean_for_every_scheme(self) -> None:
        for invalid in (1, 0, "false", None):
            with self.subTest(value=invalid), self.assertRaises(SystemExit):
                validate_version(
                    {"scheme": "NumericComponents", "componentCount": 4, "allowPrerelease": invalid},
                    "0.7.5.10",
                )


class VersionSourceParityTests(unittest.TestCase):
    def test_json_pointer_supports_document_root_and_rfc_6901_escaped_object_keys(self) -> None:
        self.assertEqual("0.7.5.9", read_pointer("0.7.5.9", ""))
        document = {"a/b": {"m~n": "0.7.5.9"}}
        self.assertEqual("0.7.5.9", read_pointer(document, "/a~1b/m~0n"))

    def test_json_pointer_rejects_invalid_escape_sequences(self) -> None:
        document = {"version": "0.7.5.9", "version~2name": "wrong"}
        for pointer in ("/version~", "/version~2name", "/version~~0"):
            with self.subTest(pointer=pointer), self.assertRaises(SystemExit):
                read_pointer(document, pointer)

    def test_json_pointer_array_indexes_use_ascii_non_padded_decimal(self) -> None:
        document = {"versions": ["0.7.5.9"]}
        self.assertEqual("0.7.5.9", read_pointer(document, "/versions/0"))
        for pointer in ("/versions/00", "/versions/٠", "/versions/+0", "/versions/1", "/versions/"):
            with self.subTest(pointer=pointer), self.assertRaises(SystemExit):
                read_pointer(document, pointer)

    def test_toml_projection_accepts_only_string_and_integer_scalars(self) -> None:
        self.assertEqual("0.7.5.9", read_toml_version(
            tomllib.loads('version = "0.7.5.9"'), "version"))
        self.assertEqual("7", read_toml_version(
            tomllib.loads("version = 7"), "version"))

        for source in ("version = 1.25", "version = true", "version = [7]"):
            with self.subTest(source=source), self.assertRaises(SystemExit):
                read_toml_version(tomllib.loads(source), "version")

    def test_regex_validation_uses_the_same_portable_grammar(self) -> None:
        accepted = (
            r"^version=(?<version>[0-9.]+)$",
            r"(?=version=)version=(?<version>\d+)",
            r"version=(?<version>\d+)\k<version>",
        )
        rejected = (
            r"version=(?<version>\w+)",
            r"version=(?<version>\0)",
            r"version=(?<version>\a)",
            r"version=(?<version>\x30+)",
            r"version=(?<version>\p{L}+)",
            r"version=(?<version>\d+)\1",
            r"version=(?<version>\d+)\k<other>",
            r"version=\k<version>(?<version>\d+)",
            r"(?i)version=(?<version>\d+)",
            r"version=(?<version>[a-z-[aeiou]]+)",
            r"version=(?<version>[0-9]+)(?<other>[0-9]+)",
        )
        for selector in accepted:
            with self.subTest(selector=selector):
                self.assertEqual((True, None), validate_portable_dotnet_regex(selector))
        for selector in rejected:
            with self.subTest(selector=selector):
                self.assertFalse(validate_portable_dotnet_regex(selector)[0])


class ReleaseManifestProjectionTests(unittest.TestCase):
    def test_reads_absolute_and_descendant_xml_location_paths(self) -> None:
        xml = "<Project><PropertyGroup><Version>0.7.5.9</Version></PropertyGroup></Project>"
        self.assertEqual("0.7.5.9", read_xml_version(xml, "/Project/PropertyGroup/Version"))
        self.assertEqual("0.7.5.9", read_xml_version(xml, "//Version"))
        self.assertEqual("0.7.5.9", read_xml_version(xml, "//PropertyGroup/Version"))

    def test_rejects_xpath_features_that_policy_validation_rejects(self) -> None:
        xml = "<Project><Version>0.7.5.9</Version></Project>"
        for selector in ("//Version[1]", "//Version[@Condition='release']", "//*[local-name()='Version']", "//Version | //PackageVersion"):
            with self.subTest(selector=selector), self.assertRaises(SystemExit):
                read_xml_version(xml, selector)

    def test_supports_dotnet_named_regex_groups(self) -> None:
        self.assertEqual(
            "0.7.5.9",
            read_regex_version("version=0.7.5.9", r"version=(?<version>[0-9.]+)"),
        )
        self.assertEqual(
            "0.7.5.9",
            read_regex_version("version=0.7.5.9", r"version=([0-9.]+)"),
        )

    def test_translates_dotnet_named_backreferences_without_touching_lookbehind(self) -> None:
        self.assertEqual(
            r"(?P<version>[0-9.]+)-(?P=version)",
            dotnet_regex_to_python(r"(?<version>[0-9.]+)-\k<version>"),
        )
        self.assertEqual(
            r"(?<=version=)[0-9.]+",
            dotnet_regex_to_python(r"(?<=version=)[0-9.]+"),
        )


class ReleaseTagPatternTests(unittest.TestCase):
    def test_accepts_default_four_component_numeric_tag(self) -> None:
        self.assertEqual("v0.7.5.9", validate_tag_pattern("v{version}", "0.7.5.9", "v0.7.5.9"))

    def test_accepts_customer_prefix_for_semver(self) -> None:
        self.assertEqual(
            "release-2.4.1",
            validate_tag_pattern("release-{version}", "2.4.1", "release-2.4.1"),
        )

    def test_accepts_hierarchical_git_tag_pattern(self) -> None:
        self.assertEqual(
            "customer/releases/2.4.1",
            validate_tag_pattern("customer/releases/{version}", "2.4.1", "customer/releases/2.4.1"),
        )

    def test_rejects_tag_that_does_not_match_exact_pattern(self) -> None:
        with self.assertRaises(SystemExit):
            validate_tag_pattern("release-{version}", "2.4.1", "v2.4.1")

    def test_rejects_pattern_without_exactly_one_placeholder(self) -> None:
        for pattern in ("release-2.4.1", "release-{version}-{version}", ""):
            with self.subTest(pattern=pattern):
                with self.assertRaises(SystemExit):
                    validate_tag_pattern(pattern, "2.4.1", "release-2.4.1")

    def test_rejects_unsafe_git_ref_names(self) -> None:
        for tag in (
            "",
            "-release-2.4.1",
            "/release-2.4.1",
            "release-2.4.1/",
            "release/../2.4.1",
            "release//2.4.1",
            ".hidden/2.4.1",
            "release/2.4.1.lock",
            "release-2.4.1 ",
            "release-2:4:1",
        ):
            with self.subTest(tag=tag):
                self.assertFalse(is_safe_git_tag_name(tag))


if __name__ == "__main__":
    unittest.main()
