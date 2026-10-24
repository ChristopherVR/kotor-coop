"""Export reviewed public files from one immutable private Git commit. No network or Git writes."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
import tempfile
from urllib.parse import unquote, urlsplit

POLICY = 'tools/publication/public-export.json'
IMPLEMENTATION = ('tools/Prepare-PublicRepository.ps1', 'tools/publication/export_public.py')
INVENTORY = '.publication-manifest.json'
PUBLIC_REPO = 'ChristopherVR/kotor-coop'
RETIRED = frozenset(('media/kotor-coop-overview.mp4', 'media/poster.jpg',
                     'media/provenance.json', 'media/transcript.txt', 'media/DejaVu-LICENSE.txt'))
TEXT_TYPES = {'.md', '.txt', '.html', '.css', '.svg', '.json'}
EXTENSIONS = TEXT_TYPES | {'.png', '.jpg', '.mp4'}
SECRET = re.compile(rb'(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|AKIA[0-9A-Z]{16}|sk-proj-[A-Za-z0-9_-]{30,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|https?://[^\s/:]+:[^\s/@]+@)')

class PolicyError(ValueError):
    pass

def git(root, *args):
    result = subprocess.run(['git', '-C', str(root), *args], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode:
        raise PolicyError('Git command failed: ' + ' '.join(args[:2]) + '; ' + result.stderr.decode(errors='replace').strip())
    return result.stdout

def digest(data):
    return hashlib.sha256(data).hexdigest()

def clean_path(value):
    if not isinstance(value, str) or not value or '\\' in value or ':' in value or not re.fullmatch(r'[A-Za-z0-9_./-]+', value):
        raise PolicyError('Invalid export path')
    p = PurePosixPath(value)
    if p.is_absolute() or str(p) != value or any(x.lower() in ('.', '..', '.git') or x.endswith(('.', ' ')) for x in p.parts):
        raise PolicyError('Unsafe export path: ' + value)
    if any(re.match(r'^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)', x, re.I) for x in p.parts):
        raise PolicyError('Reserved export path: ' + value)
    return value

def source_allowed(value):
    return value == 'LICENSE' or value.startswith(('site/public/', 'tools/publication/public/'))

def target_allowed(value):
    if value in ('README.md', 'SECURITY.md', 'LICENSE', '.gitignore', '.gitattributes'):
        return True
    return value.startswith('docs/') and PurePosixPath(value).suffix.lower() in EXTENSIONS and not any(x.startswith('.') for x in PurePosixPath(value).parts)

def linked(path):
    if not path.exists() and not path.is_symlink():
        return False
    return path.is_symlink() or bool(getattr(path.lstat(), 'st_file_attributes', 0) & 0x400)

def reject_links(path):
    for part in (path, *path.parents):
        if linked(part):
            raise PolicyError('Symlink or junction in export path: ' + str(part))

def blob(root, commit, path):
    clean_path(path)
    entry = git(root, 'ls-tree', '-z', commit, '--', path).split(b'\0')[0]
    if not entry or b'\t' not in entry:
        raise PolicyError('Committed publication input is missing: ' + path)
    mode, kind, oid = entry.split(b'\t')[0].split()
    if kind != b'blob' or mode not in (b'100644', b'100755'):
        raise PolicyError('Publication input is not a regular Git file: ' + path)
    return git(root, 'cat-file', 'blob', oid.decode())

def parse_policy(data):
    try:
        value = json.loads(data)
    except (ValueError, UnicodeError) as exc:
        raise PolicyError('Invalid publication policy JSON') from exc
    if value.get('schema') != 1 or value.get('target_repository') != PUBLIC_REPO or not isinstance(value.get('files'), list):
        raise PolicyError('Unsupported publication policy')
    seen = set()
    for entry in value['files']:
        if set(entry) != {'source', 'target'}:
            raise PolicyError('Each mapping must have exactly source and target')
        source, target = clean_path(entry['source']), clean_path(entry['target'])
        if not source_allowed(source) or not target_allowed(target):
            raise PolicyError('Mapping crosses the public boundary: ' + source + ' -> ' + target)
        if target.casefold() in seen:
            raise PolicyError('Duplicate public target: ' + target)
        seen.add(target.casefold())
    if '.gitattributes' not in seen:
        raise PolicyError('Publication policy must export .gitattributes for exact-byte checkouts')
    return value

def validate_content(contents):
    names = set(contents)
    for target, data in contents.items():
        if data.startswith(b'version https://git-lfs.github.com/spec/v1'):
            raise PolicyError('LFS pointers are not publication content: ' + target)
        if PurePosixPath(target).suffix not in TEXT_TYPES and target not in ('LICENSE', '.gitignore', '.gitattributes'):
            continue
        if SECRET.search(data):
            raise PolicyError('Potential credential in approved content: ' + target)
        text = data.decode('utf-8-sig')
        if re.search(r'kotor-coop-dev|(?:[A-Z]:[\\/](?:Users|Development)\b)|file://', text, re.I):
            raise PolicyError('Private development reference in public content: ' + target)
        links = re.findall(r'(?:href|src)\s*=\s*[\"\x27]([^\"\x27]+)', text, re.I)
        links += re.findall(r'!?\[[^\]]*\]\(([^\s)]+)(?:\s+[^)]*)?\)', text)
        links += re.findall(r'url\(\s*[\"\x27]?([^\s)\"\x27]+)', text, re.I)
        for link in links:
            parsed = urlsplit(link)
            if parsed.scheme or link.startswith(('//', '#')):
                continue
            path = unquote(parsed.path)
            if not path:
                continue
            # Absolute site paths and relative links must resolve to another exported file.
            full = PurePosixPath(path.lstrip('/')) if path.startswith('/') else PurePosixPath(target).parent / path
            parts = []
            for part in full.parts:
                if part == '..':
                    if not parts:
                        raise PolicyError('Link escapes public root: ' + target)
                    parts.pop()
                elif part != '.':
                    parts.append(part)
            resolved = '/'.join(parts)
            if path.endswith('/'):
                resolved += '/index.html'
            if resolved not in names:
                raise PolicyError('Local link is not exported: ' + target + ' -> ' + link)

def plan(source, destination, source_ref=None, adopt_existing=False):
    source = Path(source).absolute()
    destination = Path(destination).absolute()
    reject_links(source); reject_links(destination)
    source = source.resolve(); destination = destination.resolve()
    if source == destination or source in destination.parents or destination in source.parents:
        raise PolicyError('Public destination must be separate from the private repository')
    if not source_ref:
        source_ref = git(source, 'symbolic-ref', '--quiet', '--short', 'HEAD').decode().strip()
    commit = git(source, 'rev-parse', '--verify', '--end-of-options', source_ref + '^{commit}').decode().strip()
    if not re.fullmatch(r'[0-9a-f]{40,64}', commit):
        raise PolicyError('Cannot resolve exact source commit')
    policy_bytes = blob(source, commit, POLICY)
    policy = parse_policy(policy_bytes)
    selected = [POLICY, *IMPLEMENTATION] + [x['source'] for x in policy['files']]
    if git(source, 'status', '--porcelain', '--untracked-files=all', '--', *selected):
        raise PolicyError('Approved publication sources, policy or exporter implementation have uncommitted changes; commit reviewed inputs first')
    contents = {}; file_rows = []
    for row in policy['files']:
        data = blob(source, commit, row['source'])
        contents[row['target']] = data
        file_rows.append({**row, 'sha256': digest(data), 'bytes': len(data)})
    if contents.get('.gitattributes') != b'* -text\n':
        raise PolicyError('Public .gitattributes must be exactly * -text with an LF newline')
    validate_content(contents)
    inventory = {'schema': 1, 'source_commit': commit, 'source_ref': source_ref,
                 'policy_sha256': digest(policy_bytes), 'files': sorted(file_rows, key=lambda x:x['target'])}
    contents[INVENTORY] = (json.dumps(inventory, indent=2, ensure_ascii=True) + '\n').encode()
    prior = {}
    if (destination / INVENTORY).exists():
        raw = (destination / INVENTORY).read_bytes()
        try:
            previous = json.loads(raw)
            if previous['schema'] != 1:
                raise ValueError()
            for entry in previous['files']:
                path = clean_path(entry['target'])
                if not target_allowed(path) or not source_allowed(clean_path(entry['source'])) or not re.fullmatch(r'[0-9a-f]{64}', entry['sha256']):
                    raise ValueError()
                prior[path] = entry['sha256']
        except (ValueError, KeyError, TypeError):
            raise PolicyError('Invalid previous managed-file inventory')
    tracked = set()
    metadata = destination / '.git'
    reject_links(metadata)
    if metadata.exists() and not metadata.is_dir():
        raise PolicyError('Public destination must use its own .git directory; gitfiles/worktrees are not supported')
    if metadata.exists():
        remote = git(destination, 'remote', 'get-url', 'origin').decode().strip().removesuffix('.git')
        if remote not in ('https://github.com/' + PUBLIC_REPO, 'git@github.com:' + PUBLIC_REPO):
            raise PolicyError('Destination is not the expected public repository')
        if git(destination, 'status', '--porcelain', '--untracked-files=all'):
            raise PolicyError('Public checkout has pending changes; review/commit them before export')
        tracked = set(git(destination, 'ls-files', '-z').decode().rstrip('\0').split('\0'))
    # No arbitrary recursive deletion. Inventory-owned files and five named retired files only.
    allowed = set(contents) | set(prior) | RETIRED
    if destination.exists():
        for base, dirs, files in os.walk(destination, followlinks=False):
            dirs[:] = [x for x in dirs if not (Path(base) == destination and x == '.git')]
            for name in dirs + files:
                reject_links(Path(base) / name)
            for name in files:
                relative = (Path(base)/name).relative_to(destination).as_posix()
                if relative not in allowed:
                    raise PolicyError('Unmanaged file in public destination: ' + relative)
    writes=[]; deletes=[]
    for target, data in contents.items():
        path = destination/target
        reject_links(path)
        if path.exists() and not path.is_file():
            raise PolicyError('Export target is not a file: ' + target)
        old = path.read_bytes() if path.exists() else None
        if old == data:
            continue
        if old is not None and target != INVENTORY:
            if target in prior:
                if digest(old) != prior[target]:
                    raise PolicyError('Managed file changed outside exporter: ' + target)
            elif not (adopt_existing and target in tracked):
                raise PolicyError('Existing file differs; use --adopt-existing only after reviewing a clean tracked public checkout: ' + target)
        writes.append(target)
    for target in (set(prior) | RETIRED) - set(contents):
        path=destination/target
        reject_links(path)
        if path.exists():
            if not path.is_file():
                raise PolicyError('Stale target is not a regular file: ' + target)
            if target in prior:
                if digest(path.read_bytes()) != prior[target]:
                    raise PolicyError('Stale managed file was edited: ' + target)
            elif not (adopt_existing and target in tracked):
                raise PolicyError('Retired media deletion requires explicit adoption of a clean tracked checkout: ' + target)
            deletes.append(target)
    return {'source_commit':commit, 'source_ref':source_ref, 'destination':str(destination),
            'writes':sorted(writes), 'deletes':sorted(deletes), '_contents':contents}

def apply(plan_result):
    destination=Path(plan_result['destination'])
    reject_links(destination)
    destination.mkdir(parents=True, exist_ok=True)
    for target in [x for x in plan_result['writes'] if x != INVENTORY]:
        path=destination/target
        reject_links(path)
        path.parent.mkdir(parents=True,exist_ok=True)
        fd,tmp=tempfile.mkstemp(prefix='.export-',dir=path.parent)
        try:
            with os.fdopen(fd,'wb') as stream:
                stream.write(plan_result['_contents'][target])
            os.replace(tmp,path)
        finally:
            if os.path.exists(tmp):os.unlink(tmp)
    for target in plan_result['deletes']:
        path=destination/target
        reject_links(path)
        path.unlink()
    if INVENTORY in plan_result['writes']:
        fd,tmp=tempfile.mkstemp(prefix='.export-',dir=destination)
        try:
            with os.fdopen(fd,'wb') as stream:
                stream.write(plan_result['_contents'][INVENTORY])
            os.replace(tmp,destination/INVENTORY)
        finally:
            if os.path.exists(tmp):os.unlink(tmp)

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',default=str(Path(__file__).resolve().parents[2]))
    parser.add_argument('--destination',required=True)
    parser.add_argument('--source-ref')
    parser.add_argument('--adopt-existing',action='store_true')
    mode=parser.add_mutually_exclusive_group()
    mode.add_argument('--dry-run',action='store_true')
    mode.add_argument('--check',action='store_true')
    args=parser.parse_args()
    try:
        result=plan(args.source,args.destination,args.source_ref,args.adopt_existing)
        changed=bool(result['writes'] or result['deletes'])
        if not (args.dry_run or args.check):apply(result)
        print(json.dumps({k:v for k,v in result.items() if not k.startswith('_')},indent=2))
        return 1 if args.check and changed else 0
    except (PolicyError,OSError,UnicodeError) as exc:
        print('Publication export refused: '+str(exc),file=sys.stderr)
        return 2

if __name__=='__main__':
    raise SystemExit(main())
