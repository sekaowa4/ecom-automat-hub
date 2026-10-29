"""Loopback-only file and workflow service for the shipping workbench."""

import argparse
import hashlib
import json
import mimetypes
import os
import re
import shutil
import threading
import uuid
import webbrowser
from datetime import datetime
from decimal import Decimal
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, unquote, urlparse
from zipfile import BadZipFile
from xml.etree.ElementTree import ParseError

from .manifest import parse_manifest
from .bridge import BrowserBridge, RunHandle
from .config import load_config
from .wecom import call_tool, document_url, read_document, match_items, match_prices, download_image

ROOT = Path(__file__).resolve().parent.parent
STEP_NAMES = [("catalog", "核对常用账号商品"), ("products", "新品录入并保存"), ("measurements", "更新 OMS 尺寸重量"), ("inbound", "填写入库单"), ("submit", "提交入库单"), ("ship", "入库单发货"), ("invoice_order", "创建开票手工订单"), ("tax", "填写 NCM 与原产地"), ("issue", "开具发票"), ("documents", "下载 PDF 与 XML"), ("attachments", "回传 OMS 附件")]
GATES = {"submit", "ship", "issue", "attachments"}


def now():
    return datetime.now().astimezone().isoformat(timespec="seconds")


def completed_history(batch_name):
    filename = ROOT / 'webapp/completed_batches.json'
    return json.loads(filename.read_text(encoding='utf-8')).get(batch_name) if filename.exists() else None


def within(root, relative):
    target = (Path(root) / relative).resolve()
    if not target.is_relative_to(Path(root).resolve()) or target == Path(root).resolve():
        raise ValueError("不允许访问此路径")
    return target


def atomic_json(path, value):
    temporary = Path(path).with_suffix(".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(path)


class Store:
    def __init__(self, directory):
        self.directory = Path(directory).resolve()
        self.directory.mkdir(parents=True, exist_ok=True)
        self.lock = threading.RLock()
        self.jobs = {}
        self.processes = {}
        self.wecom = {**load_config().get('wecom', {}), 'enabled': False, 'ok': False, 'message': '请连接企微后匹配产品图与尺寸重量'}
        wecom_file = self.directory / 'wecom.json'
        if wecom_file.exists():
            self.wecom.update(json.loads(wecom_file.read_text(encoding='utf-8')))
        self.wecom['ok'] = False
        self.bridge = BrowserBridge(self.browser_event)
        self.browser = {"ok": False, "pages": [], "message": "点击连接浏览器，读取已经登录的标签页"}
        self.settings = {"common_page": None, "invoice_page": None, "oms_page": None}
        settings_file = self.directory / "settings.json"
        if settings_file.exists():
            self.settings.update(json.loads(settings_file.read_text(encoding="utf-8")))
        for state in self.directory.glob("*/state.json"):
            try:
                job = json.loads(state.read_text(encoding="utf-8"))
                if job['status'] == 'matching':
                    job['status'] = 'invalid'
                    job['manifest']['errors'] = ['匹配过程中工作台重启，请重新匹配企微资料']
                if job["status"] in {"running", "pausing", "waiting_confirmation"}:
                    job["status"] = "paused"
                    job["gate"] = None
                    job["logs"].append({"time": now(), "message": "工作台重新启动，已保留进度。继续前会核对上一步结果。", "level": "info"})
                self.jobs[job["id"]] = job
            except (ValueError, KeyError, OSError):
                continue

    def save(self, job):
        job["updated_at"] = now()
        atomic_json(self.directory / job["id"] / "state.json", job)

    def get(self, identifier):
        if identifier not in self.jobs:
            raise ValueError("批次不存在")
        return self.jobs[identifier]

    def browser_event(self, identifier, event):
        with self.lock:
            if identifier in self.jobs:
                self.event(self.jobs[identifier], event)
            elif identifier is None:
                self.browser["ok"] = False
                for job in self.jobs.values():
                    if job["status"] in {"running", "pausing", "waiting_confirmation"}:
                        self.event(job, event)

    def upload(self, filename, data):
        filename = re.split(r"[/\\]", filename)[-1]
        if not filename or Path(filename).suffix.lower() not in {".xlsx", ".csv"} or re.search(r'[<>:"|?*\x00-\x1f]', filename):
            raise ValueError("清单文件名无效，请使用 .xlsx 或 .csv")
        digest = hashlib.sha256(data).hexdigest()
        with self.lock:
            previous = next((j for j in self.jobs.values() if j["source_hash"] == digest), None)
            if previous:
                requested_price = load_config().get('batch_price_sheets', {}).get(filename)
                if previous['source_name'] != filename or (requested_price and requested_price != previous.get('price_sheet')):
                    raise ValueError('相同文件内容已用于已有批次，但文件名或单价子表绑定不同。请核对原批次，不能静默复用旧价格或重复建单')
                return previous
            identifier = uuid.uuid4().hex
            directory = self.directory / identifier
            (directory / "source").mkdir(parents=True)
            (directory / "artifacts").mkdir()
            source = directory / "source" / filename
            source.write_bytes(data)
            try:
                manifest = parse_manifest(source, directory / "images", external_data=True, external_price=True)
            except (BadZipFile, ParseError, KeyError, IndexError, UnicodeDecodeError, RuntimeError) as exc:
                shutil.rmtree(directory)
                raise ValueError("无法读取清单：文件可能已损坏、加密，或不是真正的 XLSX/CSV 文件") from exc
            except Exception:
                shutil.rmtree(directory)
                raise
            job = {"id": identifier, "source_name": filename, "source_hash": digest, "created_at": now(), "updated_at": now(), "status": "invalid" if manifest["errors"] else "ready", "manifest": manifest, "steps": [{"id": key, "label": label, "status": "pending"} for key, label in STEP_NAMES], "logs": [{"time": now(), "message": f"已读取 {manifest['totals']['source_rows']} 行，合并为 {manifest['totals']['skus']} 个 SKU。", "level": "info"}], "records": {}, "gate": None, "inflight": None, "files": []}
            job['price_sheet'] = load_config().get('batch_price_sheets', {}).get(filename, '')
            history = completed_history(manifest['batch_name'])
            job['history_protected'] = bool(history)
            if history:
                sku_hash = hashlib.sha256('\n'.join(sorted(i['sku'] for i in manifest['items'])).encode()).hexdigest()
                if not manifest["errors"] and sku_hash == history["sku_hash"] and all(manifest["totals"][key] == history[key] for key in ["skus", "quantity"]):
                    job["status"] = "completed"
                    manifest['totals']['amount'] = history['amount']
                    job["records"] = history["records"]
                    for step in job["steps"]:
                        step.update(status="done", message="沿用项目已完成的历史记录")
                    job["logs"].append({"time": now(), "message": f"此批次已于 {history['completed_at']} 完成，沿用既有单号，不重复创建。历史文件仍保存在原下载目录。", "level": "info"})
                else:
                    manifest["errors"].append("该批次已有完成记录，但上传资料与历史总数或金额不同，请核对批次名称及原单据")
                    job["status"] = "invalid"
            self.jobs[identifier] = job
            self.save(job)
            if self.wecom.get('enabled') and job['status'] != 'completed' and not history:
                self.match_wecom(identifier)
            return job

    def connect_wecom(self, settings):
        image_url = document_url(settings.get('images_url', ''))
        size_url = document_url(settings.get('sizes_url', ''))
        for url, title in [(image_url, '图包作图需求'), (size_url, '箱规汇总表')]:
            info = call_tool('sheet_get_info', {'url': url})
            if not any(s.get('title') == title for s in info.get('sheets', [])):
                raise ValueError(f'企微链接没有子表：{title}')
        price_sheets = [s['title'] for s in info.get('sheets', []) if s.get('title') != '箱规汇总表']
        with self.lock:
            self.wecom = {'images_url': image_url, 'sizes_url': size_url, 'price_sheets': price_sheets, 'enabled': True, 'ok': True, 'message': '企微已连接（只读）；上传后匹配产品资料及对应批次 E 列单价'}
            atomic_json(self.directory / 'wecom.json', self.wecom)
        return self.wecom

    def match_wecom(self, identifier, price_sheet=None):
        with self.lock:
            job = self.get(identifier)
            if not self.wecom.get('enabled'):
                raise ValueError('请先在连接设置中连接企微')
            if job.get('history_protected') or completed_history(job['manifest']['batch_name']):
                raise ValueError('该批次已有历史完成记录，禁止重新匹配或重复建单')
            if job['status'] in {'completed', 'running', 'matching', 'pausing', 'waiting_confirmation'} or job['inflight'] or job['records'] or any(s['status'] == 'done' for s in job['steps']):
                raise ValueError('仅允许对尚未执行业务操作的批次匹配资料，不能改动已有单据的批次')
            settings = dict(self.wecom)
            selection = job.get('price_sheet') if price_sheet is None else str(price_sheet).strip()
            if selection and selection not in self.wecom.get('price_sheets', []):
                raise ValueError('所选单价子表不在当前正清表中，请重新连接企微')
            settings['price_sheet'] = selection or None
            job['price_sheet'] = selection or ''
            job['status'] = 'matching'
            job['manifest']['errors'] = ['正在读取企微资料，匹配完成前不能开始']
            self.save(job)
        threading.Thread(target=self._match_wecom, args=(identifier, settings), daemon=True).start()
        return job

    def _match_wecom(self, identifier, settings):
        directory = self.directory / identifier
        job = self.get(identifier)
        try:
            manifest = parse_manifest(directory / 'source' / job['source_name'], directory / 'images', external_data=True, external_price=True)
            images = read_document(settings['images_url'])
            sizes = read_document(settings['sizes_url'])
            manifest['items'], price_sheet = match_prices(manifest['items'], sizes, settings.get('price_sheet'))
            amount = sum((Decimal(i['unit_price']) * i['quantity'] for i in manifest['items']), Decimal(0))
            if amount * 100 > 9007199254740991:
                raise ValueError('对应批次开票总额超过精确计算范围')
            manifest['totals']['amount'] = str(amount.quantize(Decimal('0.01')))
            manifest['items'], errors = match_items(manifest['items'], images, sizes)
            image_issues = {item.get('image_issue') for item in manifest['items']}
            manifest['errors'].extend(e for e in errors if e not in image_issues)
            manifest['warnings'].extend(e + '；若为新品需补齐，补货不重新上传图片' for e in errors if e in image_issues)
            cache = {}
            for item in manifest['items']:
                url = item.get('image_url')
                if url:
                    try:
                        if url not in cache:
                            cache[url] = download_image(url, directory / 'images')
                        item['image'] = cache[url]
                    except ValueError:
                        item['image_issue'] = f"{item['sku']} 企微产品图下载失败"
                        manifest['warnings'].append(item['image_issue'] + '；若为新品需补齐，补货不重新上传图片')
            manifest['external_source'] = {'kind': 'wecom', 'matched_at': now(), 'images_sheet': '图包作图需求', 'images_column': 'N', 'sizes_sheet': '箱规汇总表', 'sizes_columns': 'AA:AD', 'weight_unit': 'kg → g', 'price_sheet': price_sheet, 'price_column': 'E'}
            # Persist only the relevant matched snapshot; never rewrite the source files.
            with self.lock:
                job['manifest'] = manifest
                job['price_sheet'] = price_sheet
                job['status'] = 'invalid' if manifest['errors'] else 'ready'
                job['logs'].append({'time': now(), 'level': 'info', 'message': f"企微资料匹配完成，{len(manifest['errors'])} 项问题；原清单未修改。"})
                self.save(job)
        except Exception as exc:
            with self.lock:
                job['status'] = 'invalid'
                job['manifest']['errors'] = [str(exc) if isinstance(exc, ValueError) else '企微匹配失败，请检查连接后重试']
                self.save(job)

    def event(self, job, event):
        with self.lock:
            kind = event.get("type")
            if kind == "log":
                job["logs"].append({"time": now(), "message": str(event.get("message", ""))[:3000], "level": event.get("level", "info")})
                job["logs"] = job["logs"][-500:]
            elif kind in {"step_started", "step_done"}:
                step = next(s for s in job["steps"] if s["id"] == event["step"])
                step["status"] = "running" if kind == "step_started" else "done"
                step["message"] = event.get("message", "")
                if kind == "step_done":
                    step["completed_at"] = now()
            elif kind == "records":
                job["records"].update(event.get("data", {}))
            elif kind == "inflight":
                job["inflight"] = event.get("action")
            elif kind == "confirmation":
                if event.get("step") not in GATES:
                    raise ValueError("无效的确认步骤")
                job["gate"] = {"id": event["id"], "step": event["step"], "title": event["title"], "details": event.get("details", []), "requested_at": now()}
                job["status"] = "waiting_confirmation"
            elif kind == "files":
                job["files"] = event["files"]
            elif kind in {"paused", "completed", "failed"}:
                job["status"] = {"paused": "paused", "completed": "completed", "failed": "blocked"}[kind]
                job["gate"] = None
                if event.get("message"):
                    job["logs"].append({"time": now(), "message": event["message"], "level": "error" if kind == "failed" else "info"})
                if kind == "failed":
                    active = next((s for s in job["steps"] if s["status"] == "running"), None)
                    if active:
                        active["status"] = "blocked"
                        active["message"] = event.get("message", "")
            self.save(job)

    def connect_browser(self):
        browser = self.bridge.call("probe")
        if not browser.get("ok"):
            browser.setdefault("pages", [])
        self.browser = browser
        return browser

    def set_settings(self, settings):
        if any(p.poll() is None for p in self.processes.values()):
            raise ValueError("请先暂停当前批次，再变更账号连接")
        pages = {p["id"]: p for p in self.browser.get("pages", [])}
        desired = {key: settings.get(key) for key in ["common_page", "invoice_page", "oms_page"]}
        for key, site in [("common_page", "upseller"), ("invoice_page", "upseller"), ("oms_page", "oms")]:
            value = desired[key]
            if type(value) is not int or value not in pages or pages[value]["site"] != site:
                raise ValueError("请选择已经连接且属于相应网站的标签页")
            if pages[value].get("login_required"):
                raise ValueError("选中的标签页尚未完成登录验证，请先在原浏览器登录后重新读取标签页")
        if desired["common_page"] == desired["invoice_page"]:
            raise ValueError("常用与开票账号需要选择不同的标签页，并确认账户各自正确")
        common, invoice = pages[desired["common_page"]], pages[desired["invoice_page"]]
        if common.get("account") and common.get("account") == invoice.get("account"):
            raise ValueError("两个 UpSeller 标签页显示为同一账号，请选择已登录开票专用账号的另一个窗口")
        self.settings.update(desired)
        self.settings["bindings"] = {key: pages[value] for key, value in desired.items()}
        atomic_json(self.directory / "settings.json", self.settings)
        return self.settings

    def start(self, identifier):
        with self.lock:
            job = self.get(identifier)
            if job.get('history_protected') or completed_history(job['manifest']['batch_name']):
                raise ValueError('该批次已有历史完成记录，禁止重复执行')
            if job["status"] in {"completed", "invalid"} or job["manifest"]["errors"]:
                raise ValueError("该批次已完成或校验未通过")
            if any(p.poll() is None for p in self.processes.values()):
                raise ValueError("已有批次正在使用浏览器，请先暂停或完成它")
            if not self.browser.get("ok") or not all(self.settings.get(k) is not None for k in ["common_page", "invoice_page", "oms_page"]):
                raise ValueError("请先连接浏览器并指定三个账号标签页")
            if not job['manifest'].get('external_source') or job['manifest']['external_source'].get('kind') != 'wecom' or not job['manifest']['external_source'].get('price_sheet'):
                raise ValueError('请先连接企微并匹配本批产品图、尺寸重量及对应子表E列单价。旧版已执行批次须先人工核对原单据，不能自动迁移或回退清单数据')
            same_batch = next((j for j in self.jobs.values() if j["id"] != identifier and j["manifest"]["batch_name"] == job["manifest"]["batch_name"] and (j["inflight"] or j["records"].get("saved_new_skus") or any(j["records"].get(k) for k in ["inbound_no", "manual_order_no", "invoice_no"]) or any(s["id"] == "measurements" and s["status"] == "done" for s in j["steps"]))), None)
            if same_batch:
                raise ValueError("同一批次已有执行记录，请从历史记录继续原批次，避免重复建单")
            directory = self.directory / identifier
            payload = {"id": identifier, "job_dir": str(directory), "source": str(directory / "source" / job["source_name"]), "manifest": job["manifest"], "settings": self.settings, "records": job["records"], "inflight": job["inflight"], "completed_steps": [s["id"] for s in job["steps"] if s["status"] == "done"], "python": os.sys.executable}
            atomic_json(directory / "run.json", payload)
            self.processes[identifier] = RunHandle(self.bridge, job)
            job["status"] = "running"
            job["gate"] = None
            self.save(job)
        try:
            self.bridge.call("run", timeout=10, input=payload)
            return job
        except ValueError as exc:
            self.event(job, {"type": "failed", "message": str(exc)})
            raise

    def approve(self, identifier, nonce):
        with self.lock:
            job = self.get(identifier)
            process = self.processes.get(identifier)
            if job["status"] != "waiting_confirmation" or not job.get("gate") or job["gate"]["id"] != nonce or not process or process.poll() is not None:
                raise ValueError("这条确认已过期，请刷新当前批次")
            job["logs"].append({"time": now(), "message": f"已确认：{job['gate']['title']}", "level": "info"})
            self.save(job)
            process.stdin.write(json.dumps({"type": "approve", "id": nonce}) + "\n")
            process.stdin.flush()
            job["status"] = "running"
            job["gate"] = None
            self.save(job)
            return job

    def pause(self, identifier):
        with self.lock:
            job = self.get(identifier)
            process = self.processes.get(identifier)
            if not process or process.poll() is not None:
                raise ValueError("此批次当前没有运行")
            process.stdin.write('{"type":"pause"}\n')
            process.stdin.flush()
            job["status"] = "pausing"
            self.save(job)
            return job


class Handler(BaseHTTPRequestHandler):
    server_version = "OMSWorkbench/1.0"

    def log_message(self, *_):
        pass

    def send_json(self, data, status=200):
        with self.server.store.lock:
            payload = json.dumps(data, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def body(self, limit=1_000_000):
        length = int(self.headers.get("Content-Length", "0"))
        if length <= 0 or length > limit:
            raise ValueError(f"请求大小无效，上限为 {limit // 1_000_000} MB")
        return self.rfile.read(length)

    def same_origin(self):
        host = self.headers.get("Host", "")
        port = self.server.server_port
        allowed = {f"127.0.0.1:{port}", f"localhost:{port}"}
        if host not in allowed:
            raise ValueError("工作台只接受本机地址访问")
        origin = self.headers.get("Origin")
        if origin and origin not in {"http://" + a for a in allowed}:
            raise ValueError("拒绝来自其他网站的操作请求")

    def do_GET(self):
        try:
            self.same_origin()
            path = unquote(urlparse(self.path).path)
            store = self.server.store
            if path == "/api/health":
                return self.send_json({"app": "oms-sea-workbench", "ok": True})
            if path == "/api/state":
                with store.lock:
                    return self.send_json({"jobs": sorted(store.jobs.values(), key=lambda j: j["created_at"], reverse=True), "settings": store.settings, "browser": store.browser, "wecom": store.wecom, "steps": STEP_NAMES})
            match = re.fullmatch(r"/api/jobs/([a-f0-9]{32})(?:/(.*))?", path)
            if match:
                job = store.get(match[1])
                if not match[2]:
                    return self.send_json(job)
                if match[2] == "preview.csv":
                    import csv
                    import io
                    output = io.StringIO()
                    writer = csv.writer(output)
                    writer.writerow(["SKU", "葡语名称", "件数", "开票单价(BRL)", "长(cm)", "宽(cm)", "高(cm)", "重量(g)"])
                    writer.writerows([i["sku"], i["name_pt"], i["quantity"], i["unit_price"], i["length_cm"], i["width_cm"], i["height_cm"], i["weight_g"]] for i in job["manifest"]["items"])
                    content = output.getvalue().encode("utf-8-sig")
                    self.send_response(200)
                    self.send_header("Content-Type", "text/csv; charset=utf-8")
                    self.send_header("Content-Disposition", 'attachment; filename="manifest-preview.csv"')
                    self.send_header("Content-Length", str(len(content)))
                    self.end_headers()
                    self.wfile.write(content)
                    return
                if not match[2].startswith(("images/", "artifacts/", "source/")):
                    raise ValueError("文件不可访问")
                return self.send_file(within(store.directory / match[1], match[2]), attachment=not match[2].startswith("images/"))
            if path.startswith("/api/"):
                return self.send_json({"error": "接口不存在"}, 404)
            target = within(ROOT / "web/dist", path.lstrip("/") or "index.html")
            if not target.is_file():
                target = ROOT / "web/dist/index.html"
            return self.send_file(target)
        except (ValueError, OSError) as exc:
            self.send_json({"error": str(exc)}, 400)

    def send_file(self, path, attachment=False):
        if not path.is_file():
            return self.send_json({"error": "文件不存在，请先完成对应步骤"}, 404)
        self.send_response(200)
        self.send_header("Content-Type", mimetypes.guess_type(path.name)[0] or "application/octet-stream")
        self.send_header("Content-Length", str(path.stat().st_size))
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Cache-Control", "no-store")
        if attachment:
            from urllib.parse import quote
            self.send_header("Content-Disposition", "attachment; filename*=UTF-8''" + quote(path.name))
        self.end_headers()
        with path.open("rb") as source:
            shutil.copyfileobj(source, self.wfile)

    def do_POST(self):
        try:
            self.same_origin()
            parsed = urlparse(self.path)
            store = self.server.store
            if parsed.path == "/api/jobs":
                filename = parse_qs(parsed.query).get("filename", [""])[0]
                return self.send_json(store.upload(filename, self.body(50_000_000)), 201)
            body = json.loads(self.body())
            if parsed.path == "/api/browser/connect":
                return self.send_json(store.connect_browser())
            if parsed.path == "/api/settings":
                return self.send_json(store.set_settings(body))
            if parsed.path == '/api/wecom/connect':
                return self.send_json(store.connect_wecom(body))
            match = re.fullmatch(r"/api/jobs/([a-f0-9]{32})/(start|approve|pause|references|match-wecom)", parsed.path)
            if not match:
                return self.send_json({"error": "接口不存在"}, 404)
            identifier, action = match.groups()
            if action == 'match-wecom':
                return self.send_json(store.match_wecom(identifier, body.get('price_sheet')))
            if action == "start":
                return self.send_json(store.start(identifier))
            if action == "approve":
                return self.send_json(store.approve(identifier, body.get("id")))
            if action == "pause":
                return self.send_json(store.pause(identifier))
            job = store.get(identifier)
            if identifier in store.processes and store.processes[identifier].poll() is None:
                raise ValueError("先暂停当前运行，再补充单号")
            with store.lock:
                for key, pattern in [("inbound_no", r"IN[A-Za-z0-9]+"), ("manual_order_no", r"UP[A-Za-z0-9]+"), ("invoice_no", r"\d{1,9}")]:
                    value = str(body.get(key, "")).strip().lstrip("#")
                    if value:
                        if not re.fullmatch(pattern, value):
                            raise ValueError("单号格式不正确")
                        job["records"][key] = value
                store.save(job)
            return self.send_json(job)
        except (ValueError, OSError) as exc:
            self.send_json({"error": str(exc)}, 400)


def main():
    parser = argparse.ArgumentParser(description="启动 OMS 海运网页工作台")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--no-open", action="store_true")
    args = parser.parse_args()
    if not (ROOT / "web/dist/index.html").exists():
        raise SystemExit("网页还未构建，请先运行 npm install 和 npm run build，或双击启动海运工作台.cmd")
    try:
        server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    except OSError:
        from urllib.request import urlopen
        try:
            with urlopen(f"http://127.0.0.1:{args.port}/api/health", timeout=2) as response:
                if json.load(response).get("app") != "oms-sea-workbench":
                    raise ValueError("端口被其他程序占用")
            if not args.no_open:
                webbrowser.open(f"http://127.0.0.1:{args.port}")
            print("海运工作台已经运行，已复用当前服务。")
            return
        except Exception:
            raise SystemExit(f"端口 {args.port} 被占用，请关闭占用程序后重新启动")
    server.store = Store(ROOT / ".workbench")
    print(f"海运工作台：http://127.0.0.1:{args.port}", flush=True)
    if not args.no_open:
        webbrowser.open(f"http://127.0.0.1:{args.port}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        for process in server.store.processes.values():
            if process.poll() is None:
                try:
                    process.stdin.write('{"type":"pause"}\n')
                    process.stdin.flush()
                except OSError:
                    pass
        server.store.bridge.close()
        server.server_close()


if __name__ == "__main__":
    main()
