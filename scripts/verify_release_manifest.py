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
REGEX_TIMEOUT_SECONDS = 2.0

# Run Python's backtracking regex engine in a child process. A Python thread
# timeout cannot stop catastrophic backtracking, whereas subprocess.run does.
REGEX_WORKER = r"""
import json
import re
import sys

payload = json.load(sys.stdin)
try:
    match = re.search(payload["pattern"], payload["text"])
except re.error as exc:
    print(json.dumps({"error": f"invalid regex: {exc}"}))
    raise SystemExit(0)
if match is None:
    print(json.dumps({"error": "Regex version selector did not match"}))
elif "version" in match.groupdict():
    print(json.dumps({"value": match.group("version")}))
elif match.lastindex:
    print(json.dumps({"value": match.group(1)}))
else:
    print(json.dumps({"error": "Regex selector must provide a named 'version' group or capture group 1"}))
"""

# Keep this grammar in sync with ProjectVersionPolicyCatalog. The release gate
# accepts a portable XPath location-path subset evaluated identically by .NET
# XPathSelectElement and Python ElementTree.
XML_LOCATION_PATH = re.compile(
    r"^(?:/[A-Za-z_][A-Za-z0-9_.-]*(?:/[A-Za-z_][A-Za-z0-9_.-]*)*"
    r"|//[A-Za-z_][A-Za-z0-9_.-]*(?:/[A-Za-z_][A-Za-z0-9_.-]*)*)$"
)


def fail(message: str) -> "NoReturn":
    # Keep the GitHub workflow annotation for real release-gate failures, but
    # don't emit workflow commands when this module is imported by unit tests.
    if __name__ == "__main__":
        print(f"::error::{message}", file=sys.stderr)
    else:
        print(message, file=sys.stderr)
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


def safe_manifest_path(root: Path) -> Path:
    """Resolve the release manifest without traversing a repository-controlled symlink."""
    try:
        resolved_root = root.resolve(strict=True)
    except OSError as exc:
        fail(f"release workflow checkout root is unavailable: {exc}")

    beacon_dir = resolved_root / ".beacon"
    if beacon_dir.is_symlink() or not beacon_dir.is_dir():
        fail("tagged commit does not contain a regular .beacon directory")

    try:
        resolved_beacon_dir = beacon_dir.resolve(strict=True)
    except OSError as exc:
        fail(f"release manifest directory is unavailable: {exc}")
    if resolved_beacon_dir.parent != resolved_root:
        fail(".beacon directory resolves outside the checked-out repository")

    manifest = beacon_dir / "release.json"
    if manifest.is_symlink() or not manifest.is_file():
        fail("tagged commit does not contain a regular .beacon/release.json file")

    try:
        resolved_manifest = manifest.resolve(strict=True)
    except OSError as exc:
        fail(f"release manifest file is unavailable: {exc}")
    if resolved_manifest.parent != resolved_beacon_dir:
        fail(".beacon/release.json resolves outside the regular .beacon directory")
    return resolved_manifest


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


def unescape_json_pointer_part(raw_part: str) -> str:
    decoded: list[str] = []
    index = 0
    while index < len(raw_part):
        character = raw_part[index]
        if character != "~":
            decoded.append(character)
            index += 1
            continue
        if index + 1 >= len(raw_part) or raw_part[index + 1] not in ("0", "1"):
            fail(f"JSON Pointer component {raw_part!r} contains an invalid escape sequence")
        decoded.append("~" if raw_part[index + 1] == "0" else "/")
        index += 2
    return "".join(decoded)


def read_pointer(document: Any, pointer: str) -> Any:
    # RFC 6901 defines the empty string as the pointer to the entire document.
    if not isinstance(pointer, str) or (pointer and not pointer.startswith("/")):
        fail("JSON version selector must be an RFC 6901 JSON Pointer (empty string selects the document root)")
    current = document
    if pointer == "":
        return current

    for raw_part in pointer[1:].split("/"):
        part = unescape_json_pointer_part(raw_part)
        if isinstance(current, dict):
            if part not in current:
                fail(f"JSON Pointer component {part!r} does not exist")
            current = current[part]
        elif isinstance(current, list):
            # Array indexes are ASCII decimal, zero or a non-zero digit followed by
            # ASCII digits. Python str.isdigit() is deliberately not used because it
            # accepts Unicode numerals that .NET's RFC 6901 reader correctly rejects.
            if not part or not all("0" <= character <= "9" for character in part) \
                    or (len(part) > 1 and part.startswith("0")):
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
    if not isinstance(selector, str) or not XML_LOCATION_PATH.fullmatch(selector):
        fail(
            "XML selector must be an absolute or descendant element path "
            "(for example '/Project/PropertyGroup/Version' or '//Version'); "
            "predicates, attributes, axes, functions, wildcards, and namespace prefixes are not supported"
        )

    if selector.startswith("//"):
        parts = selector[2:].split("/")
        matches = [element for element in root.iter() if element.tag == parts[0]]
        for part in parts[1:]:
            matches = [child for parent in matches for child in parent.findall(part)]
            if not matches:
                break
        current = matches[0] if matches else None
    else:
        parts = selector[1:].split("/")
        if parts and parts[0] == root.tag:
            parts = parts[1:]
        current = root
        for part in parts:
            current = current.find(part) if current is not None else None
            if current is None:
                break

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
    # Match the supported scalar contract of the .NET Tomlyn reader. Complex
    # TOML values, booleans and date/time values are not version projections.
    if isinstance(current, bool) or not isinstance(current, (str, int)):
        fail("TOML version selector must resolve to a string or integer scalar")
    return str(current).strip()


def validate_portable_dotnet_regex(selector: str) -> tuple[bool, str | None]:
    if not isinstance(selector, str) or not selector:
        return False, "Regex selector is empty"

    capture_groups = 0
    has_named_version_group = False
    has_named_version_backreference = False
    in_character_class = False
    index = 0

    while index < len(selector):
        character = selector[index]
        if character == "\\":
            index += 1
            if index >= len(selector):
                return False, "A trailing escape is not supported"
            escaped = selector[index]
            if escaped == "k":
                marker = "k<version>"
                if in_character_class or not selector.startswith(marker, index):
                    return False, r"Only the .NET named backreference \k<version> is supported, and only outside a character class"
                has_named_version_backreference = True
                index += len(marker)
                continue

            portable_escape = escaped in {"d", "D", "s", "S", "t", "r", "n", "f", "v", "\\", "]", "-", "^"}
            if not in_character_class:
                portable_escape = portable_escape or escaped in {
                    "A", "z", ".", "$", "|", "?", "*", "+", "(", ")", "[", "{", "}"
                }
            if not portable_escape:
                return False, r"Use only portable escapes (\d, \D, \s, \S, \t, \r, \n, \f, \v, \A, \z, escaped regex punctuation, or \k<version>)"
            index += 1
            continue

        if in_character_class:
            if character == "[":
                return False, "Nested and subtractive character classes are not supported"
            if character == "]":
                in_character_class = False
            index += 1
            continue

        if character == "[":
            in_character_class = True
            index += 1
            continue

        if character != "(":
            index += 1
            continue

        if selector.startswith("(?<version>", index):
            capture_groups += 1
            has_named_version_group = True
            index += len("(?<version>")
            continue

        if any(selector.startswith(extension, index) for extension in ("(?:", "(?=", "(?!")):
            index += 3
            continue

        if selector.startswith("(?", index):
            return False, "Only (?<version>...), (?:...), (?=...), and (?!...) group extensions are supported"
        capture_groups += 1
        index += 1

    if in_character_class:
        return False, "Character class is not closed"
    if capture_groups != 1:
        return False, "A portable version selector must contain exactly one capturing group: (?<version>...) or one ordinary group (...)"
    if has_named_version_backreference and not has_named_version_group:
        return False, r"\k<version> requires a named (?<version>...) capture group"

    try:
        re.compile(dotnet_regex_to_python(selector))
    except re.error as exc:
        return False, f"invalid portable regular expression: {exc}"
    return True, None


def dotnet_regex_to_python(selector: str) -> str:
    # Translate only the two .NET constructs intentionally supported by the
    # common grammar; do not rewrite escaped text or content inside a class.
    output: list[str] = []
    in_character_class = False
    index = 0
    while index < len(selector):
        character = selector[index]
        if character == "\\" and index + 1 < len(selector):
            escaped = selector[index + 1]
            if not in_character_class and escaped == "k" and selector.startswith("k<version>", index + 1):
                output.append("(?P=version)")
                index += len("\\k<version>")
                continue
            if not in_character_class and escaped == "z":
                output.append(r"\Z")
            else:
                output.append("\\" + escaped)
            index += 2
            continue
        if not in_character_class and selector.startswith("(?<version>", index):
            output.append("(?P<version>")
            index += len("(?<version>")
            continue
        if character == "[" and not in_character_class:
            in_character_class = True
        elif character == "]" and in_character_class:
            in_character_class = False
        output.append(character)
        index += 1
    return "".join(output)


def read_regex_version(text: str, selector: str) -> str:
    portable, compatibility_error = validate_portable_dotnet_regex(selector)
    if not portable:
        fail("Regex selector is not portable to the release workflow: " + (compatibility_error or "unsupported syntax"))
    pattern = dotnet_regex_to_python(selector)
    try:
        completed = subprocess.run(
            [sys.executable, "-I", "-c", REGEX_WORKER],
            input=json.dumps({"pattern": pattern, "text": text}),
            text=True,
            encoding="utf-8",
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
            timeout=REGEX_TIMEOUT_SECONDS,
        )
    except subprocess.TimeoutExpired:
        fail(f"Regex version selector exceeded the {REGEX_TIMEOUT_SECONDS:g}s execution limit")
    if completed.returncode != 0:
        fail(
            "Regex version selector worker failed: "
            + completed.stderr.strip()[:800]
        )
    try:
        result = json.loads(completed.stdout)
    except json.JSONDecodeError:
        fail("Regex version selector worker returned invalid JSON")
    if not isinstance(result, dict):
        fail("Regex version selector worker returned an invalid result")
    if "error" in result:
        fail(str(result["error"]))
    value = result.get("value")
    if not isinstance(value, str) or not value.strip():
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


def require_positive_json_integer(value: Any, label: str) -> int:
    # bool is a subclass of int in Python; JSON schema revisions must be actual
    # integer tokens, not true/false or floating-point values such as 1.0.
    if type(value) is not int or value < 1:
        fail(f"{label} must be a positive JSON integer")
    return value


def validate_version(policy: dict[str, Any], version: str) -> None:
    if type(policy.get("allowPrerelease")) is not bool:
        fail("release manifest policy allowPrerelease must be a JSON Boolean")
    scheme = policy.get("scheme")
    component_count = policy.get("componentCount")
    if type(component_count) is not int:
        fail("release manifest policy componentCount must be a JSON integer")
    if scheme == "NumericComponents":
        if component_count < 1 or component_count > 8:
            fail("NumericComponents policy has an invalid componentCount")
        if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)*", version) or len(version.split(".")) != component_count:
            fail(f"version {version!r} does not contain exactly {component_count} numeric components")
        return
    if scheme == "SemVer":
        if component_count != 3:
            fail("SemVer policy componentCount must be exactly 3")
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


def validate_release_snapshot(
    snapshot_json: Any,
    project_id: str,
    release_id: str,
    version: str,
    target_branch: str,
    version_policy_revision: int,
    release_revision: int,
    policy: dict[str, Any],
) -> None:
    if not isinstance(snapshot_json, str) or not snapshot_json or len(snapshot_json) > 1_000_000:
        fail("release manifest snapshotJson must be a non-empty JSON object string within the 1 MB limit")
    try:
        snapshot = json.loads(snapshot_json)
    except json.JSONDecodeError as exc:
        fail(f"release manifest snapshotJson is invalid JSON: {exc}")
    if not isinstance(snapshot, dict) or type(snapshot.get("schemaVersion")) is not int or snapshot["schemaVersion"] != 1:
        fail("release manifest snapshotJson must contain an object with schemaVersion=1")

    try:
        snapshot_project_id = str(uuid.UUID(str(snapshot.get("projectId"))))
    except (ValueError, AttributeError):
        fail("release snapshot projectId must be a valid GUID")
    if snapshot_project_id != project_id:
        fail("release snapshot projectId differs from the release manifest")

    snapshot_policy_revision = require_positive_json_integer(
        snapshot.get("versionPolicyRevision"), "release snapshot versionPolicyRevision"
    )
    if snapshot_policy_revision != version_policy_revision:
        fail("release snapshot versionPolicyRevision differs from the release manifest")

    snapshot_policy = snapshot.get("versionPolicy")
    if not isinstance(snapshot_policy, dict):
        fail("release snapshot must contain a versionPolicy object")
    for key in ("scheme", "componentCount", "calendarFormat", "tagPattern", "allowPrerelease"):
        if snapshot_policy.get(key) != policy.get(key) or type(snapshot_policy.get(key)) is not type(policy.get(key)):
            fail(f"release snapshot versionPolicy.{key} differs from the release manifest policy")

    release = snapshot.get("release")
    if not isinstance(release, dict):
        fail("release snapshot must contain a release object")
    try:
        snapshot_release_id = str(uuid.UUID(str(release.get("releaseId"))))
    except (ValueError, AttributeError):
        fail("release snapshot releaseId must be a valid GUID")
    if snapshot_release_id != release_id:
        fail("release snapshot releaseId differs from the release manifest")
    if release.get("targetVersion") != version or release.get("targetBranch") != target_branch:
        fail("release snapshot target version or branch differs from the release manifest")
    snapshot_release_revision = require_positive_json_integer(
        release.get("releaseRevision"), "release snapshot releaseRevision"
    )
    if snapshot_release_revision != release_revision:
        fail("release snapshot releaseRevision differs from the release manifest")


def main() -> None:
    tag = os.environ.get("GITHUB_REF_NAME", "")
    if not is_safe_git_tag_name(tag):
        fail("release workflow must be triggered by a safe, non-empty Git tag")
    manifest_path = safe_manifest_path(ROOT)
    if manifest_path.stat().st_size > MAX_MANIFEST_BYTES:
        fail(".beacon/release.json exceeds the size limit")

    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        fail(f".beacon/release.json is invalid: {exc}")
    if not isinstance(manifest, dict) or type(manifest.get("schemaVersion")) is not int or manifest["schemaVersion"] != 1:
        fail("release manifest must be a JSON object with integer schemaVersion=1")

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
    version_policy_revision = require_positive_json_integer(
        manifest.get("versionPolicyRevision"), "release manifest versionPolicyRevision"
    )
    release_revision = require_positive_json_integer(
        manifest.get("releaseRevision"), "release manifest releaseRevision"
    )
    target_branch = manifest.get("targetBranch")
    if not isinstance(target_branch, str) or not target_branch:
        fail("release manifest targetBranch is required")

    validate_version(policy, version)
    validate_release_snapshot(
        manifest.get("snapshotJson"),
        project_id,
        release_id,
        version,
        target_branch,
        version_policy_revision,
        release_revision,
        policy,
    )
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

    # The original ReleasePrepare commit must remain in the tagged commit ancestry.
    # Release PRs therefore require the merge-commit method; squash and rebase rewrite
    # the ReleasePrepare commit and cannot prove its Beacon-Release trailer.
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
