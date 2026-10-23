"""Read-only privacy preflight. Reports locations/categories, never matched secrets.

Review every finding; zero matches is a scoped check, not a guarantee of no secrets.
Only explicitly selected Git history is inspected, not local assistant checkpoint refs.
"""
import argparse
import hashlib
import ipaddress
import json
from pathlib import Path
import re
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parent.parent
TEXT = {'.cs', '.xaml', '.csproj', '.manifest', '.md', '.txt', '.py', '.ps1', '.json', '.yml', '.yaml'}
FORBIDDEN = re.compile(r'(^|/)(launcher_config[^/]*|icon_cache[^/]*|logs|Backgrounds|\.env(?:\.[^/]*)?|[^/]+\.(?:pdb|pfx|p12|pem|key|lnk))($|/)', re.I)
PATTERNS = {
    'github-token': re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{30,})\b'),
    'private-key': re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
    'cloud-key': re.compile(r'\b(?:AKIA|ASIA)[A-Z0-9]{16}\b'),
    'user-profile-path': re.compile(r'[A-Za-z]:[\\/]Users[\\/]([^\\/\s<>"\']+)', re.I),
    'workspace-path': re.compile(r'[A-Za-z]:[\\/]Zcode[\\/]', re.I),
    'email': re.compile(r'[A-Za-z0-9.!#$%&\'*+/=?^_`{|}~-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+'),
    'private-ip': re.compile(r'(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])'),
}
findings = []
counts = {'history_commits': 0, 'history_blobs': 0, 'working_files': 0, 'archives': 0, 'archive_entries': 0}

def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT)

def inspect_bytes(scope, path, data):
    normalized = path.replace('\\', '/')
    if FORBIDDEN.search(normalized):
        findings.append({'scope': scope, 'file': path, 'kind': 'forbidden-runtime-or-sensitive-file'})
    textual = Path(path).suffix.lower() in TEXT or Path(path).name.startswith('.git')
    views = [('utf-8', data.decode('utf-8', errors='replace'))]
    if not textual:
        views.append(('utf-16le', data.decode('utf-16le', errors='replace')))
    for encoding, text in views:
        for kind, pattern in PATTERNS.items():
            # Vendor runtime emails/build paths are outside the owner-data check.
            if not textual and kind in ('email', 'private-ip'):
                continue
            for match in pattern.finditer(text):
                if kind == 'user-profile-path' and match.group(1).lower() in {'user', 'username', 'example', 'test', 'public', 'default', 'yourname'}:
                    continue
                if kind == 'email' and match.group().lower().endswith('noreply.github.com'):
                    continue
                if kind == 'private-ip':
                    context = text.splitlines()[text.count('\n', 0, match.start())]
                    if path.endswith('.deps.json') and '"assemblyVersion"' in context:
                        continue
                    try:
                        address = ipaddress.ip_address(match.group())
                        if not address.is_private or address.is_loopback or address.is_unspecified:
                            continue
                    except ValueError:
                        continue
                findings.append({'scope': scope, 'file': path, 'line': text.count('\n', 0, match.start()) + 1 if textual else None,
                                 'kind': kind, 'encoding': encoding})

def inspect_history(refs):
    commits = git('rev-list', *refs).decode().splitlines()
    counts['history_commits'] = len(commits)
    seen = set()
    for commit in commits:
        inspect_bytes('commit-metadata', commit[:12], git('show', '-s', '--format=%B%n%an <%ae>%n%cn <%ce>', commit))
        for item in git('ls-tree', '-r', '-z', commit).split(b'\0'):
            if not item:
                continue
            metadata, path = item.split(b'\t', 1)
            _, kind, oid = metadata.split()
            if kind != b'blob' or oid in seen:
                continue
            seen.add(oid)
            inspect_bytes('history', path.decode('utf-8'), git('cat-file', 'blob', oid.decode()))
    counts['history_blobs'] = len(seen)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--ref', action='append', required=True)
    parser.add_argument('--zip', action='append', default=[])
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    inspect_history(args.ref)
    for raw in set(git('ls-files', '-z', '--cached', '--others', '--exclude-standard').split(b'\0')):
        if not raw:
            continue
        relative = raw.decode('utf-8')
        path = ROOT / relative
        if path.is_file():
            counts['working_files'] += 1
            inspect_bytes('working-tree', relative, path.read_bytes())
    archives = []
    for name in args.zip:
        path = Path(name)
        counts['archives'] += 1
        with zipfile.ZipFile(path) as package:
            entries = [i for i in package.infolist() if not i.is_dir()]
            counts['archive_entries'] += len(entries)
            for item in entries:
                relative = item.filename.replace('\\', '/')
                # Inspect names for all files and owner-built binaries/text for content.
                owner = Path(relative).name.startswith('QuickLauncherWinUI3') or Path(relative).suffix.lower() in TEXT | {'.xbf', '.pri'} or '/Assets/' in relative
                inspect_bytes('release-archive', relative, package.read(item) if owner else b'')
        with path.open('rb') as content:
            archives.append({'name': path.name, 'sha256': hashlib.file_digest(content, 'sha256').hexdigest(), 'entries': len(entries)})
    result = {'counts': counts, 'archives': archives, 'findings': findings, 'scope': 'selected reachable history, publishable working files, and supplied release archives'}
    Path(args.output).parent.mkdir(parents=True, exist_ok=True)
    Path(args.output).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False, indent=2))

if __name__ == '__main__':
    main()
