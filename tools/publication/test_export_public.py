"""Run: python -m unittest discover -s tools/publication -p test_export_public.py -v"""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import export_public as e

class ExportTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='kotor-export-test-')
        self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name); self.source=self.root/'private'; self.target=self.root/'public'
        self.source.mkdir()
        self.run_git(self.source,'init','-b','main')
        self.run_git(self.source,'config','user.name','Publication fixture')
        self.run_git(self.source,'config','user.email','fixture@example.invalid')
        self.policy={'schema':1,'target_repository':e.PUBLIC_REPO,'files':[
            {'source':'site/public/project-README.md','target':'README.md'},
            {'source':'site/public/index.html','target':'docs/index.html'},
            {'source':'tools/publication/public/gitattributes','target':'.gitattributes'}]}
        self.write('tools/publication/public/gitattributes',b'* -text\n')
        self.write('site/public/project-README.md',b'# Example\n[Site](docs/index.html)\n')
        self.write('site/public/index.html',b'<h1>Reviewed public site</h1>\n')
        self.write('private-recording.wav',b'PRIVATE RECORDING NEVER EXPORT')
        self.save_policy();self.commit()

    def run_git(self,root,*args):
        return e.git(root,*args)
    def write(self,name,data):
        p=self.source/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(data)
    def save_policy(self):
        self.write(e.POLICY,(json.dumps(self.policy)+'\n').encode())
    def commit(self):
        self.run_git(self.source,'add','.')
        self.run_git(self.source,'commit','-m','Reviewed fixture')
    def plan(self,**kw):
        return e.plan(self.source,self.target,**kw)
    def export(self):
        result=self.plan();e.apply(result);return result
    def public_git(self):
        self.run_git(self.target,'init','-b','main')
        self.run_git(self.target,'config','user.name','Public fixture')
        self.run_git(self.target,'config','user.email','fixture@example.invalid')
        self.run_git(self.target,'remote','add','origin','https://github.com/'+e.PUBLIC_REPO+'.git')
        self.run_git(self.target,'add','.')
        self.run_git(self.target,'commit','-m','Public fixture')

    def test_export_is_exact_commit_bytes_without_private_files_or_history(self):
        result=self.export()
        self.assertEqual((self.target/'README.md').read_bytes(),e.blob(self.source,result['source_commit'],'site/public/project-README.md'))
        self.assertFalse((self.target/'.git').exists())
        self.assertFalse((self.target/'private-recording.wav').exists())
        inv=json.loads((self.target/e.INVENTORY).read_bytes())
        self.assertEqual(inv['source_commit'],self.run_git(self.source,'rev-parse','HEAD').decode().strip())
        self.assertEqual(next(x['sha256'] for x in inv['files'] if x['target']=='README.md'),e.digest((self.target/'README.md').read_bytes()))
        self.assertNotIn(str(self.source),(self.target/e.INVENTORY).read_text())

    def test_repeat_export_is_noop(self):
        self.export();result=self.plan()
        self.assertEqual([],result['writes']);self.assertEqual([],result['deletes'])

    def test_explicit_old_ref_reads_old_committed_content(self):
        old=self.run_git(self.source,'rev-parse','HEAD').decode().strip()
        self.write('site/public/index.html',b'New reviewed site');self.commit()
        result=self.plan(source_ref=old)
        self.assertIn(b'Reviewed public site',result['_contents']['docs/index.html'])
        self.assertEqual(old,result['source_commit'])

    def test_dirty_selected_source_refused(self):
        self.write('site/public/index.html',b'Uncommitted information')
        with self.assertRaisesRegex(e.PolicyError,'uncommitted'):self.plan()

    def test_unrelated_private_dirty_file_never_exported(self):
        self.write('private-recording.wav',b'new private audio')
        result=self.export();self.assertNotIn('private-recording.wav',result['_contents'])

    def test_missing_committed_input_refused(self):
        self.policy['files'].append({'source':'site/public/missing.html','target':'docs/missing.html'})
        self.save_policy();self.commit()
        with self.assertRaisesRegex(e.PolicyError,'missing'):self.plan()

    def test_path_traversal_backslash_absolute_and_git_rejected(self):
        for name in ('../secret','/outside','docs/../secret','docs\\evil','docs/.git/config','docs/.GIT/config','docs/CON.txt'):
            with self.subTest(name=name),self.assertRaises(e.PolicyError):e.clean_path(name)

    def test_private_sources_and_executable_targets_refused(self):
        for source,target in [('docs/research/evidence.md','docs/evidence.md'),('artifacts/video.mp4','docs/media/video.mp4'),('site/public/a.html','.github/workflows/publish.yml'),('site/public/a.html','docs/malware.exe')]:
            policy={'schema':1,'target_repository':e.PUBLIC_REPO,'files':[{'source':source,'target':target}]}
            with self.subTest(source=source,target=target),self.assertRaises(e.PolicyError):e.parse_policy(json.dumps(policy))

    def test_case_insensitive_duplicate_target_refused(self):
        self.policy['files'].append({'source':'site/public/other.html','target':'docs/INDEX.html'})
        with self.assertRaisesRegex(e.PolicyError,'Duplicate'):e.parse_policy(json.dumps(self.policy))

    def test_source_git_symlink_rejected_without_following(self):
        path='site/public/index.html'
        # A Git tree may contain symlinks even on a Windows checkout configured without them.
        payload=subprocess.run(['git','-C',str(self.source),'hash-object','-w','--stdin'],input=b'../../private-recording.wav',stdout=subprocess.PIPE,check=True).stdout.decode().strip()
        self.run_git(self.source,'update-index','--add','--cacheinfo','120000,'+payload+','+path)
        self.run_git(self.source,'commit','-m','Symlink fixture')
        self.run_git(self.source,'update-index','--assume-unchanged',path)
        with self.assertRaisesRegex(e.PolicyError,'regular Git file'):self.plan()

    def test_destination_inside_source_refused(self):
        with self.assertRaisesRegex(e.PolicyError,'separate'):e.plan(self.source,self.source/'public')

    def test_unknown_destination_file_not_deleted(self):
        self.export();(self.target/'keep.txt').write_text('private user data')
        with self.assertRaisesRegex(e.PolicyError,'Unmanaged'):self.plan()
        self.assertTrue((self.target/'keep.txt').exists())

    def test_edited_managed_file_not_overwritten(self):
        self.export();(self.target/'README.md').write_text('manual edit')
        with self.assertRaisesRegex(e.PolicyError,'outside exporter'):self.plan()

    def test_removed_mapping_deletes_only_unchanged_inventory_owned_file(self):
        self.policy['files'].append({'source':'site/public/old.html','target':'docs/old.html'})
        self.write('site/public/old.html',b'old');self.save_policy();self.commit();self.export()
        self.policy['files'].pop();self.save_policy();self.commit()
        result=self.plan();self.assertEqual(['docs/old.html'],result['deletes']);e.apply(result)
        self.assertFalse((self.target/'docs/old.html').exists());self.assertTrue((self.target/'README.md').exists())

    def test_edited_stale_file_not_deleted(self):
        self.policy['files'].append({'source':'site/public/old.html','target':'docs/old.html'})
        self.write('site/public/old.html',b'old');self.save_policy();self.commit();self.export()
        (self.target/'docs/old.html').write_bytes(b'new public edit')
        self.policy['files'].pop();self.save_policy();self.commit()
        with self.assertRaisesRegex(e.PolicyError,'Stale managed file was edited'):self.plan()

    def test_adoption_requires_clean_correct_public_repo(self):
        self.target.mkdir();(self.target/'README.md').write_text('old public README');self.public_git()
        with self.assertRaisesRegex(e.PolicyError,'adopt-existing'):self.plan()
        result=self.plan(adopt_existing=True);self.assertIn('README.md',result['writes'])
        self.run_git(self.target,'remote','set-url','origin','https://github.com/elsewhere/another.git')
        with self.assertRaisesRegex(e.PolicyError,'expected public'):self.plan(adopt_existing=True)

    def test_rejected_overview_deleted_only_with_explicit_tracked_adoption(self):
        self.target.mkdir();path=self.target/'media/kotor-coop-overview.mp4';path.parent.mkdir();path.write_bytes(b'rejected')
        self.public_git()
        with self.assertRaisesRegex(e.PolicyError,'explicit adoption'):self.plan()
        result=self.plan(adopt_existing=True);self.assertEqual(['media/kotor-coop-overview.mp4'],result['deletes'])
        e.apply(result);self.assertFalse(path.exists())

    def test_dirty_public_checkout_refused(self):
        self.export();self.public_git();(self.target/'README.md').write_text('pending public edit')
        with self.assertRaisesRegex(e.PolicyError,'pending changes'):self.plan()

    def test_private_links_missing_links_and_tokens_refused(self):
        for payload in (b'[Internal](research/private.md)',b'https://github.com/ChristopherVR/kotor-coop-dev',b'ghp_'+b'a'*35):
            with self.subTest(payload=payload[:20]),self.assertRaises(e.PolicyError):e.validate_content({'README.md':payload})

    def test_lfs_pointer_not_published_as_video(self):
        with self.assertRaisesRegex(e.PolicyError, 'LFS pointers'):
            e.validate_content({'docs/media/trailer.mp4': b'version https://git-lfs.github.com/spec/v1\noid sha256:abc\n'})

    def test_tampered_inventory_cannot_delete_outside_destination(self):
        self.export()
        inv=json.loads((self.target/e.INVENTORY).read_bytes())
        inv['files'][0]['target']='../precious.txt'
        (self.target/e.INVENTORY).write_text(json.dumps(inv))
        outside=self.root/'precious.txt';outside.write_text('keep')
        with self.assertRaises(e.PolicyError):self.plan()
        self.assertEqual('keep',outside.read_text())

    def test_destination_symlink_refused(self):
        real=self.root/'actual-public';real.mkdir()
        try:self.target.symlink_to(real,target_is_directory=True)
        except OSError as exc:self.skipTest('Platform cannot create test symlink: '+str(exc))
        with self.assertRaisesRegex(e.PolicyError,'Symlink or junction'):self.plan()
        self.assertEqual([],list(real.iterdir()))

    def test_windows_autocrlf_clone_preserves_all_published_hashes(self):
        self.export();self.public_git()
        clone=self.root/'fresh-public-clone'
        subprocess.run(['git','-c','core.autocrlf=true','clone',str(self.target),str(clone)],check=True,capture_output=True)
        self.run_git(clone,'config','core.autocrlf','true')
        self.run_git(clone,'remote','set-url','origin','https://github.com/'+e.PUBLIC_REPO+'.git')
        inv=json.loads((clone/e.INVENTORY).read_bytes())
        for row in inv['files']:
            self.assertEqual(row['sha256'],e.digest((clone/row['target']).read_bytes()),row['target'])
        self.assertEqual(b'',self.run_git(clone,'status','--porcelain'))
        result=e.plan(self.source,clone)
        self.assertEqual([],result['writes']);self.assertEqual([],result['deletes'])

    def test_dirty_exporter_or_wrapper_refused(self):
        for path in e.IMPLEMENTATION:
            self.write(path,b'approved implementation fixture\n')
        self.commit()
        for path in e.IMPLEMENTATION:
            self.write(path,b'unreviewed local implementation\n')
            with self.subTest(path=path),self.assertRaisesRegex(e.PolicyError,'implementation.*uncommitted'):self.plan()
            self.write(path,b'approved implementation fixture\n')

    def test_public_git_metadata_symlink_refused_before_git_access(self):
        self.target.mkdir()
        try:(self.target/'.git').symlink_to(self.source/'.git',target_is_directory=True)
        except OSError as exc:self.skipTest('Platform cannot create test symlink: '+str(exc))
        with self.assertRaisesRegex(e.PolicyError,'Symlink or junction'):self.plan()

    def test_public_gitfile_pointing_elsewhere_refused(self):
        self.target.mkdir()
        (self.target/'.git').write_text('gitdir: '+str(self.source/'.git')+'\n')
        with self.assertRaisesRegex(e.PolicyError,'gitfiles/worktrees'):self.plan()

    def test_policy_cannot_disable_exact_byte_attributes(self):
        self.write('tools/publication/public/gitattributes',b'* text=auto\n');self.commit()
        with self.assertRaisesRegex(e.PolicyError,'must be exactly'):self.plan()

    def test_cli_dryrun_and_check_do_not_create_destination(self):
        command=[sys.executable,str(Path(e.__file__)), '--source',str(self.source),'--destination',str(self.target)]
        self.assertEqual(0,subprocess.run(command+['--dry-run'],capture_output=True).returncode)
        self.assertEqual(1,subprocess.run(command+['--check'],capture_output=True).returncode)
        self.assertFalse(self.target.exists())
        self.export()
        self.assertEqual(0,subprocess.run(command+['--check'],capture_output=True).returncode)

if __name__=='__main__':unittest.main()
