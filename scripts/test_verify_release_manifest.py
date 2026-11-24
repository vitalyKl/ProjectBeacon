#!/usr/bin/env python3
"""Unit tests for release manifest tag-pattern validation."""
from __future__ import annotations

import unittest

from verify_release_manifest import is_safe_git_tag_name, validate_tag_pattern


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
