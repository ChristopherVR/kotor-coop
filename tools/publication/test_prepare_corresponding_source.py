import argparse
import base64
import copy
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch

import prepare_corresponding_source as c


class CorrespondingSourceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='kotor-source-test-')
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.root = self.base / 'reviewed'; self.root.mkdir()
        self.git('init', '-b', 'main')
        self.git('config', 'user.name', 'Source fixture')
        self.git('config', 'user.email', 'fixture@example.invalid')
        self.commit = 'a' * 40
        source = 'example/Library@' + self.commit
        package = b'fixture nupkg'; nuspec = b'fixture nuspec'
        self.inventory = {'schemaVersion': 1, 'agentPackages': ['Example/1.0'], 'pluginPackages': [],
            'packages': [{'package': 'Example/1.0', 'source': source, 'nuspecSha256': c.sha256(nuspec),
                          'nupkgSha512': base64.b64encode(hashlib.sha512(package).digest()).decode(), 'identityEvidence': 'Fixture commit'}],
            'sources': {source: {'repository': 'https://github.com/example/Library', 'commit': self.commit,
                                'archiveUrl': 'https://codeload.github.com/example/Library/zip/' + self.commit}}}
        cache = self.base / 'cache/example/1.0'; cache.mkdir(parents=True)
        (cache / 'example.nuspec').write_bytes(nuspec)
        (cache / 'example.1.0.nupkg').write_bytes(package)
        files = {'VERSION': b'1.2.3\n', 'src/KotorCoop.Agent/Program.cs': b'// original source\n',
                 c.INVENTORY: (json.dumps(self.inventory) + '\n').encode(),
                 'tools/publication/public/CORRESPONDING-SOURCE.md': b'# Corresponding source\n'}
        self.policy = {'schema': 2, 'mode': 'open-source', 'files': [
            {'source': name, 'target': 'docs/CORRESPONDING-SOURCE.md' if name.endswith('CORRESPONDING-SOURCE.md') else name} for name in files]}
        files[c.POLICY] = json.dumps(self.policy).encode()
        for name, data in files.items():
            path = self.root / name; path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(data)
        self.git('add', '.'); self.git('commit', '-m', 'Reviewed source fixture')
        head = self.git('rev-parse', 'HEAD').decode().strip()
        rows = [dict(row, bytes=len(files[row['source']]), sha256=c.sha256(files[row['source']])) for row in self.policy['files']]
        manifest = {'schema': 2, 'mode': 'open-source', 'source_commit': head, 'policy_sha256': c.sha256(files[c.POLICY]), 'files': rows}
        self.contents = {row['target']: files[row['source']] for row in rows}
        self.contents['.publication-manifest.json'] = json.dumps(manifest).encode()
        self.deps = {'libraries': {'Example/1.0': {}, 'KotorCoop.Agent/1.0': {}}}
        (self.base / 'agent.json').write_text(json.dumps(self.deps))
        (self.base / 'plugin.json').write_text(json.dumps({'libraries': {}}))
        self.archive = self.base / 'public.tar.gz'
        self.archive.write_bytes(self.tar(self.contents))

    def git(self, *args):
        return subprocess.check_output(['git', '-C', str(self.root), *args], stderr=subprocess.DEVNULL)

    def tar(self, contents):
        out = io.BytesIO()
        with tarfile.open(fileobj=out, mode='w:gz') as archive:
            for name, data in contents.items():
                entry = tarfile.TarInfo('public-' + self.commit[:7] + '/' + name)
                entry.size = len(data); archive.addfile(entry, io.BytesIO(data))
        return out.getvalue()

    def args(self):
        return argparse.Namespace(source_root=str(self.root), public_commit=self.commit, public_source_archive=str(self.archive),
            agent_deps=str(self.base / 'agent.json'), plugin_assets=str(self.base / 'plugin.json'),
            nuget_cache=str(self.base / 'cache'), output_directory=str(self.base / 'output'), verify_downloads=True)

    def test_matching_snapshot_and_dependency_cache_pass(self):
        c.validate_snapshot(self.root, self.contents)
        c.validate_dependencies(self.inventory, self.deps, {'libraries': {}})
        c.validate_cache(self.inventory, self.base / 'cache')
        self.assertEqual(self.contents, c.read_archive(self.archive.read_bytes()))

    def test_forged_source_commit_missing_extra_and_changed_files_fail(self):
        for change in ('commit', 'missing', 'extra', 'changed', 'mapping'):
            contents = copy.deepcopy(self.contents)
            manifest = json.loads(contents['.publication-manifest.json'])
            if change == 'commit': manifest['source_commit'] = 'b' * 40
            if change == 'mapping': manifest['files'][0]['source'] = 'src/other.cs'
            if change == 'missing': del contents['src/KotorCoop.Agent/Program.cs']
            if change == 'extra': contents['private-recording.wav'] = b'private'
            if change == 'changed': contents['src/KotorCoop.Agent/Program.cs'] = b'changed source'
            contents['.publication-manifest.json'] = json.dumps(manifest).encode()
            with self.subTest(change=change), self.assertRaises(ValueError): c.validate_snapshot(self.root, contents)

    def test_unreviewed_new_dependency_or_missing_identity_fails(self):
        deps = copy.deepcopy(self.deps); deps['libraries']['New/2.0'] = {}
        with self.assertRaisesRegex(ValueError, 'inventory changed'): c.validate_dependencies(self.inventory, deps, {'libraries': {}})
        inventory = copy.deepcopy(self.inventory); inventory['sources'] = {}
        with self.assertRaisesRegex(ValueError, 'Unresolved'): c.validate_dependencies(inventory, self.deps, {'libraries': {}})
        with self.assertRaisesRegex(ValueError, 'NativeAOT'): c.validate_dependencies(self.inventory, self.deps, {'libraries': {'NewRuntime/2.0': {}}})

    def test_changed_nuget_bytes_fail(self):
        (self.base / 'cache/example/1.0/example.1.0.nupkg').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'package bytes changed'): c.validate_cache(self.inventory, self.base / 'cache')

    def test_archive_links_traversal_and_duplicate_paths_fail(self):
        with self.assertRaises(ValueError): c.read_archive(self.tar({'../escape': b'private'}))
        with self.assertRaises(ValueError): c.read_archive(self.tar({'file.cs': b'a', 'FILE.cs': b'b'}))
        out = io.BytesIO()
        with tarfile.open(fileobj=out, mode='w:gz') as archive:
            entry = tarfile.TarInfo('root/link'); entry.type = tarfile.SYMTYPE; entry.linkname = '../private'; archive.addfile(entry)
        with self.assertRaises(ValueError): c.read_archive(out.getvalue())

    def test_public_commit_tree_is_bound_to_every_source_byte(self):
        tree = {'sha': 'tree-id', 'tree': [dict(path=name, type='blob', mode='100644', sha=c.git_blob_hash(data)) for name,data in self.contents.items()]}
        def response(): return io.BytesIO(json.dumps(tree).encode())
        with patch.object(c, 'request', side_effect=lambda url: response()):
            c.verify_public_tree(self.commit, self.contents)
            changed = dict(self.contents); changed['VERSION'] = b'forged version'
            with self.assertRaisesRegex(ValueError, 'exact public Git commit'): c.verify_public_tree(self.commit, changed)

    def test_source_download_failure_or_hash_change_stops_preparation(self):
        source = next(iter(self.inventory['sources'].values()))
        with patch.object(c, 'request', return_value=io.BytesIO(b'not an archive')):
            with self.assertRaises(ValueError): c.verify_download(source)
        source = dict(source, archiveSha256='0' * 64, archiveBytes=4)
        with patch.object(c, 'request', return_value=io.BytesIO(b'PKxx')):
            with self.assertRaisesRegex(ValueError, 'hash changed'): c.verify_download(source)

    def test_prepare_emits_only_safe_companions_and_checksums(self):
        with patch.object(c, 'verify_public_tree'), patch.object(c, 'verify_download', return_value={'verification': 'fixture'}):
            result = c.prepare(self.args())
        output = self.base / 'output'
        self.assertEqual({'kotor-coop-1.2.3-source.tar.gz', 'CORRESPONDING-SOURCE.md', 'corresponding-source.json', 'SHA256SUMS.txt'}, {p.name for p in output.iterdir()})
        for name in result['assets']:
            self.assertIn(c.sha256((output / name).read_bytes()) + '  ' + name, (output / 'SHA256SUMS.txt').read_text())
        self.assertNotIn(str(self.base), (output / 'corresponding-source.json').read_text())

    def test_dirty_source_or_missing_online_verification_fails_before_output(self):
        args = self.args(); args.verify_downloads = False
        with self.assertRaisesRegex(ValueError, 'verify-downloads'): c.prepare(args)
        (self.root / 'src/KotorCoop.Agent/Program.cs').write_bytes(b'pending')
        with self.assertRaisesRegex(ValueError, 'clean reviewed'): c.validate_snapshot(self.root, self.contents)
        self.assertFalse((self.base / 'output').exists())


if __name__ == '__main__': unittest.main()
