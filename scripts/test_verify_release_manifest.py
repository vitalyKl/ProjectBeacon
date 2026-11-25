#!/usr/bin/env python3
"""Unit tests for release manifest tag-pattern validation."""
from __future__ import annotations

import unittest

from verify_release_manifest import (
    dotnet_regex_to_python,
    is_safe_git_tag_name,
    read_regex_version,
    read_xml_version,
    validate_tag_pattern,
)


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
