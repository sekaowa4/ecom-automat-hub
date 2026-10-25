"""Create a sanitized public-source snapshot; never upload local runtime data."""

import hashlib
import json
import re
import subprocess
from pathlib import Path


def sanitize(text):
    text = re.sub(r'\b([es]3_)[A-Za-z0-9_-]{16,}', lambda m: m[1] + 'EXAMPLE_' + hashlib.sha256(m[0].encode()).hexdigest()[:12], text)
    text = re.sub(r'([?&](?:apikey|key|token|scode)=)[A-Za-z0-9_%.-]{12,}', r'\1REDACTED', text, flags=re.I)
    text = re.sub(r'(us_u=)[A-Za-z0-9_%.-]{16,}', r'\1REDACTED', text)
    username = Path.home().name
    if len(username) > 3:
        text = text.replace(username, 'USER')
    return text


def prepare_snapshot(root, paths):
    root = Path(root).resolve()
    destination = root / 'runtime/github-public/brazil-stock-sync'
    manifest = {'repository': 'sekaowa4/ecom-automat-hub', 'prefix': 'brazil-stock-sync', 'files': []}
    for relative in sorted(set(paths)):
        parts = Path(relative).parts
        if Path(relative).is_absolute() or '..' in parts:
            raise ValueError('Unsafe snapshot path')
        if any(part in {'runtime', '_runtime', '.git', '__pycache__', 'node_modules', '.venv', 'venv', '导出文件', '预演结果', '正式运行'} for part in parts):
            continue
        path = root / relative
        if path.suffix.lower() not in {'.py', '.js', '.cjs', '.html', '.css', '.md', '.cmd'} and relative != '.gitignore':
            continue
        if path.is_symlink() or not path.is_file():
            raise ValueError(f'Not a regular source file: {relative}')
        original = path.read_text(encoding='utf-8-sig')
        content = sanitize(original)
        payload = content.encode('utf-8')
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(payload)
        blob = hashlib.sha1(b'blob ' + str(len(payload)).encode() + b'\0' + payload).hexdigest()
        manifest['files'].append({'path': Path(relative).as_posix(), 'source_sha256': hashlib.sha256(original.encode()).hexdigest(), 'blob_sha': blob, 'bytes': len(payload), 'sanitized': content != original})
    output = root / 'runtime/github_public_manifest.json'
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    return manifest


if __name__ == '__main__':
    root = Path(__file__).resolve().parents[1]
    result = subprocess.run(['git', '-c', 'core.quotePath=false', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], cwd=root, check=True, capture_output=True)
    paths = [p for p in result.stdout.decode('utf-8').split('\0') if p]
    manifest = prepare_snapshot(root, paths)
    print(json.dumps({'source_files': len(manifest['files']), 'sanitized_files': sum(item['sanitized'] for item in manifest['files']), 'manifest': 'runtime/github_public_manifest.json'}, ensure_ascii=False))
