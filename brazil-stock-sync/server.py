from __future__ import annotations

import cgi
import json
import mimetypes
import os
import shutil
import subprocess
import tempfile
import threading
import time
import uuid
from queue import Empty, Queue
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from inventory_pipeline import process_files, read_xlsx_rows, report_json, validate_source_files
from wecom_sync import WeComMCPClient, load_local_doc_mcp_endpoint, read_smartsheet_snapshot


ROOT = Path(__file__).resolve().parent
WEB = ROOT / "web"
RUNTIME = ROOT / "runtime"
RUNTIME.mkdir(exist_ok=True)
PORTAL_EXPORT_DIR = RUNTIME / "portal-downloads"
PUBLISHED_EXPORT_DIR = ROOT / "导出文件"
JOB_DIR = RUNTIME / "jobs"
JOB_DIR.mkdir(exist_ok=True)
WECOM_CONFIG_PATH = RUNTIME / "wecom_config.json"
WECOM_CLI_CONFIG_DIR = RUNTIME / "wecom-cli"
EXPORT_JOB_STEPS = [
    ("sales_30d", "近30天销售数据.xlsx", "upseller"),
    ("sales_7d", "近7天销售数据.xlsx", "upseller"),
    ("sales_15d", "近15天销售数据.xlsx", "upseller"),
    ("sales_3d", "近3天销售数据.xlsx", "upseller"),
    ("full_inventory", "FULL库存数据.xlsx", "upseller"),
    ("inventory", "库存数据.xlsx", "upseller"),
]
EXPORT_JOBS: dict[str, dict] = {}
EXPORT_JOBS_LOCK = threading.Lock()
EXPORT_STEP_TIMEOUT_SECONDS = 180
EXPORT_REQUIRED_HEADERS = {
    "upseller": ["SKU", "店铺", "销量"],
}


def validate_export_headers(source: str, headers: list[str]) -> dict:
    required = EXPORT_REQUIRED_HEADERS.get(source)
    if required is None:
        raise ValueError(f"未知导出类型: {source}")
    normalized = {str(header).strip() for header in headers}
    missing = [header for header in required if header not in normalized]
    return {"ok": not missing, "missing": missing}


def parse_portal_preview_output(output: str) -> dict:
    try:
        payload = json.loads(output)
    except json.JSONDecodeError as exc:
        raise ValueError("浏览器预览返回的不是有效 JSON") from exc
    if payload.get("mode") != "preview_only" or not isinstance(payload.get("portals"), list):
        raise ValueError("浏览器预览结果格式不完整")
    return payload


def build_inventory_preview(report: dict) -> dict:
    summary = report.get("summary", {})
    warnings = report.get("warnings", [])
    errors = report.get("errors", [])
    unmatched_count = sum(1 for item in warnings if item.get("type") in {"unmatched_product", "ambiguous_product"})
    unmatched = [
        {
            "type": item.get("type", "unmatched_product"),
            "row": item.get("row"),
            "product": item.get("product", ""),
        }
        for item in warnings
        if item.get("type") in {"unmatched_product", "ambiguous_product"}
    ][:100]
    return {
        "ok": not errors,
        "targetRows": summary.get("target_rows", 0),
        "matchedInventory": summary.get("target_skus_matched_in_inventory", 0),
        # Compatibility for older UI clients; this now means third-party inventory matches.
        "matchedOms": summary.get("target_skus_matched_in_inventory", 0),
        "matchedSales": summary.get("target_skus_matched_in_sales", 0),
        "unmatchedCount": unmatched_count,
        "warningCount": len(warnings),
        "errorCount": len(errors),
        "unmatched": unmatched,
        "rows": report.get("preview_rows", []),
        "priorityReplenishment": report.get("priority_replenishment", 0),
        "dataTime": report.get("generated_at", ""),
        # Unmatched products are preserved in the Stock sheet and reported for review.
        # They must not block updating rows that have an unambiguous SKU mapping.
        "canConfirm": not errors,
    }


def save_wecom_config(mcp_url: str, sheet_url: str) -> None:
    endpoint = urlparse(str(mcp_url or "").strip())
    sheet = urlparse(str(sheet_url or "").strip())
    if endpoint.scheme != "https" or endpoint.netloc != "qyapi.weixin.qq.com" or not endpoint.path.startswith("/mcp/"):
        raise ValueError("企业微信 MCP 地址必须是 qyapi.weixin.qq.com 的 HTTPS 地址")
    if sheet.scheme != "https" or sheet.netloc != "doc.weixin.qq.com" or not sheet.path.startswith("/sheet/"):
        raise ValueError("Stock 地址必须是企业微信普通在线表格链接")
    WECOM_CONFIG_PATH.write_text(json.dumps({"sheet_url": sheet_url}, ensure_ascii=False), encoding="utf-8")
    try:
        os.chmod(WECOM_CONFIG_PATH, 0o600)
    except OSError:
        pass


def wecom_status() -> dict:
    if not WECOM_CONFIG_PATH.exists():
        return {"configured": False, "endpointHint": "", "sheetUrl": ""}
    try:
        config = json.loads(WECOM_CONFIG_PATH.read_text(encoding="utf-8"))
        endpoint = urlparse(load_local_doc_mcp_endpoint(WECOM_CLI_CONFIG_DIR))
        return {
            "configured": bool(endpoint.scheme and endpoint.netloc),
            "endpointHint": f"{endpoint.scheme}://{endpoint.netloc}{endpoint.path}",
            "sheetUrl": str(config.get("sheet_url", "")),
        }
    except (OSError, json.JSONDecodeError, ValueError, PermissionError):
        return {"configured": False, "endpointHint": "", "sheetUrl": ""}


def run_wecom_smartsheet_export() -> dict:
    if not WECOM_CONFIG_PATH.exists():
        raise ValueError("请先配置企业微信 MCP 地址和 Stock 智能表格链接")
    config = json.loads(WECOM_CONFIG_PATH.read_text(encoding="utf-8"))
    sheets = read_smartsheet_snapshot(WeComMCPClient(load_local_doc_mcp_endpoint(WECOM_CLI_CONFIG_DIR)), config["sheet_url"], {"订单库存总表"})
    export_id = uuid.uuid4().hex[:8]
    source_path = RUNTIME / f"smartsheet_snapshot_{export_id}.json"
    output_name = f"Stock智能表格导出_{export_id}.xlsx"
    output_path = RUNTIME / output_name
    source_path.write_text(json.dumps(sheets, ensure_ascii=False), encoding="utf-8")
    node, env = _node_runtime()
    script = ROOT / "automation" / "export_smartsheet_snapshot.cjs"
    completed = subprocess.run([node, str(script), str(source_path), str(output_path)], cwd=ROOT, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120, check=False)
    if completed.returncode != 0 or not output_path.exists():
        detail = (completed.stderr or completed.stdout or "").strip()
        raise RuntimeError(detail or f"智能表格 Excel 生成失败（退出码 {completed.returncode}）")
    return {"workbook_url": f"/downloads/{output_name}", "sheets": len(sheets), "rows": sum(len(sheet["rows"]) for sheet in sheets)}


def local_source_manifest() -> dict[str, Path]:
    mic = Path("D:/Mic")
    desktop = Path("D:/\u684c\u9762")
    return {
        "inventory": mic / "\u5e93\u5b58\u6570\u636e.xlsx",
        "full": mic / "FULL\u5e93\u5b58\u6570\u636e.xlsx",
        "3\u5929": mic / "\u8fd13\u5929\u9500\u552e\u6570\u636e.xlsx",
        "7\u5929": mic / "\u8fd17\u5929\u9500\u552e\u6570\u636e.xlsx",
        "15\u5929": mic / "\u8fd115\u5929\u9500\u552e\u6570\u636e.xlsx",
        "30\u5929": mic / "\u8fd130\u5929\u9500\u552e\u6570\u636e.xlsx",
        "target": desktop / "Stock_Order.xlsx",
    }


def portal_source_manifest() -> dict[str, Path]:
    return {
        "full": PORTAL_EXPORT_DIR / "FULL库存数据.xlsx",
        "3天": PORTAL_EXPORT_DIR / "近3天销售数据.xlsx",
        "7天": PORTAL_EXPORT_DIR / "近7天销售数据.xlsx",
        "15天": PORTAL_EXPORT_DIR / "近15天销售数据.xlsx",
        "30天": PORTAL_EXPORT_DIR / "近30天销售数据.xlsx",
        "target": RUNTIME / "stock_order_template_fixed_layout.xlsx",
        "inventory": PORTAL_EXPORT_DIR / "库存数据.xlsx",
    }


def _portal_export_files_ready() -> bool:
    return all((PORTAL_EXPORT_DIR / filename).exists() for _, filename, _ in EXPORT_JOB_STEPS)


def is_new_export_file(path: str | Path, baseline_mtimes: dict[str, int]) -> bool:
    """Return true only when a file changed after this batch was started."""
    candidate = Path(path)
    if not candidate.exists():
        return False
    return candidate.stat().st_mtime_ns > baseline_mtimes.get(str(candidate), -1)


def snapshot_export_mtimes() -> dict[str, int]:
    return {
        str(PORTAL_EXPORT_DIR / filename): (PORTAL_EXPORT_DIR / filename).stat().st_mtime_ns
        if (PORTAL_EXPORT_DIR / filename).exists()
        else -1
        for _, filename, _ in EXPORT_JOB_STEPS
    }


def publish_export_file(source_path: str | Path, destination_dir: str | Path = PUBLISHED_EXPORT_DIR) -> Path:
    """Copy one completed portal export to the user-visible review folder, replacing its previous version."""
    source = Path(source_path)
    destination = Path(destination_dir) / source.name
    Path(destination_dir).mkdir(parents=True, exist_ok=True)
    last_error = None
    for attempt in range(5):
        try:
            shutil.copy2(source, destination)
            return destination
        except PermissionError as exc:
            last_error = exc
            if attempt == 4:
                break
            time.sleep(1)
    raise RuntimeError(f"导出文件仍被占用，复制失败：{source.name}") from last_error


def is_export_step_timed_out(last_progress: float, now: float, timeout_seconds: float = EXPORT_STEP_TIMEOUT_SECONDS) -> bool:
    return now - last_progress > timeout_seconds


def apply_export_progress_event(state: dict, event: dict) -> bool:
    """Apply one explicit completion event emitted by the browser batch script."""
    if event.get("event") != "step-complete":
        return False
    index = event.get("index")
    if not isinstance(index, int) or not 0 <= index < len(state["steps"]):
        return False
    step = state["steps"][index]
    step.update({"status": "completed", "path": str(event.get("path") or "")})
    if index + 1 < len(state["steps"]):
        state["steps"][index + 1]["status"] = "running"
        state["currentIndex"] = index + 1
    else:
        state["currentIndex"] = len(state["steps"])
    return True


def create_export_job() -> dict:
    job_id = uuid.uuid4().hex[:12]
    state = {
        "job_id": job_id,
        "status": "queued",
        "currentIndex": 0,
        "startedAt": None,
        "finishedAt": None,
        "error": None,
        "steps": [
            {"id": step_id, "filename": filename, "source": source, "status": "pending", "path": None, "error": None}
            for step_id, filename, source in EXPORT_JOB_STEPS
        ],
    }
    with EXPORT_JOBS_LOCK:
        EXPORT_JOBS[job_id] = state
    (JOB_DIR / f"{job_id}.json").write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
    return {"job_id": job_id, **state}


def get_export_job(job_id: str) -> dict | None:
    with EXPORT_JOBS_LOCK:
        state = EXPORT_JOBS.get(job_id)
        if state:
            return _normalized_export_job(state)
    persisted_path = JOB_DIR / f"{job_id}.json"
    if not persisted_path.exists():
        return None
    try:
        persisted = json.loads(persisted_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        return _failed_export_job(job_id, f"任务状态文件损坏：{exc}")
    with EXPORT_JOBS_LOCK:
        EXPORT_JOBS[job_id] = persisted
    return _normalized_export_job(persisted)


def _failed_export_job(job_id: str, error: str) -> dict:
    return {
        "job_id": job_id,
        "status": "failed",
        "currentIndex": 0,
        "startedAt": None,
        "finishedAt": time.time(),
        "error": error,
        "steps": [
            {"id": step_id, "filename": filename, "source": source, "status": "failed" if index == 0 else "pending", "path": None, "error": error if index == 0 else None}
            for index, (step_id, filename, source) in enumerate(EXPORT_JOB_STEPS)
        ],
    }


def _normalized_export_job(state: dict) -> dict:
    normalized = json.loads(json.dumps(state, ensure_ascii=False))
    started_at = normalized.get("startedAt")
    if normalized.get("status") == "running" and isinstance(started_at, (int, float)) and time.time() - started_at > EXPORT_STEP_TIMEOUT_SECONDS * 2:
        normalized["status"] = "failed"
        normalized["error"] = "任务已超时，导出进程已结束或无响应"
        normalized["finishedAt"] = time.time()
        index = min(max(int(normalized.get("currentIndex", 0)), 0), len(normalized.get("steps", [])) - 1)
        normalized["steps"][index].update({"status": "failed", "error": normalized["error"]})
        with EXPORT_JOBS_LOCK:
            EXPORT_JOBS[normalized["job_id"]] = normalized
        try:
            (JOB_DIR / f"{normalized['job_id']}.json").write_text(json.dumps(normalized, ensure_ascii=False, indent=2), encoding="utf-8")
        except OSError:
            pass
    return normalized


def _save_export_job(state: dict) -> None:
    with EXPORT_JOBS_LOCK:
        EXPORT_JOBS[state["job_id"]] = state
    (JOB_DIR / f"{state['job_id']}.json").write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")


def _node_runtime() -> tuple[str, dict]:
    node = os.environ.get("BRAZIL_STOCK_NODE")
    if not node:
        bundled = Path(r"C:\Users\USER\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe")
        node = str(bundled if bundled.exists() else "node")
    env = os.environ.copy()
    node_modules = Path(r"C:\Users\USER\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\node_modules")
    if node_modules.exists():
        env["NODE_PATH"] = str(node_modules)
    return node, env


def _run_portal_batch(job_id: str) -> None:
    state = get_export_job(job_id)
    if state is None:
        return
    state["status"] = "running"
    state["startedAt"] = time.time()
    state["steps"][0]["status"] = "running"
    _save_export_job(state)
    # 副本2改为直接调用 UpSeller 导出接口，不再启动或连接专用 Chrome。
    try:
        from upseller_api_export import run_upseller_api_exports

        files = run_upseller_api_exports(PORTAL_EXPORT_DIR)
        for index, path in enumerate(files):
            publish_export_file(path)
            state["steps"][index].update({"status": "completed", "path": str(path)})
            state["currentIndex"] = index + 1
            if index + 1 < len(state["steps"]):
                state["steps"][index + 1]["status"] = "running"
            _save_export_job(state)
        state["status"] = "completed"
    except Exception as exc:
        index = min(state.get("currentIndex", 0), len(state["steps"]) - 1)
        state["steps"][index].update({"status": "failed", "error": str(exc)})
        state["status"] = "failed"
        state["error"] = str(exc)
    state["finishedAt"] = time.time()
    _save_export_job(state)
    return

    node, env = _node_runtime()
    script = ROOT / "automation" / "batch_export.js"
    start_time = time.time()
    baseline_mtimes = snapshot_export_mtimes()
    last_completed_index = -1
    last_progress_time = start_time
    process = subprocess.Popen([node, str(script)], cwd=ROOT, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
    progress_events: Queue[dict] = Queue()
    stdout_lines: list[str] = []

    def read_progress() -> None:
        assert process.stdout is not None
        for line in process.stdout:
            stdout_lines.append(line)
            try:
                progress_events.put(json.loads(line))
            except json.JSONDecodeError:
                pass

    reader = threading.Thread(target=read_progress, daemon=True)
    reader.start()
    while process.poll() is None:
        while True:
            try:
                event = progress_events.get_nowait()
            except Empty:
                break
            if apply_export_progress_event(state, event):
                completed_index = event["index"]
                completed_path = PORTAL_EXPORT_DIR / state["steps"][completed_index]["filename"]
                if completed_path.exists():
                    publish_export_file(completed_path)
                last_completed_index = max(last_completed_index, completed_index)
                last_progress_time = time.time()
                _save_export_job(state)
        if is_export_step_timed_out(last_progress_time, time.time()):
            process.kill()
            state["error"] = f"批量导出超时：当前步骤超过 {EXPORT_STEP_TIMEOUT_SECONDS} 秒未完成"
            break
        for index, (_, filename, _) in enumerate(EXPORT_JOB_STEPS):
            path = PORTAL_EXPORT_DIR / filename
            if is_new_export_file(path, baseline_mtimes) and state["steps"][index]["status"] != "completed":
                publish_export_file(path)
                state["steps"][index].update({"status": "completed", "path": str(path)})
                last_completed_index = index
                last_progress_time = time.time()
                if index + 1 < len(state["steps"]):
                    state["steps"][index + 1]["status"] = "running"
                    state["currentIndex"] = index + 1
                _save_export_job(state)
        time.sleep(0.5)
    reader.join(timeout=2)
    while True:
        try:
            event = progress_events.get_nowait()
        except Empty:
            break
        if apply_export_progress_event(state, event):
            last_completed_index = max(last_completed_index, event["index"])
    stdout = "".join(stdout_lines)
    stderr = process.stderr.read() if process.stderr is not None else ""
    if state.get("error"):
        index = min(max(last_completed_index + 1, 0), len(state["steps"]) - 1)
        state["currentIndex"] = index
        state["steps"][index].update({"status": "failed", "error": state["error"]})
        state["status"] = "failed"
    elif process.returncode == 0:
        for index, (_, filename, _) in enumerate(EXPORT_JOB_STEPS):
            path = PORTAL_EXPORT_DIR / filename
            publish_export_file(path)
            state["steps"][index].update({"status": "completed", "path": str(path)})
        state["currentIndex"] = len(EXPORT_JOB_STEPS)
        state["status"] = "completed"
    else:
        index = min(max(last_completed_index + 1, 0), len(state["steps"]) - 1)
        state["currentIndex"] = index
        message = (stderr or stdout or f"批量导出失败（退出码 {process.returncode}）").strip()[-2000:]
        state["steps"][index].update({"status": "failed", "error": message})
        state["status"] = "failed"
        state["error"] = message
    state["finishedAt"] = time.time()
    _save_export_job(state)


def run_portal_batch(job_id: str) -> None:
    """Run an export and persist unexpected errors instead of leaving a ghost running job."""
    try:
        _run_portal_batch(job_id)
    except Exception as exc:
        state = get_export_job(job_id)
        if state is None:
            raise
        state["status"] = "failed"
        state["error"] = str(exc)
        state["finishedAt"] = time.time()
        index = min(max(int(state.get("currentIndex", 0)), 0), len(state["steps"]) - 1)
        state["steps"][index].update({"status": "failed", "error": str(exc)})
        _save_export_job(state)


def start_export_job() -> dict:
    result = create_export_job()
    thread = threading.Thread(target=run_portal_batch, args=(result["job_id"],), daemon=True)
    thread.start()
    return result


def launch_portal_login() -> dict:
    """Open the dedicated CDP Chrome profile when the workbench has no browser to attach to."""
    node, env = _node_runtime()
    script = ROOT / "automation" / "launch_portal_chrome.js"
    completed = subprocess.run(
        [node, str(script)], cwd=ROOT, env=env, capture_output=True, text=True,
        encoding="utf-8", errors="replace", timeout=15, check=False,
    )
    if completed.returncode != 0:
        raise RuntimeError((completed.stderr or completed.stdout or "启动 Chrome 失败").strip())
    return json.loads(completed.stdout)


def run_local_inventory_preview() -> dict:
    files = portal_source_manifest() if _portal_export_files_ready() else local_source_manifest()
    validation = validate_source_files(files)
    if not validation["ok"]:
        return {"ok": False, "preview": {"ok": False, "canConfirm": False, "missing": validation["missing"]}}
    _, report = process_files(files)
    return {"ok": not report.get("errors"), "preview": build_inventory_preview(report), "report": report}


def run_local_inventory_preview_copy() -> dict:
    """Build a downloadable review copy without changing the original workbook."""
    files = portal_source_manifest() if _portal_export_files_ready() else local_source_manifest()
    validation = validate_source_files(files)
    if not validation["ok"]:
        return {
            "ok": False,
            "preview": {"ok": False, "canConfirm": False, "missing": validation["missing"]},
        }
    workbook, report = process_files(files)
    preview = build_inventory_preview(report)
    if workbook is None:
        return {"ok": False, "preview": preview, "report": report}
    workbook_name = f"preview_stock_order_{uuid.uuid4().hex[:8]}.xlsx"
    (RUNTIME / workbook_name).write_bytes(workbook)
    return {
        "ok": True,
        "preview": preview,
        "report": report,
        "workbook_url": f"/downloads/{workbook_name}",
    }


def run_portal_preview() -> dict:
    node = os.environ.get("BRAZIL_STOCK_NODE")
    if not node:
        bundled = Path(r"C:\Users\USER\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe")
        node = str(bundled if bundled.exists() else "node")
    script = ROOT / "automation" / "collect_preview.js"
    env = os.environ.copy()
    node_modules = Path(r"C:\Users\USER\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\node_modules")
    if node_modules.exists():
        env["NODE_PATH"] = str(node_modules)
    completed = subprocess.run(
        [node, str(script)],
        cwd=ROOT,
        env=env,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=60,
        check=False,
    )
    if completed.returncode != 0:
        detail = str(completed.stderr or completed.stdout or "").strip()
        raise RuntimeError(detail or "未连接到 Chrome 调试端口 9222，请用指定方式启动 Chrome")
    return parse_portal_preview_output(completed.stdout)


def run_portal_export(source: str, output_name: str = "") -> dict:
    if source != "upseller":
        raise ValueError("不支持的数据源")
    node = os.environ.get("BRAZIL_STOCK_NODE")
    if not node:
        bundled = Path(r"C:\Users\USER\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe")
        node = str(bundled if bundled.exists() else "node")
    script = ROOT / "automation" / "export_portal.js"
    env = os.environ.copy()
    node_modules = Path(r"C:\Users\USER\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\node_modules")
    if node_modules.exists():
        env["NODE_PATH"] = str(node_modules)
    completed = subprocess.run(
        [node, str(script), source, output_name],
        cwd=ROOT,
        env=env,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=90,
        check=False,
    )
    if completed.returncode != 0:
        raise RuntimeError(str(completed.stderr or "").strip() or "数据导出失败")
    try:
        result = json.loads(completed.stdout)
    except json.JSONDecodeError as exc:
        raise ValueError("导出程序返回的不是有效 JSON") from exc
    export_path = Path(result.get("path", ""))
    if not export_path.exists():
        raise ValueError("导出程序没有生成文件")
    rows, sheet = read_xlsx_rows(export_path)
    header_result = validate_export_headers(source, list(rows[0]) if rows else [])
    if not header_result["ok"]:
        raise ValueError(f"{source} 导出文件缺少字段: {', '.join(header_result['missing'])}")
    result["sheet"] = sheet
    result["rows"] = len(rows)
    result["headers"] = list(rows[0]) if rows else []
    return result


class InventoryHandler(BaseHTTPRequestHandler):
    def _send_bytes(self, content: bytes, content_type: str, status: int = 200, download_name: str | None = None):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(content)))
        if download_name:
            self.send_header("Content-Disposition", f'attachment; filename="{download_name}"')
        self.end_headers()
        self.wfile.write(content)

    def _send_json(self, payload: dict, status: int = 200):
        self._send_bytes(json.dumps(payload, ensure_ascii=False).encode("utf-8"), "application/json; charset=utf-8", status)

    def do_GET(self):  # noqa: N802
        if self.path == "/api/wecom-status":
            self._send_json({"ok": True, "wecom": wecom_status()})
            return
        if self.path.startswith("/api/export-status"):
            job_id = parse_qs(urlparse(self.path).query).get("id", [""])[0]
            state = get_export_job(job_id)
            if state is None:
                self._send_json({"ok": False, "error": "找不到导出任务"}, 404)
            else:
                self._send_json({"ok": True, "job": state})
            return
        if self.path in {"/", "/index.html"}:
            self._send_bytes((WEB / "index.html").read_bytes(), "text/html; charset=utf-8")
            return
        if self.path in {"/app.js", "/styles.css"}:
            path = WEB / self.path.lstrip("/")
            content_type = "text/javascript; charset=utf-8" if path.suffix == ".js" else "text/css; charset=utf-8"
            self._send_bytes(path.read_bytes(), content_type)
            return
        if self.path.startswith("/downloads/"):
            name = Path(self.path.removeprefix("/downloads/")).name
            path = RUNTIME / name
            if path.exists():
                content_type = mimetypes.guess_type(path.name)[0] or "application/octet-stream"
                self._send_bytes(path.read_bytes(), content_type, download_name=path.name)
                return
        self._send_json({"error": "找不到页面"}, 404)

    def do_POST(self):  # noqa: N802
        if self.path == "/api/wecom-config":
            try:
                length = int(self.headers.get("Content-Length", "0"))
                body = json.loads(self.rfile.read(length) or b"{}")
                save_wecom_config(body.get("mcp_url", ""), body.get("sheet_url", ""))
                self._send_json({"ok": True, "wecom": wecom_status()})
            except Exception as exc:
                self._send_json({"ok": False, "error": str(exc)}, 400)
            return
        if self.path == "/api/wecom-export":
            try:
                self._send_json({"ok": True, "export": run_wecom_smartsheet_export()})
            except Exception as exc:
                self._send_json({"ok": False, "error": str(exc)}, 502)
            return
        if self.path == "/api/export-all":
            try:
                self._send_json({"ok": True, "job": start_export_job()})
            except Exception as exc:
                self._send_json({"ok": False, "error": str(exc)}, 502)
            return
        if self.path == "/api/launch-portal-login":
            try:
                self._send_json({"ok": True, "launch": launch_portal_login()})
            except Exception as exc:
                self._send_json({"ok": False, "error": str(exc)}, 502)
            return
        if self.path == "/api/inventory-preview":
            try:
                self._send_json(run_local_inventory_preview())
            except Exception as exc:
                self._send_json({"ok": False, "error": str(exc)}, 502)
            return
        if self.path == "/api/inventory-preview-copy":
            try:
                self._send_json(run_local_inventory_preview_copy())
            except Exception as exc:
                self._send_json({"ok": False, "error": str(exc)}, 502)
            return
        if self.path == "/api/portal-preview":
            try:
                self._send_json({"ok": True, "preview": run_portal_preview()})
            except subprocess.TimeoutExpired:
                self._send_json({"ok": False, "error": "浏览器预览超时，未执行导出或写回"}, 504)
            except Exception as exc:
                self._send_json({"ok": False, "error": str(exc)}, 502)
            return
        if self.path == "/api/export":
            try:
                length = int(self.headers.get("Content-Length", "0"))
                body = json.loads(self.rfile.read(length) or b"{}")
                self._send_json({"ok": True, "export": run_portal_export(body.get("source", ""), body.get("output_name", ""))})
            except subprocess.TimeoutExpired:
                self._send_json({"ok": False, "error": "导出超时，未生成可用文件"}, 504)
            except Exception as exc:
                self._send_json({"ok": False, "error": str(exc)}, 502)
            return
        if self.path != "/api/process":
            self._send_json({"error": "不支持的请求"}, 404)
            return
        try:
            form = cgi.FieldStorage(fp=self.rfile, headers=self.headers, environ={"REQUEST_METHOD": "POST"})
            field_names = ["inventory", "full", "3天", "7天", "15天", "30天", "target"]
            missing = [name for name in field_names if name not in form or not getattr(form[name], "filename", None)]
            if missing:
                self._send_json({"error": "缺少文件: " + ", ".join(missing)}, 400)
                return
            with tempfile.TemporaryDirectory(prefix="inventory_upload_") as temp_dir:
                paths = {}
                for name in field_names:
                    item = form[name]
                    destination = Path(temp_dir) / Path(item.filename).name
                    destination.write_bytes(item.file.read())
                    paths[name] = destination
                workbook, report = process_files(paths)
            report_name = f"validation_report_{uuid.uuid4().hex[:8]}.json"
            report_path = RUNTIME / report_name
            report_path.write_text(report_json(report), encoding="utf-8")
            if workbook is None:
                self._send_json({"ok": False, "report": report, "preview": build_inventory_preview(report), "report_url": f"/downloads/{report_name}"}, 422)
                return
            workbook_name = f"updated_stock_order_{uuid.uuid4().hex[:8]}.xlsx"
            workbook_path = RUNTIME / workbook_name
            workbook_path.write_bytes(workbook)
            self._send_json(
                {
                    "ok": True,
                    "report": report,
                    "preview": build_inventory_preview(report),
                    "workbook_url": f"/downloads/{workbook_name}",
                    "report_url": f"/downloads/{report_name}",
                }
            )
        except Exception as exc:  # surface actionable errors in the local MVP
            self._send_json({"error": str(exc)}, 500)

    def log_message(self, fmt, *args):
        print(fmt % args)


if __name__ == "__main__":
    port = 8765
    print(f"巴西库存更新平台已启动: http://127.0.0.1:{port}")
    ThreadingHTTPServer(("127.0.0.1", port), InventoryHandler).serve_forever()
