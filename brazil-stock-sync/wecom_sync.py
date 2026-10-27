"""Safe, transport-independent helpers for WeCom smart-sheet writeback."""

from __future__ import annotations

import base64
import json
import re
import threading
import time
from pathlib import Path
from typing import Any, Iterable
from urllib.error import URLError
from urllib.request import ProxyHandler, Request, build_opener

from cryptography.hazmat.primitives.ciphers.aead import AESGCM


def load_local_doc_mcp_endpoint(config_dir: Path) -> str:
    """Load the authorized document MCP endpoint from the CLI's encrypted cache."""
    key = base64.b64decode((config_dir / ".encryption_key").read_text(encoding="utf-8").strip())
    encrypted = (config_dir / "mcp_config.enc").read_bytes()
    if len(encrypted) < 28:
        raise ValueError("企业微信 MCP 配置文件无效")
    configs = json.loads(AESGCM(key).decrypt(encrypted[:12], encrypted[12:], None))
    endpoint = next((item.get("url") for item in configs if item.get("biz_type") == "doc" and item.get("is_authed")), None)
    if not endpoint:
        raise PermissionError("企业微信文档 MCP 未授权")
    return str(endpoint)


def decode_mcp_response(raw: str) -> dict[str, Any]:
    """Return the JSON-RPC result from JSON or a Streamable HTTP event stream."""
    content = raw.strip()
    if content.startswith("data:") or "\ndata:" in content:
        data_lines = [line.removeprefix("data:").strip() for line in content.splitlines() if line.startswith("data:")]
        content = data_lines[-1] if data_lines else ""
    payload = json.loads(content)
    if "error" in payload:
        raise RuntimeError(str(payload["error"]))
    return payload.get("result", {})


class WeComMCPClient:
    """Minimal Streamable HTTP MCP client for the WeCom document robot."""

    # Large fixed-layout batches can take longer than the normal document
    # reads. Keep the retry boundary above the observed MCP response time.
    RESPONSE_READ_TIMEOUT_SECONDS = 90

    def __init__(self, endpoint: str):
        self.endpoint = endpoint
        self._opener = build_opener(ProxyHandler({}))
        self.session_id = ""
        self._request_id = 0
        self._initialized = False

    @staticmethod
    def _response_content(response: Any) -> str:
        # urllib's timeout only covers opening the connection.  MCP replies are
        # streamed, so apply the same bound while waiting for the first data line.
        try:
            response.fp.raw._sock.settimeout(WeComMCPClient.RESPONSE_READ_TIMEOUT_SECONDS)
        except AttributeError:
            pass
        result: list[str] = []
        errors: list[BaseException] = []
        finished = threading.Event()

        def read_stream() -> None:
            try:
                lines: list[str] = []
                while raw_line := response.readline():
                    line = raw_line.decode("utf-8")
                    if line.startswith("data:"):
                        result.append(line.removeprefix("data:").strip())
                        return
                    lines.append(line)
                result.append("".join(lines).strip())
            except BaseException as error:
                errors.append(error)
            finally:
                finished.set()

        threading.Thread(target=read_stream, daemon=True).start()
        if not finished.wait(WeComMCPClient.RESPONSE_READ_TIMEOUT_SECONDS):
            response.close()
            raise TimeoutError("企微 MCP 响应超时（30 秒未返回数据）")
        if errors:
            raise errors[0]
        return result[0] if result else ""

    def _post(self, method: str, params: dict[str, Any] | None = None, notification: bool = False) -> dict[str, Any]:
        self._request_id += 1
        payload: dict[str, Any] = {"jsonrpc": "2.0", "method": method}
        if not notification:
            payload["id"] = self._request_id
        if params is not None:
            payload["params"] = params
        headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
        if self.session_id:
            headers["Mcp-Session-Id"] = self.session_id
        request = Request(self.endpoint, data=json.dumps(payload).encode("utf-8"), headers=headers, method="POST")
        for attempt in range(3):
            try:
                with self._opener.open(request, timeout=90) as response:
                    self.session_id = response.headers.get("Mcp-Session-Id", self.session_id)
                    content = self._response_content(response)
                break
            except (URLError, TimeoutError):
                if attempt == 2:
                    raise
                time.sleep(attempt + 1)
        return {} if notification or not content else decode_mcp_response(content)

    def initialize(self) -> None:
        if self._initialized:
            return
        self._post(
            "initialize",
            {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {"name": "brazil-stock-workbench", "version": "1.0.0"},
            },
        )
        self._post("notifications/initialized", notification=True)
        self._initialized = True

    def call_tool(self, name: str, arguments: dict[str, Any]) -> dict[str, Any]:
        self.initialize()
        return self._post("tools/call", {"name": name, "arguments": arguments})


def tool_payload(result: dict[str, Any]) -> dict[str, Any]:
    text = next((item.get("text", "") for item in result.get("content", []) if item.get("type") == "text"), "")
    if not text:
        raise ValueError("企业微信 MCP 未返回表格数据")
    payload = json.loads(text)
    if payload.get("errcode") not in (0, None):
        raise RuntimeError(str(payload.get("errmsg") or payload))
    return payload


def display_value(value: Any) -> Any:
    if isinstance(value, list):
        return ", ".join(str(item.get("text", "")) for item in value if isinstance(item, dict))
    return value


def read_smartsheet_snapshot(client: WeComMCPClient, sheet_url: str, titles: set[str] | None = None) -> list[dict[str, Any]]:
    sheets = tool_payload(client.call_tool("smartsheet_get_sheet", {"url": sheet_url})).get("sheet_list", [])
    snapshot: list[dict[str, Any]] = []
    for sheet in sheets:
        if titles is not None and str(sheet.get("title")) not in titles:
            continue
        sheet_id = str(sheet["sheet_id"])
        fields = tool_payload(client.call_tool("smartsheet_get_fields", {"url": sheet_url, "sheet_id": sheet_id})).get("fields", [])
        headers = [str(field.get("field_title") or "") for field in fields if field.get("field_title")]
        records: list[dict[str, Any]] = []
        cursor: Any = None
        while True:
            args: dict[str, Any] = {"url": sheet_url, "sheet_id": sheet_id, "limit": 1000}
            if cursor not in (None, "", 0):
                args["cursor"] = cursor
            page = tool_payload(client.call_tool("smartsheet_get_records", args))
            records.extend(page.get("records", []))
            if not page.get("has_more"):
                break
            cursor = page.get("next")
        rows = [[display_value(record.get("values", {}).get(header, "")) for header in headers] for record in records]
        snapshot.append({"title": str(sheet.get("title") or "Sheet"), "headers": headers, "rows": rows})
    return snapshot


def _cell_text(value: Any) -> str:
    if isinstance(value, list):
        return "".join(str(item.get("text", "")) for item in value if isinstance(item, dict)).strip()
    return str(value or "").strip()


def build_sheet_row_index(rows: Iterable[dict[str, Any]], sku_key: str = "SKU") -> dict[str, int]:
    return {str(row.get(sku_key) or "").strip(): int(row["row_index"]) for row in rows if str(row.get(sku_key) or "").strip()}


def build_sheet_write_rows(row_index_by_sku: dict[str, int], updates_by_sku: dict[str, dict[int, Any]]) -> tuple[list[dict[str, Any]], list[str]]:
    writes, skipped = [], []
    for sku, columns in updates_by_sku.items():
        row_index = row_index_by_sku.get(sku)
        if row_index is None:
            skipped.append(sku)
            continue
        writes.append({"row_index": row_index, "columns": dict(columns)})
    return writes, skipped


def parse_ordinary_sheet_markdown(content: str, title: str, first_data_row_index: int = 3) -> list[dict[str, Any]]:
    lines = content.splitlines()
    title_index = next((index for index, line in enumerate(lines) if line.strip() == title), -1)
    if title_index < 0:
        raise ValueError(f"找不到工作表: {title}")
    header_index = next((
        index for index in range(title_index + 1, len(lines))
        if lines[index].startswith("|") and "|产品|SKU|" in lines[index]
    ), -1)
    if header_index < 0:
        raise ValueError(f"工作表没有表头: {title}")

    def cells(line: str) -> list[str]:
        return [value.strip() for value in line.split("|")[1:-1]]

    headers = cells(lines[header_index])
    rows: list[dict[str, Any]] = []
    for offset, line in enumerate(lines[header_index + 2 :]):
        if not line.startswith("|"):
            break
        values = cells(line)
        row = {header: values[index] if index < len(values) else "" for index, header in enumerate(headers) if header}
        # The document export keeps every physical cell but omits labels for
        # merged child headers. Preserve the fixed writeback columns by their
        # physical position so verification can compare each one accurately.
        for column in (column for group in WRITEBACK_COLUMN_RANGES for column in group):
            index = _column_index(column)
            if index < len(values):
                row[column] = values[index]
        row["row_index"] = first_data_row_index + offset
        if not str(row.get("SKU") or "").strip():
            continue
        rows.append(row)
    return rows


def _column_index(reference: str) -> int:
    letters = re.fullmatch(r"[A-Z]+", reference)
    if not letters:
        raise ValueError(f"无效列名: {reference}")
    result = 0
    for char in reference:
        result = result * 26 + ord(char) - 64
    return result - 1


def _sheet_cell(value: Any) -> dict[str, Any]:
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        return {"cell_value": {"number": value}, "data_type": "NUMBER"}
    if isinstance(value, str) and re.fullmatch(r"-?(?:0|[1-9]\d*)(?:\.\d+)?", value.strip()):
        numeric = float(value) if "." in value else int(value)
        return {"cell_value": {"number": numeric}, "data_type": "NUMBER"}
    return {"cell_value": {"text": "" if value is None else str(value)}, "data_type": "TEXT"}


def build_sheet_grid_data(
    online_rows: list[dict[str, Any]], updates_by_sku: dict[str, dict[str, Any]], columns: list[str]
) -> dict[str, Any]:
    if not online_rows or not columns:
        raise ValueError("线上行和写入列不能为空")
    online_skus = [str(row.get("SKU") or "").strip() for row in online_rows]
    if not all(online_skus) or set(online_skus) != set(updates_by_sku):
        raise ValueError("线上表与本次结果的 SKU 集合不一致")
    row_indexes = [int(row["row_index"]) for row in online_rows]
    if row_indexes != list(range(row_indexes[0], row_indexes[0] + len(row_indexes))):
        raise ValueError("线上 SKU 行号不连续")
    column_indexes = [_column_index(column) for column in columns]
    if column_indexes != list(range(column_indexes[0], column_indexes[0] + len(column_indexes))):
        raise ValueError("写入列必须连续")
    return {
        "start_row": row_indexes[0],
        "start_column": column_indexes[0],
        "rows": [
            {"values": [_sheet_cell(updates_by_sku[sku].get(column, "")) for column in columns]}
            for sku in online_skus
        ],
    }


def read_ordinary_sheet_rows(
    client: WeComMCPClient,
    sheet_url: str,
    title: str = "订单库存总表",
    poll_interval: float = 2.0,
    max_polls: int = 30,
) -> tuple[str, list[dict[str, Any]]]:
    info = tool_payload(client.call_tool("sheet_get_info", {"url": sheet_url}))
    sheet = next((item for item in info.get("sheets", []) if item.get("title") == title), None)
    if not sheet:
        raise ValueError(f"找不到工作表: {title}")
    response = tool_payload(client.call_tool("get_doc_content", {"url": sheet_url, "type": 2}))
    task_id = response.get("task_id")
    for _ in range(max_polls):
        if response.get("task_done"):
            content = str(response.get("content") or "")
            return str(sheet["sheet_id"]), parse_ordinary_sheet_markdown(content, title)
        if not task_id:
            raise RuntimeError("企微未返回在线表格读取任务 ID")
        if poll_interval:
            time.sleep(poll_interval)
        response = tool_payload(client.call_tool("get_doc_content", {"url": sheet_url, "type": 2, "task_id": task_id}))
    raise TimeoutError("读取企微在线表格超时")


WRITEBACK_COLUMN_RANGES = [
    ["S", "T", "U", "V", "W", "X", "Y", "Z"],
    ["AC", "AD", "AE", "AF"],
    ["AL", "AM", "AN", "AO", "AP", "AQ", "AR", "AS", "AT", "AU", "AV", "AW", "AX", "AY", "AZ", "BA", "BB", "BC"],
]
MAX_ORDINARY_SHEET_ROWS_PER_REQUEST = 1000
# The formal Stock_Order sheet currently responds reliably to 50-row MCP
# writes; 100-row requests can remain pending without a response.
MAX_FIXED_LAYOUT_ROWS_PER_REQUEST = 50


def _contiguous_row_groups(rows: list[dict[str, Any]]) -> list[list[dict[str, Any]]]:
    groups: list[list[dict[str, Any]]] = []
    for row in rows:
        if not groups or int(row["row_index"]) != int(groups[-1][-1]["row_index"]) + 1:
            groups.append([row])
        else:
            groups[-1].append(row)
    return groups


def _same_writeback_value(current: Any, desired: Any) -> bool:
    if current in (None, "") or desired in (None, ""):
        return current in (None, "") and desired in (None, "")
    try:
        return float(current) == float(desired)
    except (TypeError, ValueError):
        return str(current or "").strip() == str(desired or "").strip()


def filter_online_changed_updates(
    online_rows: list[dict[str, Any]], updates_by_sku: dict[str, dict[str, Any]]
) -> dict[str, dict[str, Any]]:
    """Keep only allowed cells whose target differs from the current online sheet."""
    online_by_sku = {str(row.get("SKU") or "").strip(): row for row in online_rows}
    allowed_columns = {column for columns in WRITEBACK_COLUMN_RANGES for column in columns}
    changed: dict[str, dict[str, Any]] = {}
    for sku, desired_columns in updates_by_sku.items():
        online_row = online_by_sku.get(str(sku).strip())
        if online_row is None:
            raise ValueError(f"在线表格缺少 SKU: {sku}")
        cells = {
            column: value
            for column, value in desired_columns.items()
            if column in allowed_columns and not _same_writeback_value(online_row.get(column), value)
        }
        if cells:
            changed[sku] = cells
    return changed


def _contiguous_column_runs(columns: list[str]) -> list[list[str]]:
    runs: list[list[str]] = []
    for column in columns:
        if not runs or _column_index(column) != _column_index(runs[-1][-1]) + 1:
            runs.append([column])
        else:
            runs[-1].append(column)
    return runs


def write_ordinary_sheet_column_updates(
    client: WeComMCPClient,
    sheet_url: str,
    sheet_id: str,
    rows: list[dict[str, Any]],
    updates_by_sku: dict[str, dict[str, Any]],
    columns: list[str],
) -> dict[str, int]:
    """Write one continuous column block without shifting non-contiguous SKU rows."""
    if not columns:
        raise ValueError("写入列不能为空")
    indexes = [_column_index(column) for column in columns]
    if indexes != list(range(indexes[0], indexes[0] + len(indexes))):
        raise ValueError("写入列必须连续")
    changed_rows = [row for row in rows if str(row.get("SKU") or "").strip() in updates_by_sku]
    changed_rows.sort(key=lambda row: int(row["row_index"]))
    written_cells = requests = 0
    for group in _contiguous_row_groups(changed_rows):
        for start in range(0, len(group), MAX_FIXED_LAYOUT_ROWS_PER_REQUEST):
            batch = group[start : start + MAX_FIXED_LAYOUT_ROWS_PER_REQUEST]
            tool_payload(client.call_tool("sheet_update_range_data", {
                "url": sheet_url,
                "sheet_id": sheet_id,
                "grid_data": {
                    "start_row": int(batch[0]["row_index"]),
                    "start_column": indexes[0],
                    "rows": [
                        {"values": [_sheet_cell(updates_by_sku[str(row["SKU"]).strip()].get(column, "")) for column in columns]}
                        for row in batch
                    ],
                },
            }))
            written_cells += len(batch) * len(columns)
            requests += 1
    return {"updated_rows": len(changed_rows), "written_cells": written_cells, "requests": requests}


def write_ordinary_sheet_updates(
    client: WeComMCPClient,
    sheet_url: str,
    sheet_id: str,
    online_rows: list[dict[str, Any]],
    updates_by_sku: dict[str, dict[str, Any]],
) -> dict[str, Any]:
    updated_skus: set[str] = set()
    written_cells = 0
    for columns in WRITEBACK_COLUMN_RANGES:
        rows = [
            row for row in online_rows
            if any(column in updates_by_sku.get(str(row["SKU"]), {}) for column in columns)
        ]
        for group in _contiguous_row_groups(rows):
            for start in range(0, len(group), MAX_ORDINARY_SHEET_ROWS_PER_REQUEST):
                batch = group[start : start + MAX_ORDINARY_SHEET_ROWS_PER_REQUEST]
                grid_rows = []
                for row in batch:
                    sku = str(row["SKU"])
                    changed = updates_by_sku.get(sku, {})
                    grid_rows.append({
                        "values": [
                            _sheet_cell(changed[column] if column in changed else row.get(column, ""))
                            for column in columns
                        ]
                    })
                tool_payload(client.call_tool("sheet_update_range_data", {
                    "url": sheet_url,
                    "sheet_id": sheet_id,
                    "grid_data": {
                        "start_row": int(batch[0]["row_index"]),
                        "start_column": _column_index(columns[0]),
                        "rows": grid_rows,
                    },
                }))
                updated_skus.update(str(row["SKU"]) for row in batch)
                written_cells += len(batch) * len(columns)
    return {"updated_rows": len(updated_skus), "ranges": len(WRITEBACK_COLUMN_RANGES), "written_cells": written_cells}


def write_fixed_layout_updates(
    client: WeComMCPClient,
    sheet_url: str,
    sheet_id: str,
    layout_rows: list[dict[str, Any]],
    updates_by_sku: dict[str, dict[str, Any]],
    pause_seconds: float = 0.0,
) -> dict[str, Any]:
    """Write the three data blocks using the established Stock_Order row layout."""
    if not layout_rows:
        raise ValueError("本地 Stock_Order 没有可写回的 SKU 行")
    row_indexes = [int(row["row_index"]) for row in layout_rows]
    if row_indexes != list(range(row_indexes[0], row_indexes[0] + len(row_indexes))):
        raise ValueError("本地 Stock_Order SKU 行号不连续，不能使用固定行写回")

    written_cells = 0
    for columns in WRITEBACK_COLUMN_RANGES:
        batch_size = MAX_FIXED_LAYOUT_ROWS_PER_REQUEST
        for start in range(0, len(layout_rows), batch_size):
            batch = layout_rows[start : start + batch_size]
            grid_rows = []
            for row in batch:
                values = updates_by_sku.get(str(row["SKU"]), {})
                grid_rows.append({"values": [_sheet_cell(values.get(column, "")) for column in columns]})
            tool_payload(client.call_tool("sheet_update_range_data", {
                "url": sheet_url,
                "sheet_id": sheet_id,
                "grid_data": {
                    "start_row": int(batch[0]["row_index"]),
                    "start_column": _column_index(columns[0]),
                    "rows": grid_rows,
                },
            }))
            written_cells += len(batch) * len(columns)
            if pause_seconds and start + batch_size < len(layout_rows):
                time.sleep(pause_seconds)
    return {"updated_rows": len(layout_rows), "ranges": len(WRITEBACK_COLUMN_RANGES), "written_cells": written_cells}


def verify_fixed_layout_updates(
    online_rows: list[dict[str, Any]],
    layout_rows: list[dict[str, Any]],
    updates_by_sku: dict[str, dict[str, Any]],
) -> dict[str, int]:
    """Ensure the formal sheet now matches every intended fixed-layout write."""
    online_by_index = {int(row["row_index"]): row for row in online_rows}
    allowed_columns = [column for group in WRITEBACK_COLUMN_RANGES for column in group]
    mismatches: list[str] = []
    for layout_row in layout_rows:
        row_index = int(layout_row["row_index"])
        sku = str(layout_row["SKU"])
        online_row = online_by_index.get(row_index)
        if online_row is None or str(online_row.get("SKU") or "").strip() != sku:
            mismatches.append(f"{sku}: SKU 行错位")
            continue
        expected = updates_by_sku.get(sku, {})
        for column in allowed_columns:
            if not _same_writeback_value(online_row.get(column), expected.get(column, "")):
                mismatches.append(f"{sku}:{column}")
                break
    if mismatches:
        sample = "、".join(mismatches[:10])
        raise RuntimeError(f"在线表格回读核对失败（{len(mismatches)} 行）：{sample}")
    return {"verified_rows": len(layout_rows)}


def verify_online_updates_by_sku(
    online_rows: list[dict[str, Any]],
    updates_by_sku: dict[str, dict[str, Any]],
) -> dict[str, int]:
    """Verify writeback values by SKU, regardless of the sheet's row order."""
    online_by_sku = {str(row.get("SKU") or "").strip(): row for row in online_rows}
    allowed_columns = [column for group in WRITEBACK_COLUMN_RANGES for column in group]
    mismatches: list[str] = []
    for sku, expected in updates_by_sku.items():
        online_row = online_by_sku.get(str(sku).strip())
        if online_row is None:
            mismatches.append(f"{sku}: SKU 缺失")
            continue
        for column in allowed_columns:
            if not _same_writeback_value(online_row.get(column), expected.get(column, "")):
                mismatches.append(f"{sku}:{column}")
                break
    if mismatches:
        sample = "、".join(mismatches[:10])
        raise RuntimeError(f"在线表格回读核对失败（{len(mismatches)} 行）：{sample}")
    return {"verified_rows": len(updates_by_sku)}


def build_record_batches(
    records: Iterable[dict[str, Any]],
    updates_by_sku: dict[str, dict[str, Any]],
    writable_fields: set[str],
    batch_size: int = 500,
) -> tuple[list[list[dict[str, Any]]], list[dict[str, str]]]:
    """Match updates to smart-sheet record IDs and chunk safe write batches."""
    if batch_size < 1 or batch_size > 500:
        raise ValueError("batch_size 必须在 1 到 500 之间")
    record_ids_by_sku: dict[str, str] = {}
    for record in records:
        sku = _cell_text(record.get("values", {}).get("SKU"))
        record_id = str(record.get("record_id") or "")
        if sku and record_id and sku not in record_ids_by_sku:
            record_ids_by_sku[sku] = record_id

    writes: list[dict[str, Any]] = []
    skipped: list[dict[str, str]] = []
    for sku, update_values in updates_by_sku.items():
        record_id = record_ids_by_sku.get(sku)
        if not record_id:
            skipped.append({"sku": sku, "reason": "not_found_in_smartsheet"})
            continue
        values = {field: value for field, value in update_values.items() if field in writable_fields}
        if values:
            writes.append({"record_id": record_id, "values": values})
    return [writes[index : index + batch_size] for index in range(0, len(writes), batch_size)], skipped
