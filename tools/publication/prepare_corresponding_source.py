"""Prepare public source companions only after proving the exact source boundary.

Standard library only. Does not build, publish, alter Git, or extract archive paths.
"""
from __future__ import annotations
import argparse
import base64
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import tarfile
import urllib.request

REPOSITORY = 'ChristopherVR/kotor-coop'
POLICY = 'tools/publication/public-export.json'
INVENTORY = 'tools/publication/corresponding-source.json'
MAX_ARCHIVE = 250_000_000


def git(root, *args):
    result = subprocess.run(['git', '-C', str(root), *args], capture_output=True)
    if result.returncode:
        raise ValueError('Cannot read reviewed source Git state')
    return result.stdout


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def git_blob_hash(data):
    return hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()


def clean_path(value):
    if (not isinstance(value, str) or not re.fullmatch(r'[A-Za-z0-9_./-]+', value) or
        PurePosixPath(value).is_absolute() or str(PurePosixPath(value)) != value or
        any(p in ('.', '..', '.git') for p in PurePosixPath(value).parts)):
        raise ValueError('Unsafe source inventory path')
    return value


def read_archive(path):
    payload = path if isinstance(path, bytes) else Path(path).read_bytes()
    if len(payload) > MAX_ARCHIVE:
        raise ValueError('Source archive is oversized')
    contents = {}; seen = set(); prefix = None; total = 0
    with tarfile.open(fileobj=io.BytesIO(payload), mode='r:gz') as archive:
        for member in archive:
            name = member.name.rstrip('/')
            clean_path(name)
            parts = PurePosixPath(name).parts
            if prefix is None:
                prefix = parts[0]
            if parts[0] != prefix:
                raise ValueError('Source archive has multiple roots')
            if member.isdir():
                continue
            if not member.isfile() or len(parts) < 2:
                raise ValueError('Source archive contains a link or non-file')
            relative = '/'.join(parts[1:])
            if relative.casefold() in seen:
                raise ValueError('Duplicate source archive path')
            seen.add(relative.casefold())
            total += member.size
            if member.size > 100_000_000 or total > MAX_ARCHIVE:
                raise ValueError('Source archive contents are oversized')
            contents[relative] = archive.extractfile(member).read()
    return contents


def committed_blobs(root, commit, paths):
    tree = {}
    for row in git(root, 'ls-tree', '-r', '-z', commit).split(b'\0'):
        if row:
            metadata, path = row.split(b'\t', 1)
            tree[path.decode()] = metadata.split()
    selected = {}
    for path in paths:
        clean_path(path)
        mode, kind, oid = tree.get(path, (None, None, None))
        if mode not in (b'100644', b'100755') or kind != b'blob':
            raise ValueError('Required source is not a regular committed file: ' + path)
        selected[path] = oid
    unique = list(dict.fromkeys(selected.values()))
    result = subprocess.run(['git', '-C', str(root), 'cat-file', '--batch'],
        input=b''.join(oid + b'\n' for oid in unique), capture_output=True)
    if result.returncode:
        raise ValueError('Cannot read source blobs')
    payloads = {}; offset = 0
    for oid in unique:
        end = result.stdout.find(b'\n', offset)
        header = result.stdout[offset:end].split()
        if end < offset or len(header) != 3 or header[:2] != [oid, b'blob']:
            raise ValueError('Invalid source blob response')
        size = int(header[2]); start = end + 1; offset = start + size + 1
        if offset > len(result.stdout) or result.stdout[offset - 1:offset] != b'\n':
            raise ValueError('Truncated source blob response')
        payloads[oid] = result.stdout[start:start + size]
    return {path: payloads[oid] for path, oid in selected.items()}


def validate_snapshot(root, contents):
    if git(root, 'status', '--porcelain', '--untracked-files=all'):
        raise ValueError('Corresponding source requires a clean reviewed checkout')
    head = git(root, 'rev-parse', 'HEAD').decode().strip()
    try:
        manifest = json.loads(contents['.publication-manifest.json'])
    except (KeyError, ValueError) as exc:
        raise ValueError('Missing or invalid public publication manifest') from exc
    if manifest.get('schema') != 2 or manifest.get('mode') != 'open-source':
        raise ValueError('Public source must use the reviewed open-source export')
    if manifest.get('source_commit') != head:
        raise ValueError('Public source_commit does not match the reviewed build commit')
    policy_bytes = git(root, 'show', head + ':' + POLICY)
    policy = json.loads(policy_bytes)
    if policy.get('schema') != 2 or policy.get('mode') != 'open-source':
        raise ValueError('Reviewed source policy is not an open-source snapshot')
    if manifest.get('policy_sha256') != sha256(policy_bytes):
        raise ValueError('Public source policy hash differs from reviewed policy')
    rows = manifest.get('files', [])
    if sorted((r['source'], r['target']) for r in rows) != sorted((r['source'], r['target']) for r in policy['files']):
        raise ValueError('Public source file mapping differs from reviewed policy')
    expected = {clean_path(r['target']) for r in rows} | {'.publication-manifest.json'}
    if len(expected) != len(rows) + 1 or set(contents) != expected:
        raise ValueError('Public source archive contains missing, duplicate or extra files')
    original = committed_blobs(root, head, [r['source'] for r in rows])
    for row in rows:
        data = contents[row['target']]
        if row.get('bytes') != len(data) or row.get('sha256') != sha256(data) or data != original[row['source']]:
            raise ValueError('Changed public source file: ' + row['target'])
    return head


def validate_dependencies(inventory, agent_deps, plugin_assets):
    if inventory.get('schemaVersion') != 1:
        raise ValueError('Unsupported corresponding-source inventory')
    actual = sorted(k for k in agent_deps['libraries'] if not k.startswith('KotorCoop.'))
    if actual != sorted(inventory['agentPackages']):
        raise ValueError('Agent dependency inventory changed; review corresponding sources')
    if sorted(plugin_assets['libraries']) != sorted(inventory['pluginPackages']):
        raise ValueError('NativeAOT dependency inventory changed; review corresponding sources')
    required = set(actual) | set(plugin_assets['libraries'])
    records = {r['package']: r for r in inventory['packages']}
    if len(records) != len(inventory['packages']) or set(records) != required:
        raise ValueError('Missing or duplicate corresponding-source package identity')
    sources = inventory['sources']
    for record in records.values():
        source = sources.get(record['source'], {})
        commit = source.get('commit', '')
        repo = source.get('repository', '')
        if (not re.fullmatch(r'[0-9a-f]{40}', commit) or
            not re.fullmatch(r'https://github.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo) or
            source.get('archiveUrl') != repo.replace('https://github.com/', 'https://codeload.github.com/') + '/zip/' + commit or
            not record.get('identityEvidence') or not re.fullmatch(r'[0-9a-f]{64}', record.get('nuspecSha256', '')) or
            len(base64.b64decode(record.get('nupkgSha512', ''), validate=True)) != 64):
            raise ValueError('Unresolved immutable corresponding source: ' + record['package'])


def validate_cache(inventory, cache):
    for record in inventory['packages']:
        name, version = record['package'].split('/')
        name = name.removeprefix('runtimepack.').lower()
        package = Path(cache) / name / version
        nuspec = package / (name + '.nuspec')
        nupkg = package / (name + '.' + version + '.nupkg')
        if sha256(nuspec.read_bytes()) != record['nuspecSha256']:
            raise ValueError('NuGet source metadata changed: ' + record['package'])
        digest = base64.b64encode(hashlib.sha512(nupkg.read_bytes()).digest()).decode()
        if digest != record['nupkgSha512']:
            raise ValueError('NuGet package bytes changed: ' + record['package'])


def request(url):
    return urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': 'kotor-coop-source-audit'}), timeout=30)


def verify_public_tree(public_commit, contents):
    with request(f'https://api.github.com/repos/{REPOSITORY}/git/trees/{public_commit}?recursive=1') as response:
        tree = json.load(response)
    if tree.get('truncated') or tree.get('sha') is None:
        raise ValueError('Public source tree is incomplete')
    expected = {}
    for row in tree['tree']:
        if row['type'] == 'tree':
            continue
        if row['type'] != 'blob' or row['mode'] not in ('100644', '100755'):
            raise ValueError('Public source tree contains a non-regular file')
        expected[row['path']] = row['sha']
    if set(expected) != set(contents) or any(git_blob_hash(data) != expected[path] for path, data in contents.items()):
        raise ValueError('Source archive does not match the exact public Git commit')


def verify_download(source):
    expected = source.get('archiveSha256')
    limit = 16_000_000 if expected else 65_536
    with request(source['archiveUrl']) as response:
        data = response.read(limit + 1 if expected else limit)
    if not data.startswith(b'PK'):
        raise ValueError('Upstream source archive is unavailable')
    if expected and (len(data) > limit or sha256(data) != expected or len(data) != source.get('archiveBytes')):
        raise ValueError('Upstream source archive hash changed')
    return {'url': source['archiveUrl'], 'verification': 'complete-sha256' if expected else 'bounded-archive-read', 'bytesRead': len(data)}


def prepare(args):
    root = Path(args.source_root).resolve()
    if not re.fullmatch(r'[0-9a-f]{40}', args.public_commit):
        raise ValueError('An exact immutable public commit is required')
    output = Path(args.output_directory).resolve()
    if output.exists():
        raise ValueError('Corresponding-source output directory must be new')
    archive_data = Path(args.public_source_archive).read_bytes()
    contents = read_archive(archive_data)
    head = validate_snapshot(root, contents)
    inventory = json.loads(git(root, 'show', head + ':' + INVENTORY))
    validate_dependencies(inventory, json.loads(Path(args.agent_deps).read_bytes()), json.loads(Path(args.plugin_assets).read_bytes()))
    validate_cache(inventory, args.nuget_cache)
    if not args.verify_downloads:
        raise ValueError('Supply --verify-downloads to verify public and upstream availability')
    verify_public_tree(args.public_commit, contents)
    with ThreadPoolExecutor(max_workers=4) as pool:
        availability = list(pool.map(verify_download, inventory['sources'].values()))
    if git(root, 'status', '--porcelain', '--untracked-files=all') or git(root, 'rev-parse', 'HEAD').decode().strip() != head:
        raise ValueError('Reviewed source changed during verification')
    version = git(root, 'show', head + ':VERSION').decode('utf-8-sig').strip()
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?', version):
        raise ValueError('Invalid release VERSION')
    name = f'kotor-coop-{version}-source.tar.gz'
    report = {'schemaVersion': 1, 'version': version, 'sourceCommit': head,
        'publicRepository': 'https://github.com/' + REPOSITORY, 'publicCommit': args.public_commit,
        'projectSourceArchive': {'name': name, 'sha256': sha256(archive_data), 'bytes': len(archive_data)},
        'inventory': inventory, 'availabilityCheckedUtc': datetime.now(timezone.utc).isoformat(), 'availability': availability}
    text = contents['docs/CORRESPONDING-SOURCE.md'].decode('utf-8-sig').rstrip() + '\n\n## This release\n\n'
    text += f'Version: `{version}`. Public source commit: `{args.public_commit}`.\n\n'
    text += f'Download `{name}` beside the Windows ZIP. SHA-256: `{sha256(archive_data)}`.\n\n'
    text += f'The exact [project source](https://github.com/{REPOSITORY}/tree/{args.public_commit}) includes build, installation and asset-generation inputs.\n\n'
    text += 'Dependency source archives (also recorded with package identities in `corresponding-source.json`):\n\n'
    for source in inventory['sources'].values():
        text += f"- [{source['repository'].removeprefix('https://github.com/')} at {source['commit']}]({source['archiveUrl']})\n"
    output.mkdir(parents=True)
    (output / name).write_bytes(archive_data)
    (output / 'CORRESPONDING-SOURCE.md').write_text(text, encoding='utf-8')
    (output / 'corresponding-source.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    assets = [name, 'CORRESPONDING-SOURCE.md', 'corresponding-source.json']
    (output / 'SHA256SUMS.txt').write_text(''.join(f'{sha256((output / name).read_bytes())}  {name}\n' for name in sorted(assets)), encoding='ascii')
    return {'assets': assets, 'sha256sums': 'SHA256SUMS.txt', 'publicCommit': args.public_commit, 'sourceCommit': head}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', required=True)
    parser.add_argument('--public-commit', required=True)
    parser.add_argument('--public-source-archive', required=True)
    parser.add_argument('--agent-deps', required=True)
    parser.add_argument('--plugin-assets', required=True)
    parser.add_argument('--output-directory', required=True)
    parser.add_argument('--nuget-cache', default=os.environ.get('NUGET_PACKAGES', str(Path.home() / '.nuget/packages')))
    parser.add_argument('--verify-downloads', action='store_true')
    args = parser.parse_args()
    try:
        print(json.dumps(prepare(args), indent=2))
    except (ValueError, OSError, KeyError, TypeError, tarfile.TarError) as exc:
        parser.exit(2, 'Corresponding-source verification failed: ' + str(exc) + '\n')


if __name__ == '__main__':
    main()
