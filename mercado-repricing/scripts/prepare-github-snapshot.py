"""Prepare an allowlisted public-source snapshot without changing live files."""
from pathlib import Path
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parents[1]
VERSION = re.search(r'<Version>([^<]+)</Version>', (ROOT / 'server/Repricing.Api.csproj').read_text(encoding='utf-8-sig')).group(1)
DEST = ROOT / ('tmp/github-source-snapshot-' + VERSION + '/mercado-repricing')
SKIP = {'node_modules', 'bin', 'obj', 'wwwroot', 'TestResults', '__pycache__', '.git'}
DOCS = {
    '混合调价使用说明.md', '补贴活动收益优先.md', '旧式商品调价规则.md',
    '标签剪裁使用说明.md', '箱唛裁切与标签预览.md', '周末活动报名与验证日志.md',
    '同事使用说明.txt', 'Meil价格调整表约定.md', 'GITHUB_SYNC.md',
    '降价实测复盘与固定流程.md',
    '数据看板使用说明.md',
    '调价速度优化说明.md',
}
SCRIPT_NAMES = {'check.ps1', 'dotnet.ps1', 'page-source.cjs', 'publish.ps1', 'start.ps1', 'prepare-github-snapshot.py'}
TEXT_SUFFIXES = {'.cs', '.csproj', '.js', '.cjs', '.ts', '.tsx', '.css', '.json', '.html', '.svg', '.ps1', '.py', '.txt', '.md', '.toml'}
SECRET = re.compile(r'(?:ghp_[A-Za-z0-9]{25,}|github_pat_[A-Za-z0-9_]{35,}|sk-proj-[A-Za-z0-9_-]{25,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|https://qyapi\.weixin\.qq\.com/cgi-bin/webhook/send\?key=[A-Za-z0-9-]{20,})')


def selected():
    paths = [ROOT / n for n in ['.gitignore', 'AGENTS.md', 'README.md', 'global.json', '启动调价程序.cmd']]
    for name in ['server', 'web', 'tests', 'tools/carton']:
        for path in (ROOT / name).rglob('*'):
            relative = path.relative_to(ROOT)
            if not path.is_file() or any(p in SKIP for p in relative.parts):
                continue
            if path.suffix in TEXT_SUFFIXES or 'carton-licenses' in relative.parts or path.name.endswith('LICENSE') or path.name.startswith('LICENSE'):
                paths.append(path)
            elif relative.parts[:3] == ('tests', 'fixtures', 'labels') and path.suffix == '.pdf':
                paths.append(path)
    paths += [ROOT / 'scripts' / n for n in SCRIPT_NAMES]
    paths += [ROOT / 'docs' / n for n in DOCS]
    paths += [ROOT / 'samples' / n for n in ['Meil价格调整表-模板.xlsx', '今日调价模板.csv', '模拟调价样例.csv']]
    return sorted(set(paths), key=lambda p: p.relative_to(ROOT).as_posix())


def main():
    # Build in a fresh directory; never merge stale files into an upload snapshot.
    if DEST.exists():
        raise SystemExit('Snapshot already exists. Inspect it and choose a fresh snapshot directory before preparing again.')
    payloads = []
    for source in selected():
        relative = source.relative_to(ROOT)
        data = source.read_bytes()
        if source.suffix not in {'.pdf', '.xlsx'}:
            try:
                text = data.decode('utf-8-sig')
            except UnicodeDecodeError:
                if 'carton-licenses' not in relative.parts:
                    raise
                text = data.decode('latin-1')
            if relative.as_posix() == 'docs/降价实测复盘与固定流程.md':
                start, end = text.index('## 三个 SKU'), text.index('## 标签数量与执行边界')
                text = text[:start] + '## 三个 SKU\n\n本机实测记录不公开。\n\n' + text[end:]
            if SECRET.search(text):
                raise SystemExit(f'Possible credential in {relative}; snapshot aborted.')
            data = text.replace('\r\n', '\n').replace('\r', '\n').encode('utf-8')
        payloads.append((relative, data))
    manifest = []
    for relative, data in payloads:
        destination = DEST / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)
        manifest.append({'path': relative.as_posix(), 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
    (DEST / 'source-manifest.json').write_bytes((json.dumps(manifest, ensure_ascii=False, indent=2) + '\n').encode('utf-8'))
    print(json.dumps({'directory': str(DEST), 'files': len(manifest), 'bytes': sum(x['bytes'] for x in manifest)}, ensure_ascii=False))


if __name__ == '__main__':
    main()
