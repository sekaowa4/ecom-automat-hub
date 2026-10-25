"""Keep the same browser connection alive across probe, run and resume."""
import json
import os
import queue
import shutil
import subprocess
import threading
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


class BrowserBridge:
    def __init__(self, on_event):
        self.on_event = on_event
        self.process = None
        self.pending = {}
        self.lock = threading.RLock()

    def ensure(self):
        with self.lock:
            if self.process and self.process.poll() is None:
                return
            node = shutil.which('node')
            if not node:
                raise ValueError('未找到 Node.js，请先安装后重新启动工作台')
            self.process = subprocess.Popen([node, str(ROOT / 'webapp/gateway.mjs')], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', bufsize=1, cwd=ROOT, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
            threading.Thread(target=self.read, args=(self.process,), daemon=True).start()
            threading.Thread(target=lambda stream=self.process.stderr: stream.read(), daemon=True).start()

    def read(self, process):
        try:
            for line in process.stdout:
                message = json.loads(line)
                if 'reply_to' in message:
                    with self.lock:
                        waiter = self.pending.get(message['reply_to'])
                    if waiter:
                        waiter.put(message)
                elif message.get('job_id'):
                    self.on_event(message['job_id'], message['event'])
        except (ValueError, OSError):
            pass
        finally:
            with self.lock:
                if self.process is not process:
                    return
                for waiter in self.pending.values():
                    waiter.put({'error': '浏览器执行服务已停止，请重新连接；批次进度仍已保留'})
            self.on_event(None, {'type': 'failed', 'message': '浏览器执行服务已断开，请先重新连接后继续原批次。'})

    def send(self, command, **data):
        self.ensure()
        with self.lock:
            self.process.stdin.write(json.dumps({'command': command, **data}, ensure_ascii=False) + '\n')
            self.process.stdin.flush()

    def call(self, command, timeout=75, **data):
        identifier = uuid.uuid4().hex
        waiter = queue.Queue()
        with self.lock:
            self.pending[identifier] = waiter
        try:
            self.send(command, id=identifier, **data)
            try:
                result = waiter.get(timeout=timeout)
            except queue.Empty as exc:
                raise ValueError('浏览器操作超时，请检查连接；本次不会自动重复请求授权') from exc
            if result.get('error'):
                raise ValueError(result['error'])
            return result.get('result')
        finally:
            with self.lock:
                self.pending.pop(identifier, None)

    def close(self):
        if self.process and self.process.poll() is None:
            try:
                self.send('shutdown')
                self.process.stdin.close()
            except OSError:
                pass


class RunHandle:
    def __init__(self, bridge, job):
        self.bridge, self.job, self.stdin = bridge, job, self

    def poll(self):
        return None if self.job['status'] in {'running', 'pausing', 'waiting_confirmation'} and self.bridge.process and self.bridge.process.poll() is None else 0

    def write(self, line):
        self.bridge.send('control', job_id=self.job['id'], message=json.loads(line))

    def flush(self):
        pass
