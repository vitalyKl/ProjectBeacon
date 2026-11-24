#!/usr/bin/env python3
"""Fail-closed validation for Beacon-prepared release commits.

The workflow is tag-triggered. This script verifies that the tagged commit was
created by ReleasePrepare (commit trailer), that its manifest describes the
same tag/version, and that every configured version source actually projects
that version. It never writes repository files.
"""
from __future__ import annotations

import datetime as dt
import json
import os
import re
import subprocess
import sys
import tomllib
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath
from typing import Any


ROOT = Path.cwd()
MANIFEST = ROOT / ".beacon" / "release.json"
VERSION_TOKEN = "{version}"
MAX_MANIFEST_BYTES = 256 * 1024
MAX_SOURCE_BYTES = 2 * 1024 * 1024


def fail(message: str) -> "NoReturn":
    print(f"::error::{message}", file=sys.stderr)
    raise SystemExit(1)


def git(*args: str) -> str:
    completed = subprocess.run(
        ["git", *args],
        cwd=ROOT,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if completed.returncode != 0:
        fail(f"git {' '.join(args[:2])} failed: {completed.stderr.strip()[:800]}")
    return completed.stdout.strip()


def safe_relative_path(raw: Any) -> Path:
    if not isinstance(raw, str) or not raw or len(raw) > 500:
        fail("release manifest contains an invalid source path")
    if "\\" in raw or raw.startswith("/") or ":" in raw or any(ord(ch) < 32 for ch in raw):
        fail(f"unsafe version source path: {raw!r}")
    raw_parts = raw.split("/")
    if any(part in ("", ".", "..") for part in raw_parts):
        fail(f"version source path is not canonical or escapes the repository: {raw!r}")
    posix = PurePosixPath(raw)
    if posix.is_absolute() or any(part in ("", ".", "..") for part in posix.parts):
        fail(f"version source path escapes the repository: {raw!r}")
    path = ROOT.joinpath(*posix.parts)
    # Refuse symlinks in any path component. A manifest must not read outside
    # the checked-out commit through a symlink.
    current = ROOT
    for part in posix.parts:
        current = current / part
        if current.is_symlink():
            fail(f"version source path traverses a symbolic link: {raw!r}")
    resolved = path.resolve(strict=True)
    if ROOT.resolve() not in resolved.parents:
        fail(f"version source path escapes the repository: {raw!r}")
    if not resolved.is_file() or resolved.stat().st_size > MAX_SOURCE_BYTES:
        fail(f"version source is missing, not a file, or too large: {raw!r}")
    return resolved


def read_pointer(document: Any, pointer: str) -> Any:
    if not isinstance(pointer, str) or not pointer.startswith("/"):
        fail("JSON version selector must be a JSON Pointer beginning with '/'")
    current = document
    for raw_part in pointer.split("/")[1:]:
        part = raw_part.replace("~1", "/").replace("~0", "~")
        if isinstance(current, dict):
            if part not in current:
                fail(f"JSON Pointer component {part!r} does not exist")
            current = current[part]
        elif isinstance(current, list):
            if not part.isdigit() or (len(part) > 1 and part.startswith("0")):
                fail(f"JSON Pointer array index {part!r} is invalid")
            index = int(part)
            if index >= len(current):
                fail(f"JSON Pointer array index {index} is out of range")
            current = current[index]
        else:
            fail(f"JSON Pointer cannot traverse value at component {part!r}")
    return current


def read_xml_version(text: str, selector: str) -> str:
    try:
        root = ET.fromstring(text)
    except ET.ParseError as exc:
        fail(f"XML version source is invalid: {exc}")
    if not isinstance(selector, str) or not selector.startswith("/"):
        fail("XML version selector must be an absolute element path")
    parts = [part for part in selector.split("/") if part]
    if parts and parts[0] == root.tag:
        parts = parts[1:]
    current = root
    for part in parts:
        # ElementTree supports simple child paths. Unsupported XPath syntax is
        # intentionally rejected here instead of being interpreted differently
        # by the build gate and Beacon.
        if any(token in part for token in ("[", "]", "@", "(", ")", "|", "*")):
            fail(f"XML selector uses XPath syntax not supported by the release CI gate: {selector!r}")
        current = current.find(part)
        if current is None:
            fail(f"XML version selector did not match an element: {selector!r}")
    value = (current.text or "").strip()
    if not value:
        fail("XML version element is empty")
    return value


def read_toml_version(document: Any, selector: str) -> str:
    if not isinstance(selector, str) or not selector:
        fail("TOML selector is empty")
    current = document
    for part in selector.split("."):
        if not part or not isinstance(current, dict) or part not in current:
            fail(f"TOML key path {selector!r} does not exist")
        current = current[part]
    return str(current).strip()


def read_regex_version(text: str, selector: str) -> str:
    try:
        match = re.search(selector, text)
    except re.error as exc:
        fail(f"Regex version selector is invalid: {exc}")
    if match is None:
        fail("Regex version selector did not match")
    if "version" in match.groupdict():
        value = match.group("version")
    elif match.lastindex:
        value = match.group(1)
    else:
        fail("Regex selector must provide a named 'version' group or capture group 1")
    if not value:
        fail("Regex version capture is empty")
    return value.strip()


def extract_source(source: dict[str, Any]) -> str:
    path = safe_relative_path(source.get("path"))
    fmt = source.get("format")
    selector = source.get("selector")
    if not isinstance(selector, str) or len(selector) > 500:
        fail(f"version source {source.get('path')!r} has an invalid selector")
    try:
        text = path.read_text(encoding="utf-8")
    except (UnicodeError, OSError) as exc:
        fail(f"could not read version source {source.get('path')!r}: {exc}")

    if fmt == "Xml":
        return read_xml_version(text, selector)
    if fmt == "Json":
        try:
            document = json.loads(text)
        except json.JSONDecodeError as exc:
            fail(f"JSON version source {source.get('path')!r} is invalid: {exc}")
        value = read_pointer(document, selector)
        return str(value).strip()
    if fmt == "Toml":
        try:
            document = tomllib.loads(text)
        except tomllib.TOMLDecodeError as exc:
            fail(f"TOML version source {source.get('path')!r} is invalid: {exc}")
        return read_toml_version(document, selector)
    if fmt == "Regex":
        return read_regex_version(text, selector)
    fail(f"unsupported version source format: {fmt!r}")


def is_safe_git_tag_name(value: Any) -> bool:
    if not isinstance(value, str) or not value or len(value) > 200:
        return False
    if value.startswith("-") or value.startswith("/") or value.endswith("/") or ".." in value or "@{" in value:
        return False
    if not all(ch.isascii() and (ch.isalnum() or ch in "._-/") for ch in value):
        return False
    for segment in value.split("/"):
        if not segment or segment.startswith(".") or segment.endswith(".") or segment.lower().endswith(".lock"):
            return False
    return True


def validate_tag_pattern(pattern: Any, version: str, tag: str) -> str:
    if not isinstance(pattern, str) or pattern.count(VERSION_TOKEN) != 1:
        fail("release manifest tagPattern must contain exactly one {version} placeholder")
    sample = pattern.replace(VERSION_TOKEN, version)
    if not is_safe_git_tag_name(sample):
        fail(f"release manifest tagPattern produces an unsafe Git tag name: {sample!r}")
    if tag != sample or not is_safe_git_tag_name(tag):
        fail(f"release tag mismatch: manifest expects {sample!r}, workflow tag is {tag!r}")
    return sample


def validate_version(policy: dict[str, Any], version: str) -> None:
    scheme = policy.get("scheme")
    if scheme == "NumericComponents":
        count = policy.get("componentCount")
        if not isinstance(count, int) or count < 1 or count > 8:
            fail("NumericComponents policy has an invalid componentCount")
        if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)*", version) or len(version.split(".")) != count:
            fail(f"version {version!r} does not contain exactly {count} numeric components")
        return
    if scheme == "SemVer":
        pattern = r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
        match = re.fullmatch(pattern, version)
        if match is None:
            fail(f"version {version!r} is not valid SemVer 2.0.0")
        if match.group(4) and policy.get("allowPrerelease") is not True:
            fail("SemVer prerelease is not allowed by the release policy")
        return
    if scheme == "Calendar":
        value = policy.get("calendarFormat")
        if not isinstance(value, str) or not value:
            fail("Calendar policy has no calendarFormat")
        # Deliberately support only the unambiguous .NET custom date tokens used
        # by project policies. Unsupported token strings fail closed.
        mapping = {
            "yyyy": "%Y", "yy": "%y", "MM": "%m", "dd": "%d",
            "HH": "%H", "mm": "%M", "ss": "%S",
        }
        python_format = value
        for token, replacement in mapping.items():
            python_format = python_format.replace(token, replacement)
        if re.search(r"[yMdHms]", python_format):
            fail(f"calendarFormat {value!r} uses tokens not supported by the release CI gate")
        try:
            parsed = dt.datetime.strptime(version, python_format)
        except ValueError as exc:
            fail(f"calendar version {version!r} does not match {value!r}: {exc}")
        if parsed.strftime(python_format) != version:
            fail(f"calendar version {version!r} is not canonical for {value!r}")
        return
    fail(f"unsupported version scheme in release manifest: {scheme!r}")


def main() -> None:
    tag = os.environ.get("GITHUB_REF_NAME", "")
    if not is_safe_git_tag_name(tag):
        fail("release workflow must be triggered by a safe, non-empty Git tag")
    if not MANIFEST.is_file() or MANIFEST.is_symlink():
        fail("tagged commit does not contain a regular .beacon/release.json file")
    if MANIFEST.stat().st_size > MAX_MANIFEST_BYTES:
        fail(".beacon/release.json exceeds the size limit")

    try:
        manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        fail(f".beacon/release.json is invalid: {exc}")
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 1:
        fail("release manifest must be a JSON object with schemaVersion=1")

    try:
        project_id = str(uuid.UUID(str(manifest.get("projectId"))))
        release_id = str(uuid.UUID(str(manifest.get("releaseId"))))
        candidate_id = str(uuid.UUID(str(manifest.get("candidateId"))))
    except (ValueError, AttributeError):
        fail("release manifest must contain valid projectId, releaseId, and candidateId GUIDs")

    version = manifest.get("version")
    tag_pattern = manifest.get("tagPattern")
    tag_name = manifest.get("tagName")
    policy = manifest.get("policy")
    if not isinstance(version, str) or not version or len(version) > 100 or any(ch.isspace() for ch in version):
        fail("release manifest version must be a non-empty version string of at most 100 characters")
    expected_tag = validate_tag_pattern(tag_pattern, version, tag)
    if tag_name != expected_tag:
        fail(f"release manifest tagName {tag_name!r} differs from the configured tag pattern {expected_tag!r}")
    if not isinstance(policy, dict):
        fail("release manifest policy must be an object")
    if manifest.get("versionPolicyRevision", 0) < 1:
        fail("release manifest versionPolicyRevision must be positive")
    if not isinstance(manifest.get("targetBranch"), str) or not manifest["targetBranch"]:
        fail("release manifest targetBranch is required")

    validate_version(policy, version)
    sources = manifest.get("sources")
    if not isinstance(sources, list) or not sources or len(sources) > 16:
        fail("release manifest must contain 1-16 configured version sources")
    paths: set[str] = set()
    for source in sources:
        if not isinstance(source, dict):
            fail("release manifest source entries must be objects")
        raw_path = source.get("path")
        if not isinstance(raw_path, str):
            fail("version source path must be a string")
        if raw_path in paths:
            fail(f"duplicate version source path: {raw_path!r}")
        paths.add(raw_path)
        projected = extract_source(source)
        if projected != version:
            fail(f"version source {raw_path!r} projects {projected!r}, expected {version!r}")

    # The release preparation commit can be an ancestor of the tagged commit
    # when GitHub merges its PR with a merge/squash commit. Locate the commit
    # that introduced .beacon/release.json and verify its trailer and path scope.
    release_commit = git("log", "-1", "--format=%H", "HEAD", "--", ".beacon/release.json")
    if not re.fullmatch(r"[0-9a-f]{40,64}", release_commit):
        fail("could not locate the Beacon ReleasePrepare commit")
    parents = git("rev-list", "--parents", "-n", "1", release_commit).split()
    if len(parents) != 2:
        fail("ReleasePrepare commit must have exactly one parent")
    ancestry = subprocess.run(
        ["git", "merge-base", "--is-ancestor", release_commit, "HEAD"],
        cwd=ROOT, check=False,
    )
    if ancestry.returncode != 0:
        fail("ReleasePrepare commit is not an ancestor of the tagged commit")
    changed = set(filter(None, git(
        "diff-tree", "--no-commit-id", "--name-only", "-r", release_commit
    ).splitlines()))
    allowed = paths | {".beacon/release.json"}
    unexpected = sorted(changed - allowed)
    if unexpected:
        fail("release preparation commit changes paths outside its version projections and release manifest: "
             + ", ".join(unexpected[:20]))
    if ".beacon/release.json" not in changed:
        fail("ReleasePrepare commit does not modify .beacon/release.json")
    trailers = git("log", "-1", "--format=%(trailers:key=Beacon-Release,valueonly)", release_commit).splitlines()
    if len(trailers) != 1 or trailers[0].strip() != release_id:
        fail("ReleasePrepare commit must contain exactly one Beacon-Release trailer matching releaseId")

    output_path = os.environ.get("GITHUB_OUTPUT")
    if output_path:
        with open(output_path, "a", encoding="utf-8") as output:
            output.write(f"version={version}\n")
            output.write(f"tag_name={tag}\n")

    print(
        f"Release manifest verified: project={project_id} release={release_id} "
        f"candidate={candidate_id} version={version} tag={tag} sources={len(sources)}"
    )


if __name__ == "__main__":
    main()
