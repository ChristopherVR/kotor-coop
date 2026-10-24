"""Publication boundary regressions. No game install, build or network required.

Run: python -m unittest discover -s tools/publication -p test_package_policy.py -v
"""
import hashlib
import io
import stat
import tempfile
import unittest
import warnings
import zipfile
from pathlib import Path
from unittest.mock import patch

import package_policy as policy


class PackagePolicyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="kotor-publication-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.version = "1.2.3-beta.1"
        self.name = f"kotor-coop-{self.version}-windows.zip"
        (self.root / "VERSION").write_bytes((self.version + "\r\n").encode())
        self.files = {}
        for name, source in {**policy.SOURCE_FILES, **policy.BINARY_FILES}.items():
            target = self.root / source
            target.parent.mkdir(parents=True, exist_ok=True)
            data = (b"MZfixture-build:" if name in policy.BINARY_FILES else b"source:") + source.encode()
            target.write_bytes(data)
            self.files[name] = data
        # Shared source paths (coop.ini/defaults) must produce identical bytes.
        for name, source in policy.SOURCE_FILES.items():
            self.files[name] = (self.root / source).read_bytes()
        template = b"kotor-coop {{VERSION}}\r\nRead the release notes.\r\n"
        (self.root / "tools/package-README.txt").write_bytes(template)
        self.files["README.txt"] = template.replace(b"{{VERSION}}", self.version.encode())

    def archive(self, replacements=None, extra=(), omit=()):
        contents = dict(self.files)
        contents.update(replacements or {})
        out = io.BytesIO()
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
                for name, data in contents.items():
                    if name not in omit:
                        z.writestr(name, data)
                for name, data in extra:
                    z.writestr(name, data)
        return out.getvalue()

    def inspect(self, payload=None, **kwargs):
        return policy.inspect_package_bytes(
            self.archive() if payload is None else payload,
            kwargs.pop("archive_name", self.name), kwargs.pop("version", self.version),
            root=self.root, **kwargs)

    def test_exact_package_inventory_and_extraction(self):
        payload = self.archive()
        target = self.root / "extracted"
        result = self.inspect(payload, extract=target)
        self.assertEqual(result["sha256"], hashlib.sha256(payload).hexdigest())
        self.assertEqual(result["bytes"], len(payload))
        self.assertEqual({f["name"] for f in result["files"]}, policy.EXPECTED)
        for name, data in self.files.items():
            self.assertEqual((target / name).read_bytes(), data)

    def test_archive_replacement_cannot_change_validated_or_extracted_bytes(self):
        archive = self.root / self.name
        original = self.archive()
        archive.write_bytes(original)
        original_inspector = policy.inspect_package_bytes

        def replace_disk_archive(payload, *args, **kwargs):
            archive.write_bytes(self.archive(extra=[("private-recording.wav", b"private")]))
            return original_inspector(payload, *args, **kwargs)

        destination = self.root / "immutable-extract"
        with patch.object(policy, "inspect_package_bytes", side_effect=replace_disk_archive):
            result = policy.inspect_package(archive, self.version, self.root, destination)
        self.assertEqual(result["sha256"], hashlib.sha256(original).hexdigest())
        self.assertFalse((destination / "private-recording.wav").exists())
        self.assertEqual((destination / "README.txt").read_bytes(), self.files["README.txt"])

    def test_rejects_unexpected_and_traversal_paths_before_extraction(self):
        for name in ("private.log", "../outside.txt", "/absolute.txt", "C:/outside.txt", "Override/../../outside.txt"):
            with self.subTest(name=name):
                target = self.root / "not-created"
                with self.assertRaisesRegex(ValueError, "allowlist"):
                    self.inspect(self.archive(extra=[(name, b"private")]), extract=target)
                self.assertFalse(target.exists())

    def test_rejects_duplicate_and_case_colliding_paths(self):
        for name in ("README.txt", "readme.TXT"):
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "Duplicate"):
                self.inspect(self.archive(extra=[(name, b"duplicate")]))

    def test_rejects_symlink_even_with_allowlisted_name(self):
        symlink = zipfile.ZipInfo("README.txt")
        symlink.create_system = 3
        symlink.external_attr = (stat.S_IFLNK | 0o777) << 16
        with self.assertRaisesRegex(ValueError, "symbolic links"):
            self.inspect(self.archive(omit=["README.txt"], extra=[(symlink, b"../secret")]))

    def test_rejects_missing_files(self):
        with self.assertRaisesRegex(ValueError, "allowlist"):
            self.inspect(self.archive(omit=["KotorCoop.LICENSE.txt"]))

    def test_rejects_stale_asset(self):
        with self.assertRaisesRegex(ValueError, "Stale.*Override/k_coop_chungus.mdl"):
            self.inspect(self.archive({"Override/k_coop_chungus.mdl": b"old-character"}))

    def test_rejects_stale_binaries_despite_valid_mz_prefix(self):
        for name in policy.BINARY_FILES:
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "Stale"):
                self.inspect(self.archive({name: b"MZold-build"}))

    def test_rejects_stale_or_arbitrary_readme(self):
        for data in (b"private local notes", self.files["README.txt"].replace(self.version.encode(), b"0.0.1")):
            with self.subTest(data=data), self.assertRaisesRegex(ValueError, "Stale.*README.txt"):
                self.inspect(self.archive({"README.txt": data}))

    def test_rejects_version_not_declared_by_checkout(self):
        with self.assertRaisesRegex(ValueError, "match VERSION"):
            self.inspect(version="9.9.9", archive_name="kotor-coop-9.9.9-windows.zip")

    def test_rejects_path_like_version_and_incorrect_archive_name(self):
        for version in ("../1.2.3", "1.2.3/evil", "1.2.3;echo injected"):
            with self.subTest(version=version), self.assertRaisesRegex(ValueError, "Unexpected"):
                self.inspect(version=version)
        with self.assertRaisesRegex(ValueError, "Unexpected"):
            self.inspect(archive_name="different.zip")

    def test_refuses_existing_extraction_directory(self):
        target = self.root / "existing"
        target.mkdir()
        marker = target / "keep.txt"
        marker.write_bytes(b"keep")
        with self.assertRaises(FileExistsError):
            self.inspect(extract=target)
        self.assertEqual(marker.read_bytes(), b"keep")

    def test_missing_build_output_fails_closed(self):
        (self.root / policy.BINARY_FILES["dinput8.dll"]).unlink()
        with self.assertRaises(FileNotFoundError):
            self.inspect()

    def test_requires_immutable_bytes(self):
        with self.assertRaisesRegex(TypeError, "immutable"):
            self.inspect(bytearray(self.archive()))


if __name__ == "__main__":
    unittest.main()
