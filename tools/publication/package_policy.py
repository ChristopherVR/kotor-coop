"""Strict boundary between private development and public Windows downloads.

Only the enumerated runtime files may leave this repository. No git history,
recordings, source, symbols or diagnostic logs are publication inputs.
"""
import hashlib
import io
import json
import re
import stat
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
VERSION = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?")
FIXED = {
    "dinput8.dll", "chain-loader/dinput8.dll", "KotorCoop.Agent.exe",
    "install.ps1", "KotorCoop.CollectLogs.cmd", "README.txt", "coop.ini",
    "dinput8.chain.example", "KotorCoop.DEFAULTS.ini", "KotorCoop.LEGAL.txt",
    "KotorCoop.LICENSE.txt", "KotorCoop.THIRD-PARTY-INVENTORY.md",
    "KotorCoop.THIRD-PARTY-NOTICES.txt",
}
OVERRIDES = {
    "k_coop_chline.wav", "k_coop_chsnap.wav", "k_coop_chungus.mdl",
    "k_coop_chungus.mdx", "k_coop_chungus.tga", "k_coop_chungusg.tga",
    "k_coop_chungusg.txi", "k_coop_jediclass.dlg", "k_coop_phantom.utc",
    "k_coop_spawn.utw",
}
MODS = {"brotherhood-of-shadow-solomons-revenge.json", "jedi-from-the-start.json",
        "super-skip-taris.json"}
EXPECTED = FIXED | {"Override/" + p for p in OVERRIDES} | {"coop-mods/" + p for p in MODS}
SOURCE_FILES = {
    "coop.ini": "profiles/coop.ini.example",
    "KotorCoop.DEFAULTS.ini": "profiles/coop.ini.example",
    "KotorCoop.LICENSE.txt": "LICENSE", "KotorCoop.LEGAL.txt": "docs/LEGAL.md",
    "KotorCoop.THIRD-PARTY-NOTICES.txt": "third-party/NOTICES.txt",
    "KotorCoop.THIRD-PARTY-INVENTORY.md": "THIRD-PARTY-NOTICES.md",
    "install.ps1": "tools/install.ps1",
    "KotorCoop.CollectLogs.cmd": "tools/KotorCoop.CollectLogs.cmd",
    "dinput8.chain.example": "src/KotorCoop.Loader/dinput8.chain.example",
    **{"Override/" + p: "profiles/override/" + p for p in OVERRIDES},
    **{"coop-mods/" + p: "profiles/mods/" + p for p in MODS},
}
BINARY_FILES = {
    "dinput8.dll": "src/KotorCoop.Plugin/bin/x86/Release/net10.0-windows/win-x86/publish/dinput8.dll",
    "chain-loader/dinput8.dll": "src/KotorCoop.Loader/bin/dinput8.dll",
    "KotorCoop.Agent.exe": "src/KotorCoop.Agent/bin/Release/net10.0-windows/win-x64/publish/KotorCoop.Agent.exe",
}


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def inspect_package_bytes(payload, archive_name, version, root=ROOT, extract=None):
    """Validate, hash and optionally extract one immutable snapshot of a package.

    The publication entrypoint must produce the build outputs from a fresh build
    in a clean checkout. This function binds every shipped file to those local
    outputs, but is not a substitute for trusted build or antivirus execution.
    """
    if not isinstance(payload, bytes):
        raise TypeError("Package snapshot must be immutable bytes")
    root = Path(root)
    if not VERSION.fullmatch(version) or archive_name != f"kotor-coop-{version}-windows.zip":
        raise ValueError("Unexpected version or archive filename")
    declared = (root / "VERSION").read_bytes().decode("utf-8-sig").strip()
    if version != declared:
        raise ValueError("Public release version must match VERSION")
    # Match Get-Content -Raw + replacement + Set-Content -Encoding utf8
    # -NoNewline in package.ps1, retaining the template's newline bytes.
    readme = (root / "tools/package-README.txt").read_bytes().decode("utf-8-sig")
    expected_readme = readme.replace("{{VERSION}}", version).encode("utf-8")
    with zipfile.ZipFile(io.BytesIO(payload)) as z:
        entries = z.infolist()
        names = [i.filename for i in entries]
        if len(names) != len(set(n.casefold() for n in names)):
            raise ValueError("Duplicate or case-colliding archive paths")
        if set(names) != EXPECTED:
            raise ValueError(f"Package allowlist mismatch: unexpected={sorted(set(names)-EXPECTED)}, missing={sorted(EXPECTED-set(names))}")
        inventory = []
        for entry in entries:
            if entry.flag_bits & 1 or stat.S_ISLNK(entry.external_attr >> 16):
                raise ValueError("Encrypted entries and symbolic links are forbidden")
            if entry.file_size > 200_000_000:
                raise ValueError("Oversized package entry")
            data = z.read(entry)
            source = SOURCE_FILES.get(entry.filename) or BINARY_FILES.get(entry.filename)
            if source and data != (root / source).read_bytes():
                raise ValueError(f"Stale or modified publication input: {entry.filename}")
            if entry.filename == "README.txt" and data != expected_readme:
                raise ValueError("Stale or modified publication input: README.txt")
            if entry.filename.lower().endswith((".exe", ".dll")) and not data.startswith(b"MZ"):
                raise ValueError("Invalid Windows binary: " + entry.filename)
            inventory.append({"name": entry.filename, "bytes": len(data),
                              "sha256": hashlib.sha256(data).hexdigest()})
        if extract is not None:
            destination = Path(extract)
            destination.mkdir(parents=True, exist_ok=False)
            # Same in-memory ZIP that was validated above. Never reopen the
            # caller's path, which another process could replace after validation.
            z.extractall(destination)
    return {"name": archive_name, "bytes": len(payload),
            "sha256": hashlib.sha256(payload).hexdigest(), "files": inventory}


def inspect_package(archive, version, root=ROOT, extract=None):
    archive = Path(archive)
    return inspect_package_bytes(archive.read_bytes(), archive.name, version, root, extract)


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("archive", type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--extract", type=Path)
    args = parser.parse_args()
    inventory = inspect_package(args.archive, args.version, extract=args.extract)
    args.output.write_text(json.dumps(inventory, indent=2) + "\n", encoding="utf-8")
