"""Prepare a version-stamped release commit without writing back to main.

Run only in a clean CI checkout. The workflow serializes allocation and publication.
Tags are pushed only after the build passes; retries reuse a published source's tag.
"""

import argparse
import os
from pathlib import Path
import re
import subprocess


VERSION = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z.-]+))?")


def version_key(value):
    match = VERSION.fullmatch(value)
    if not match:
        raise ValueError(f"Invalid release version: {value}")
    major, minor, patch, prerelease = match.groups()
    parts = prerelease.split(".") if prerelease else []
    if any(not part or (part.isdigit() and len(part) > 1 and part[0] == "0") for part in parts):
        raise ValueError(f"Invalid release version: {value}")
    return (int(major), int(minor), int(patch), not bool(prerelease),
            tuple((0, int(part)) if part.isdigit() else (1, part) for part in parts))


def next_version(declared, tags):
    """Use an explicitly raised VERSION, otherwise increment the highest release."""
    version_key(declared)
    released = []
    for tag in tags:
        if tag.startswith("v"):
            try:
                version_key(tag[1:])
                released.append(tag[1:])
            except ValueError:
                pass
    if not released:
        return declared
    latest = max(released, key=version_key)
    if version_key(declared) > version_key(latest):
        return declared
    core, separator, prerelease = latest.partition("-")
    if separator:
        parts = prerelease.split(".")
        if parts[-1].isdigit():
            parts[-1] = str(int(parts[-1]) + 1)
        else:
            parts.append("1")
        return core + "-" + ".".join(parts)
    major, minor, patch = core.split(".")
    return f"{major}.{minor}.{int(patch) + 1}"


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


def prepare(root, tag=None):
    if git(root, "status", "--porcelain", "--untracked-files=no"):
        raise ValueError("Release preparation requires a clean checkout")
    source = git(root, "rev-parse", "HEAD")
    declared = (root / "VERSION").read_text(encoding="utf-8-sig").strip()
    version_key(declared)
    if tag:
        if tag != f"v{declared}" or git(root, "rev-parse", f"{tag}^{{commit}}") != source:
            raise ValueError("Tag must point to HEAD and match VERSION")
        return declared, tag

    tags = git(root, "tag", "--list", "v*").splitlines()
    for existing in tags:
        try:
            version_key(existing[1:])
        except ValueError:
            continue
        commit = git(root, "rev-parse", f"{existing}^{{commit}}")
        message = git(root, "show", "-s", "--format=%B", commit)
        if commit == source or f"Release-Source: {source}" in message.splitlines():
            # Never reuse an unrelated or edited source snapshot on a retry.
            changes = git(root, "diff", "--name-only", source, commit).splitlines()
            if any(path != "VERSION" for path in changes):
                raise ValueError("Existing release differs from its source beyond VERSION")
            version = git(root, "show", f"{commit}:VERSION")
            if existing != f"v{version}":
                raise ValueError("Existing release tag does not match VERSION")
            git(root, "checkout", "--detach", commit)
            return version, existing

    version = next_version(declared, tags)
    (root / "VERSION").write_text(version + "\n", encoding="utf-8")
    git(root, "add", "VERSION")
    git(root, "-c", "user.name=github-actions[bot]", "-c",
        "user.email=41898282+github-actions[bot]@users.noreply.github.com",
        "commit", "--allow-empty", "-m", f"Release v{version}\n\nRelease-Source: {source}")
    tag = f"v{version}"
    git(root, "tag", tag)
    return version, tag


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", help="Validate an explicitly pushed release tag")
    args = parser.parse_args()
    version, tag = prepare(Path.cwd(), args.tag)
    output = f"version={version}\ntag={tag}\nprerelease={str('-' in version).lower()}\n"
    print(output, end="")
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.write(output)
