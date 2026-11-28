#!/usr/bin/env python3
"""Fail-closed checks for the least-privilege GitHub Release workflow contract."""
from __future__ import annotations

import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
RELEASE_WORKFLOW = ROOT / ".github" / "workflows" / "release.yml"
CI_WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"


def _indent(line: str) -> int:
    return len(line) - len(line.lstrip(" "))


def _block_after(lines: list[str], index: int, parent_indent: int) -> list[str]:
    """Return one YAML mapping block, stopping at the next sibling."""
    values: list[str] = []
    for line in lines[index + 1:]:
        if line.strip() and _indent(line) <= parent_indent:
            break
        values.append(line)
    return values


def _mapping_block(lines: list[str], header: str, indent: int) -> list[str] | None:
    expected = " " * indent + header + ":"
    for index, line in enumerate(lines):
        if line.rstrip() == expected:
            return _block_after(lines, index, indent)
    return None


def _permissions(block: list[str] | None, header_indent: int) -> dict[str, str] | None:
    if block is None:
        return None
    result: dict[str, str] = {}
    entry_indent = header_indent + 2
    for line in block:
        if not line.strip() or _indent(line) != entry_indent:
            continue
        key, separator, value = line.strip().partition(":")
        if separator:
            result[key.strip()] = value.strip()
    return result


class ReleaseWorkflowPermissionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.release_lines = RELEASE_WORKFLOW.read_text(encoding="utf-8").splitlines()
        cls.ci_lines = CI_WORKFLOW.read_text(encoding="utf-8").splitlines()

    def _job_permissions(self, name: str) -> dict[str, str] | None:
        job = _mapping_block(self.release_lines, name, 2)
        if job is None:
            self.fail(f"Release workflow job is missing: {name}")
        return _permissions(_mapping_block(job, "permissions", 4), 4)

    def test_workflow_level_permissions_are_read_only(self) -> None:
        permissions = _permissions(_mapping_block(self.release_lines, "permissions", 0), 0)
        self.assertEqual({"contents": "read"}, permissions)

    def test_only_image_job_can_write_packages(self) -> None:
        jobs = ("tests", "client", "image", "scan", "deploy")
        writers = [
            name for name in jobs
            if (self._job_permissions(name) or {}).get("packages") == "write"
        ]
        self.assertEqual(["image"], writers)

    def test_only_client_and_image_jobs_receive_any_write_scope(self) -> None:
        jobs = ("tests", "client", "image", "scan", "deploy")
        write_scopes = {
            name: sorted(
                scope for scope, access in (self._job_permissions(name) or {}).items()
                if access == "write"
            )
            for name in jobs
        }
        self.assertEqual(
            {
                "tests": [],
                "client": ["contents"],
                "image": ["packages"],
                "scan": [],
                "deploy": [],
            },
            write_scopes,
        )

    def test_package_reader_and_release_asset_writer_are_scoped(self) -> None:
        self.assertEqual(
            {"contents": "write"},
            self._job_permissions("client"),
        )
        self.assertEqual(
            {"contents": "read", "packages": "write"},
            self._job_permissions("image"),
        )
        self.assertEqual(
            {"contents": "read", "packages": "read"},
            self._job_permissions("scan"),
        )
        for job in ("tests", "deploy"):
            self.assertNotEqual("write", (self._job_permissions(job) or {}).get("packages"))

    def test_required_checks_subscribe_to_merge_group(self) -> None:
        self.assertRegex(
            "\n".join(self.ci_lines),
            r"(?m)^  merge_group:\s*\n    types: \[checks_requested\]$",
        )



    def test_ci_does_not_receive_repository_secrets(self) -> None:
        # CI executes arbitrary PR and task-branch code; it must not receive repository secrets.
        self.assertNotRegex(
            "\n".join(self.ci_lines),
            r"\$\{\{\s*secrets\.",
        )

    def test_production_ssh_secrets_are_scoped_to_the_reviewable_deploy_job(self) -> None:
        jobs = ("tests", "client", "image", "scan", "deploy")
        deploy = _mapping_block(self.release_lines, "deploy", 2)
        self.assertIsNotNone(deploy)
        deploy_text = "\n".join(deploy or [])
        self.assertIn("    environment: production", deploy_text)
        self.assertIn("    needs: [tests, image, scan, client]", deploy_text)

        all_refs = [line.strip() for line in self.release_lines if "secrets.PRODUCTION_" in line]
        deploy_refs = [line.strip() for line in (deploy or []) if "secrets.PRODUCTION_" in line]
        self.assertTrue(all_refs, "Production SSH secret references are missing.")
        self.assertEqual(all_refs, deploy_refs)

        for name in jobs[:-1]:
            job = _mapping_block(self.release_lines, name, 2)
            self.assertIsNotNone(job)
            job_text = "\n".join(job or [])
            self.assertNotIn("secrets.PRODUCTION_", job_text)
            self.assertNotIn("environment: production", job_text)

if __name__ == "__main__":
    unittest.main(verbosity=2)
