import tempfile
import unittest
from pathlib import Path

from release_version import git, next_version, prepare, version_key


class VersionTests(unittest.TestCase):
    def test_increments_beta_numerically(self):
        self.assertEqual(next_version("0.1.0-beta.3", ["v0.1.0-beta.9", "v0.1.0-beta.10"]),
                         "0.1.0-beta.11")

    def test_stable_patch_and_explicit_promotion(self):
        self.assertEqual(next_version("0.1.0", ["v0.1.2"]), "0.1.3")
        self.assertEqual(next_version("0.2.0", ["v0.2.0-beta.9"]), "0.2.0")
        self.assertEqual(next_version("0.3.0-beta.1", ["v0.2.1"]), "0.3.0-beta.1")

    def test_first_release_and_non_version_tags(self):
        self.assertEqual(next_version("0.1.0-beta.3", ["latest", "v-preview", "pre-scrub-main"]),
                         "0.1.0-beta.3")
        self.assertEqual(next_version("0.1.0-beta", ["v0.1.0-beta"]), "0.1.0-beta.1")

    def test_invalid_versions_are_rejected(self):
        for value in ("01.0.0", "1.0.0-beta..1", "1.0.0-beta.01", "../bad", "1.0"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                version_key(value)


class ReleaseRepositoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        git(self.root, "init", "-b", "main")
        git(self.root, "config", "user.name", "Release test")
        git(self.root, "config", "user.email", "test@example.invalid")
        (self.root / "VERSION").write_text("0.1.0-beta.3\n")
        (self.root / "source.txt").write_text("original\n")
        git(self.root, "add", ".")
        git(self.root, "commit", "-m", "Initial")
        git(self.root, "tag", "v0.1.0-beta.3")
        (self.root / "source.txt").write_text("new code\n")
        git(self.root, "commit", "-am", "Change")
        self.source = git(self.root, "rev-parse", "HEAD")
        git(self.root, "checkout", "--detach", self.source)

    def test_tag_source_is_stamped_without_changing_main(self):
        self.assertEqual(prepare(self.root), ("0.1.0-beta.4", "v0.1.0-beta.4"))
        self.assertEqual(git(self.root, "rev-parse", "main"), self.source)
        self.assertEqual(git(self.root, "diff", "--name-only", self.source, "HEAD"), "VERSION")
        self.assertEqual(git(self.root, "show", "v0.1.0-beta.4:VERSION"), "0.1.0-beta.4")

    def test_retry_reuses_published_tag_and_commit(self):
        first = prepare(self.root)
        commit = git(self.root, "rev-parse", "HEAD")
        git(self.root, "checkout", "--detach", self.source)
        self.assertEqual(prepare(self.root), first)
        self.assertEqual(git(self.root, "rev-parse", "HEAD"), commit)
        self.assertEqual(len(git(self.root, "tag").splitlines()), 2)

    def test_next_push_increments_even_when_main_version_stays_the_same(self):
        prepare(self.root)
        git(self.root, "checkout", "main")
        (self.root / "source.txt").write_text("another change\n")
        git(self.root, "commit", "-am", "Next push")
        git(self.root, "checkout", "--detach", "HEAD")
        self.assertEqual(prepare(self.root)[0], "0.1.0-beta.5")

    def test_explicit_tag_requires_matching_version_and_commit(self):
        version, tag = prepare(self.root)
        self.assertEqual(prepare(self.root, tag), (version, tag))
        with self.assertRaises(ValueError):
            prepare(self.root, "v0.1.0-beta.3")

    def test_reuses_legacy_tag_already_on_source(self):
        git(self.root, "checkout", "--detach", "v0.1.0-beta.3")
        self.assertEqual(prepare(self.root), ("0.1.0-beta.3", "v0.1.0-beta.3"))

    def test_refuses_dirty_source(self):
        (self.root / "source.txt").write_text("uncommitted\n")
        with self.assertRaisesRegex(ValueError, "clean checkout"):
            prepare(self.root)

    def test_refuses_false_source_marker(self):
        (self.root / "source.txt").write_text("unrelated code\n")
        (self.root / "VERSION").write_text("0.1.0-beta.4\n")
        git(self.root, "commit", "-am", f"Release\n\nRelease-Source: {self.source}")
        git(self.root, "tag", "v0.1.0-beta.4")
        git(self.root, "checkout", "--detach", self.source)
        with self.assertRaisesRegex(ValueError, "beyond VERSION"):
            prepare(self.root)


if __name__ == "__main__":
    unittest.main()
