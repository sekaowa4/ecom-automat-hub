from __future__ import annotations

import json
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path
from urllib.request import ProxyHandler, Request, build_opener
from zoneinfo import ZoneInfo

from inventory_pipeline import beijing_update_label
from wecom_sync import (
    WRITEBACK_COLUMN_RANGES,
    WeComMCPClient,
    filter_online_changed_updates,
    load_local_doc_mcp_endpoint,
    read_ordinary_sheet_rows,
    tool_payload,
    verify_fixed_layout_updates,
    verify_online_updates_by_sku,
    write_ordinary_sheet_column_updates,
)


ROOT = Path(__file__).resolve().parent
RUNTIME = ROOT / "runtime"
AUTOMATION_CONFIG_PATH = RUNTIME / "automation_config.json"
BROWSER_WRITEBACK_COLUMNS = tuple(column for group in WRITEBACK_COLUMN_RANGES for column in group)


def beijing_now() -> datetime:
    return datetime.now(ZoneInfo("Asia/Shanghai"))


def format_notification(success: bool, result: dict, timestamp: str) -> str:
    if success:
        return f"今日库存已更新\n更新时间：{timestamp}\n写回 SKU：{result.get('updated_rows', 0)}"
    error = str(result.get("error") or "未知错误").strip()[-500:]
    return f"今日库存更新失败\n时间：{timestamp}\n原因：{error}"


def send_wecom_notification(webhook_url: str, text: str) -> None:
    payload = json.dumps({"msgtype": "text", "text": {"content": text}}, ensure_ascii=False).encode("utf-8")
    request = Request(webhook_url, data=payload, headers={"Content-Type": "application/json"}, method="POST")
    with build_opener(ProxyHandler({})).open(request, timeout=20) as response:
        result = json.loads(response.read().decode("utf-8"))
    if result.get("errcode") != 0:
        raise RuntimeError(str(result.get("errmsg") or "企微机器人通知失败"))


def update_online_timestamp(client, sheet_url: str, sheet_id: str, at: datetime | None = None) -> None:
    moment = at or beijing_now()
    label = f"最近更新时间：{moment.month}.{moment.day}"
    client.call_tool("sheet_update_range_data", {
        "url": sheet_url,
        "sheet_id": sheet_id,
        "grid_data": {
            "start_row": 0,
            "start_column": 0,
            "rows": [{"values": [{
                "cell_value": {"text": label},
                "data_type": "TEXT",
                "cell_format": {
                    "text_format": {
                        "font_size": 18,
                        "color": {"red": 255, "green": 0, "blue": 0, "alpha": 255},
                    },
                },
            }]}],
        },
    })


def save_audit(runtime_dir: Path, payload: dict, prefix: str = "daily_update") -> Path:
    audit_dir = runtime_dir / "audits"
    audit_dir.mkdir(parents=True, exist_ok=True)
    stamp = beijing_now().strftime("%Y%m%d_%H%M%S")
    path = audit_dir / f"{prefix}_{stamp}.json"
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    return path


def _load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def build_browser_writeback_payload(online_rows: list[dict], updates_by_sku: dict[str, dict]) -> dict:
    skus: list[str] = []
    payload_updates: dict[str, dict] = {}
    for row in online_rows:
        sku = str(row.get("SKU") or "").strip()
        if not sku:
            continue
        skus.append(sku)
        changed = updates_by_sku.get(sku, {})
        payload_updates[sku] = {column: changed.get(column, row.get(column, "")) for column in BROWSER_WRITEBACK_COLUMNS}
    unexpected = set(updates_by_sku) - set(skus)
    if unexpected:
        raise ValueError(f"online sheet is missing {len(unexpected)} writeback SKUs")
    return {"skus": skus, "updates_by_sku": payload_updates}


def filter_updates_to_online_skus(
    online_rows: list[dict], updates: dict[str, dict]
) -> tuple[dict[str, dict], list[str]]:
    online_skus = {str(row.get("SKU") or "").strip() for row in online_rows}
    skipped = sorted(str(sku).strip() for sku in set(updates) - online_skus)
    return {sku: values for sku, values in updates.items() if str(sku).strip() in online_skus}, skipped


def run_browser_writeback(runtime_dir: Path, sheet_url: str, online_rows: list[dict], updates_by_sku: dict[str, dict]) -> dict:
    import server

    request_path = runtime_dir / f"browser_writeback_{beijing_now().strftime('%Y%m%d_%H%M%S')}.json"
    result_path = request_path.with_name(f"{request_path.stem}_result.json")
    payload = build_browser_writeback_payload(online_rows, updates_by_sku)
    payload.update({"sheet_url": sheet_url, "update_label": beijing_update_label()})
    request_path.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    node, env = server._node_runtime()
    completed = subprocess.run(
        [node, str(ROOT / "automation" / "online_sheet_writeback.js"), str(request_path), str(result_path)],
        cwd=ROOT, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180, check=False,
    )
    if completed.returncode != 0 or not result_path.exists():
        raise RuntimeError((completed.stderr or completed.stdout or "browser writeback failed").strip())
    return json.loads(result_path.read_text(encoding="utf-8"))


def open_online_sheet(
    mcp_url: str,
    sheet_url: str,
    client_factory=WeComMCPClient,
    attempts: int = 3,
    delay: float = 5.0,
):
    last_error: Exception | None = None
    for attempt in range(attempts):
        client = client_factory(mcp_url)
        try:
            sheet_id, rows = read_ordinary_sheet_rows(client, sheet_url)
            return client, sheet_id, rows
        except (ConnectionError, TimeoutError, OSError) as exc:
            last_error = exc
            if attempt + 1 < attempts and delay:
                time.sleep(delay)
    raise last_error or RuntimeError("企微在线表格读取失败")


def open_online_sheet_id(mcp_url: str, sheet_url: str, client_factory=WeComMCPClient) -> tuple[WeComMCPClient, str]:
    client = client_factory(mcp_url)
    info = tool_payload(client.call_tool("sheet_get_info", {"url": sheet_url}))
    sheet = next((item for item in info.get("sheets", []) if item.get("title") == "订单库存总表"), None)
    if not sheet:
        raise ValueError("找不到工作表: 订单库存总表")
    return client, str(sheet["sheet_id"])


def verify_online_writeback(
    mcp_url: str,
    sheet_url: str,
    layout_rows: list[dict],
    updates: dict[str, dict],
    attempts: int = 3,
    delay: float = 5.0,
) -> dict:
    """Re-read the formal sheet before a success notification is allowed."""
    last_error: Exception | None = None
    for attempt in range(attempts):
        _, _, online_rows = open_online_sheet(mcp_url, sheet_url, attempts=1, delay=0)
        try:
            return verify_fixed_layout_updates(online_rows, layout_rows, updates)
        except RuntimeError as exc:
            last_error = exc
            if attempt + 1 < attempts and delay:
                time.sleep(delay)
    raise last_error or RuntimeError("在线表格回读核对失败")


def write_online_updates_by_sku(
    client,
    sheet_url: str,
    sheet_id: str,
    online_rows: list[dict],
    updates: dict[str, dict],
    force_all: bool = False,
) -> dict[str, int]:
    """Write only desired SKU rows, using current online row positions."""
    changed = filter_online_changed_updates(online_rows, updates)
    selected = updates if force_all else changed
    updated_skus: set[str] = set()
    written_cells = requests = 0
    for columns in WRITEBACK_COLUMN_RANGES:
        selected_skus = {
            sku for sku, values in selected.items()
            if force_all or any(column in values for column in columns)
        }
        if not selected_skus:
            continue
        rows = [row for row in online_rows if str(row.get("SKU") or "").strip() in selected_skus]
        result = write_ordinary_sheet_column_updates(client, sheet_url, sheet_id, rows, updates, columns)
        updated_skus.update(str(row.get("SKU") or "").strip() for row in rows)
        written_cells += result["written_cells"]
        requests += result["requests"]
    return {"updated_rows": len(updated_skus), "ranges": len(WRITEBACK_COLUMN_RANGES), "written_cells": written_cells, "requests": requests}


def verify_online_writeback_by_sku(
    mcp_url: str,
    sheet_url: str,
    updates: dict[str, dict],
    attempts: int = 3,
    delay: float = 5.0,
) -> dict:
    last_error: Exception | None = None
    for attempt in range(attempts):
        _, _, online_rows = open_online_sheet(mcp_url, sheet_url, attempts=1, delay=0)
        try:
            return verify_online_updates_by_sku(online_rows, updates)
        except RuntimeError as exc:
            last_error = exc
            if attempt + 1 < attempts and delay:
                time.sleep(delay)
    raise last_error or RuntimeError("在线表格回读核对失败")


def run_mcp_authorization_preflight(
    runtime_dir: Path = RUNTIME,
    automation_config_path: Path = AUTOMATION_CONFIG_PATH,
    wecom_config_path: Path | None = None,
) -> dict:
    import server

    wecom_config_path = wecom_config_path or server.WECOM_CONFIG_PATH
    automation_config = _load_json(automation_config_path)
    wecom_config = _load_json(wecom_config_path)
    timestamp = beijing_now().strftime("%Y-%m-%d %H:%M")

    try:
        _, _, rows = open_online_sheet(
            load_local_doc_mcp_endpoint(ROOT / "runtime" / "wecom-cli"),
            str(wecom_config["sheet_url"]),
            attempts=1,
            delay=0,
        )
        result = {
            "status": "available",
            "checked_rows": len(rows),
            "checked_at": timestamp,
        }
        result["audit_path"] = str(save_audit(runtime_dir, result, prefix="mcp_preflight"))
        return result
    except Exception as exc:
        failure = {"status": "failed", "error": str(exc), "checked_at": timestamp}
        failure["notification_status"] = "suppressed"
        save_audit(runtime_dir, failure, prefix="mcp_preflight")
        raise


def run_daily_update(
    runtime_dir: Path = RUNTIME,
    automation_config_path: Path = AUTOMATION_CONFIG_PATH,
    wecom_config_path: Path | None = None,
) -> dict:
    import server

    wecom_config_path = wecom_config_path or server.WECOM_CONFIG_PATH
    automation_config = _load_json(automation_config_path)
    wecom_config = _load_json(wecom_config_path)
    webhook_url = str(automation_config.get("webhook_url") or "").strip()
    if not webhook_url.startswith("https://qyapi.weixin.qq.com/cgi-bin/webhook/send?"):
        raise ValueError("企微群机器人 Webhook 未配置或格式错误")
    timestamp = beijing_now().strftime("%Y-%m-%d %H:%M")

    try:
        job_id = server.create_export_job()["job_id"]
        server.run_portal_batch(job_id)
        job = server.get_export_job(job_id) or {}
        if job.get("status") != "completed":
            raise RuntimeError(str(job.get("error") or "门户批量导出失败"))

        workbook, report = server.process_files(server.portal_source_manifest(), include_writeback_updates=True)
        if workbook is None or report.get("errors"):
            raise RuntimeError("；".join(str(error) for error in report.get("errors", [])) or "库存文件生成失败")
        updates = report.get("writeback_all_updates") or {}
        if not updates:
            raise RuntimeError("库存结果没有可写回的 SKU 数据")

        workbook_path = runtime_dir / "本次生成Stock表.xlsx"
        workbook_path.write_bytes(workbook)

        sheet_url = str(wecom_config["sheet_url"])
        mcp_url = load_local_doc_mcp_endpoint(ROOT / "runtime" / "wecom-cli")
        client, sheet_id, online_rows = open_online_sheet(mcp_url, sheet_url)
        online_updates, skipped_skus = filter_updates_to_online_skus(online_rows, updates)
        if not online_updates:
            raise RuntimeError("正式在线表没有可写回的 SKU")
        write_result = write_online_updates_by_sku(client, sheet_url, sheet_id, online_rows, online_updates)
        verification = verify_online_writeback_by_sku(mcp_url, sheet_url, online_updates)
        update_online_timestamp(client, sheet_url, sheet_id, beijing_now())
        timestamp = beijing_now().strftime("%Y-%m-%d %H:%M")
        result = {
            "status": "completed",
            "updated_rows": write_result["updated_rows"],
            "ranges": write_result["ranges"],
            "workbook_path": str(workbook_path),
            "job_id": job_id,
            "finished_at": timestamp,
            "skipped_skus": skipped_skus,
            **verification,
        }
        result["audit_path"] = str(save_audit(runtime_dir, result))
        send_wecom_notification(webhook_url, format_notification(True, result, timestamp))
        return result
    except Exception as exc:
        timestamp = beijing_now().strftime("%Y-%m-%d %H:%M")
        failure = {"status": "failed", "error": str(exc), "finished_at": timestamp}
        failure["notification_status"] = "suppressed"
        save_audit(runtime_dir, failure)
        raise


if __name__ == "__main__":
    try:
        run_daily_update()
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        sys.exit(1)
    sys.exit(0)
