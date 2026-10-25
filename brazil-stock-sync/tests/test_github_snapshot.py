import tempfile
import unittest
from pathlib import Path

from tools.prepare_github_snapshot import sanitize, prepare_snapshot


class GitHubSnapshotTests(unittest.TestCase):
    def test_redacts_private_links_but_preserves_test_credentials(self):
        text = 'https://doc.weixin.qq.com/sheet/e3_EXAMPLE_2a3fe75c9975?scode=REDACTED https://qyapi.weixin.qq.com/mcp/robot-doc?apikey=REDACTED https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=test'
        result = sanitize(text)
        self.assertNotIn('e3_EXAMPLE_2a3fe75c9975', result)
        self.assertNotIn('ABCDEFGHIJKLMNOP', result)
        self.assertNotIn('apikey=abcdefghijklmnop', result)
        self.assertIn('key=test', result)

    def test_never_copies_runtime_or_workbooks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'runtime').mkdir()
            (root / 'runtime/secret.py').write_text('SECRET', encoding='utf-8')
            (root / 'Stock_Order.xlsx').write_bytes(b'private')
            (root / 'main.py').write_text('print("ok")', encoding='utf-8')
            manifest = prepare_snapshot(root, ['runtime/secret.py', 'Stock_Order.xlsx', 'main.py'])
            self.assertEqual([item['path'] for item in manifest['files']], ['main.py'])
            self.assertEqual((root / 'runtime/github-public/brazil-stock-sync/main.py').read_text(encoding='utf-8'), 'print("ok")')
