"""Exercise the real local UI and approval endpoint with disposable batch data."""
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
from http.server import ThreadingHTTPServer

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from webapp.server import Handler, Store


class FakeProcess:
    def __init__(self, job):
        self.job, self.stdin = job, io.StringIO()
    def poll(self):
        return None if self.job['status'] in {'running', 'waiting_confirmation', 'pausing'} else 0


class FixtureHandler(Handler):
    def do_POST(self):
        if self.path == '/_test/complete':
            data = json.loads(self.body())
            self.server.store.event(self.server.store.get(data['id']), {'type': 'completed', 'message': '界面验收完成状态，不执行真实操作。'})
            return self.send_json({'ok': True})
        if self.path == '/_test/gate':
            data = json.loads(self.body())
            job = self.server.store.get(data['id'])
            self.server.fixture_process = FakeProcess(job)
            self.server.store.processes[job['id']] = self.server.fixture_process
            self.server.store.event(job, {'type': 'step_started', 'step': 'submit'})
            self.server.store.event(job, {'type': 'confirmation', 'id': 'qa-submit-nonce', 'step': 'submit', 'title': '确认提交 OMS 入库单', 'details': ['界面验收，不连接外部网站', '只验证当前测试批次的确认交互']})
            return self.send_json({'ok': True})
        if self.path == '/_test/finish':
            data = json.loads(self.body())
            captured = self.server.fixture_process.stdin.getvalue()
            self.server.store.event(self.server.store.get(data['id']), {'type': 'paused', 'message': '本地确认交互已验证；没有执行真实平台操作。'})
            return self.send_json({'commands': captured})
        return super().do_POST()


def main():
    with tempfile.TemporaryDirectory(prefix='oms-ui-') as directory:
        server = ThreadingHTTPServer(('127.0.0.1', 0), FixtureHandler)
        server.store = Store(directory)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            env = {**os.environ, 'OMS_UI_BASE': f'http://127.0.0.1:{server.server_port}'}
            result = subprocess.run(['node', str(ROOT / 'tests/ui_smoke.mjs')], cwd=ROOT, env=env)
            raise SystemExit(result.returncode)
        finally:
            server.shutdown(); server.server_close(); thread.join()


if __name__ == '__main__':
    main()
