"""OOXML-based inventory update pipeline for the Brazil stock workbook."""

from __future__ import annotations

import io
import json
import re
import zipfile
from collections import defaultdict
from copy import deepcopy
from datetime import datetime
from pathlib import Path
from typing import Any, Iterable
from xml.etree import ElementTree as ET
from zoneinfo import ZoneInfo


MAIN_NS = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
REL_NS = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
PKG_REL_NS = "http://schemas.openxmlformats.org/package/2006/relationships"
NS = {"m": MAIN_NS, "r": REL_NS, "pr": PKG_REL_NS}

STORE_COLUMNS = {
    "Meli 店铺1": "AK",
    "Meli 店铺2": "AL",
    "Meli 店铺3": "AM",
    "Meli 店铺6": "AN",
    "Meli 店铺7": "AO",
    "Meli 店铺8": "AP",
    "Shopee 店铺1": "AQ",
    "Shopee 店铺2": "AR",
    "Shopee 店铺3": "AS",
    "Shopee 店铺4": "AT",
    "Shopee 店铺5": "AU",
    "Shopee 店铺6": "AV",
    "Shopee 店铺7": "AW",
    "Shopee 店铺8": "AX",
    "Temu店铺1": "AY",
    "Temu店铺2": "AZ",
    "Temu店铺6": "BA",
    "Temu 店铺6": "BA",
    "Temu店铺7": "BB",
}
FULL_COLUMNS = {
    "Meli 店铺1": "R",
    "Meli 店铺2": "S",
    "Meli 店铺3": "T",
    "Meli 店铺6": "U",
    "Meli 店铺8": "V",
}
PERIOD_COLUMNS = {"3天": "AB", "7天": "AC", "15天": "AD", "30天": "AE"}
PHYSICAL_STORES = {"手工店铺2", "默认店铺"}


REQUIRED_SOURCE_KEYS = ["inventory", "full", "\u0033\u5929", "\u0037\u5929", "\u0031\u0035\u5929", "\u0033\u0030\u5929", "target"]


def beijing_update_label(now: datetime | None = None) -> str:
    current = (now or datetime.now(ZoneInfo("Asia/Shanghai"))).astimezone(ZoneInfo("Asia/Shanghai"))
    return f"最近更新时间：{current.month}.{current.day}"


def validate_source_files(files: dict[str, str | Path]) -> dict[str, Any]:
    missing = []
    for key in REQUIRED_SOURCE_KEYS:
        value = files.get(key)
        if not value or not Path(value).exists():
            missing.append(key)
    return {"ok": not missing, "missing": missing}


def validate_target_layout(target_path: str | Path) -> list[str]:
    """Reject older Stock_Order templates whose inventory columns no longer match the SOP."""
    try:
        with zipfile.ZipFile(target_path) as archive:
            paths = _sheet_paths(archive)
            target_sheet_path = paths.get("订单库存总表") or next(iter(paths.values()))
            root = _read_xml_text(archive.read(target_sheet_path))
            shared = _shared_strings(archive)
            row = next((candidate for candidate in root.findall(".//m:sheetData/m:row", NS) if int(candidate.attrib["r"]) == 2), None)
            values = {
                re.match(r"[A-Z]+", cell.attrib["r"]).group(0): _cell_value(cell, shared)
                for cell in (row.findall("m:c", NS) if row is not None else [])
            }
    except (KeyError, StopIteration, zipfile.BadZipFile, ET.ParseError) as exc:
        return [f"Stock_Order 目标表读取失败: {exc}"]

    expected = {
        "V": "Meli Full店铺8库存",
        "W": "第三方锁定库存",
        "X": "不良品数",
        "Y": "第三方可用库存",
    }
    errors = [
        f"{column}2 应为 {label}，实际为 {values.get(column, '') or '空'}"
        for column, label in expected.items()
        if str(values.get(column, "")).strip() != label
    ]
    if "待开票" in {str(value).strip() for value in values.values()}:
        errors.append("目标 Stock_Order 仍包含已废弃的“待开票”列")
    subheader = next((candidate for candidate in root.findall(".//m:sheetData/m:row", NS) if int(candidate.attrib["r"]) == 3), None)
    labels = {
        re.match(r"[A-Z]+", cell.attrib["r"]).group(0): str(_cell_value(cell, shared)).replace("鋪", "铺").strip()
        for cell in (subheader.findall("m:c", NS) if subheader is not None else [])
    }
    for period, column in PERIOD_COLUMNS.items():
        if labels.get(column) != period:
            errors.append(f"{column}3 应为 {period}")
    for store, column in STORE_COLUMNS.items():
        expected_label = re.search(r"店铺\d+", store).group(0)
        if labels.get(column) != expected_label:
            errors.append(f"{column}3 应为 {store}")
    return errors


def normalize_store(value: Any) -> str:
    text = str(value or "").strip()
    return re.sub(r"\[(?:mercado BR|shopee BR|temu BR|physical BR|Mercado Libre BR|Temu BR|\u624b\u5de5\u5e97\u94fa BR)\]$", "", text).strip()


def sales_sku_candidates() -> list[str]:
    return ["SKU", "\u5546\u54c1SKU", "\u7236SKU"]


def normalize_sku(value: Any) -> str:
    return str(value or "").strip()


def map_products_to_skus(
    products: Iterable[Any],
    source_rows: Iterable[dict[str, Any]],
    product_key: str,
    sku_key: str,
) -> list[str]:
    mapping = {
        str(row.get(product_key) or "").strip(): normalize_sku(row.get(sku_key))
        for row in source_rows
        if str(row.get(product_key) or "").strip()
    }
    return [mapping.get(str(product or "").strip(), "") for product in products]


def map_products_to_skus_with_status(
    products: Iterable[Any],
    source_rows: Iterable[dict[str, Any]],
    product_key: str,
    sku_key: str,
) -> list[dict[str, str]]:
    exact_mapping: dict[str, str] = {}
    normalized_mapping: defaultdict[str, set[str]] = defaultdict(set)
    for row in source_rows:
        raw_product = str(row.get(product_key) or "")
        sku = normalize_sku(row.get(sku_key))
        if not raw_product or not sku:
            continue
        exact_mapping[raw_product] = sku
        normalized_mapping[raw_product.strip()].add(sku)

    result: list[dict[str, str]] = []
    for product in products:
        raw_product = str(product or "")
        if raw_product in exact_mapping:
            result.append({"product": raw_product, "sku": exact_mapping[raw_product], "status": "exact"})
            continue
        candidates = normalized_mapping.get(raw_product.strip(), set())
        if len(candidates) == 1:
            result.append({"product": raw_product, "sku": next(iter(candidates)), "status": "normalized"})
        elif len(candidates) > 1:
            result.append({"product": raw_product, "sku": "", "status": "ambiguous"})
        else:
            result.append({"product": raw_product, "sku": "", "status": "unmatched"})
    return result


def build_key_store_formulas(row_number: int) -> dict[str, str]:
    return {
        "M": f'IF(MAX(AK{row_number}:AP{row_number})=0,"",INDEX({{"店铺1","店铺2","店铺3","店铺6","店铺7","店铺8"}},1,MATCH(MAX(AK{row_number}:AP{row_number}),AK{row_number}:AP{row_number},0)))',
        "N": f'IF(MAX(AQ{row_number}:AX{row_number})=0,"",INDEX({{"店铺1","店铺2","店铺3","店铺4","店铺5","店铺6","店铺7","店铺8"}},1,MATCH(MAX(AQ{row_number}:AX{row_number}),AQ{row_number}:AX{row_number},0)))',
        "O": f'IF(MAX(AY{row_number}:BB{row_number})=0,"",INDEX({{"店铺1","店铺2","店铺6","店铺7"}},1,MATCH(MAX(AY{row_number}:BB{row_number}),AY{row_number}:BB{row_number},0)))',
        "Z": f"SUM(R{row_number}:Y{row_number})",
    }


def build_replenishment_formulas(row_number: int) -> dict[str, str]:
    return {
        "BC": f'=IF(BD{row_number}<=0,"无库存",IF(Z{row_number}+AA{row_number}<AF{row_number}*BE{row_number}*BG{row_number},"建议补货",IF(AND(AF{row_number}*BE{row_number}*BG{row_number}<=Z{row_number}+AA{row_number},Z{row_number}+AA{row_number}<=AF{row_number}*(BE{row_number}+BH{row_number})*BG{row_number}),"预警补货","无需补货")))',
        "BD": f'=IF(OR(F{row_number}="B"),0,ROUND((BE{row_number}+BH{row_number})*AF{row_number}*BG{row_number}-Y{row_number}-AA{row_number},0))',
    }


def enable_full_recalculation(workbook_bytes: bytes) -> bytes:
    root = _read_xml_text(workbook_bytes)
    calc = root.find("m:calcPr", NS)
    if calc is None:
        calc = ET.SubElement(root, f"{{{MAIN_NS}}}calcPr")
    calc.attrib.update({"calcMode": "auto", "fullCalcOnLoad": "1", "forceFullCalc": "1"})
    return ET.tostring(root, encoding="utf-8", xml_declaration=True)


def number(value: Any) -> float:
    if value in (None, ""):
        return 0.0
    try:
        return float(value)
    except (TypeError, ValueError):
        raise ValueError(f"数量不是数字: {value!r}")


def compact_number(value: float) -> int | float:
    return int(value) if float(value).is_integer() else round(value, 6)


def _numeric_or_zero(value: Any) -> float:
    try:
        return number(value)
    except ValueError:
        return 0.0


def build_workbench_preview_row(
    source_row: dict[str, Any], updates: dict[str, Any], next_arrival_days: Any = 0
) -> dict[str, Any]:
    """Calculate the visible workbench row from real Stock fields and this run's updates."""
    def value(column: str) -> Any:
        return updates.get(column, source_row.get(column, 0))

    sales = {column: _numeric_or_zero(value(column)) for column in PERIOD_COLUMNS.values()}
    daily_sales = sales["AB"] / 3 * 0.5 + sales["AC"] / 7 * 0.25 + sales["AD"] / 15 * 0.15 + sales["AE"] / 30 * 0.1
    available = _numeric_or_zero(value("Y"))
    current_stock = sum(_numeric_or_zero(value(column)) for column in ("R", "S", "T", "U", "V", "W", "X", "Y"))
    sell_days: int | float | str = "无库存" if available == 0 else ("无动销" if daily_sales == 0 else compact_number(available / daily_sales))
    product_type = str(source_row.get("F") or "")
    transit = _numeric_or_zero(source_row.get("AA"))
    price_advice = ""
    if product_type != "B" and isinstance(sell_days, (int, float)):
        arrival_days = _numeric_or_zero(next_arrival_days)
        # Excel date serials can leak into the "next arrival days" field as
        # large negative numbers. They are not valid day counts.
        has_valid_arrival_days = 0 <= arrival_days <= 365
        if transit == 0 or not has_valid_arrival_days:
            if sell_days <= 7:
                price_advice = "+8%~15%"
            elif sell_days <= 15:
                price_advice = "+5%~8%"
            elif sell_days <= 30:
                price_advice = "+3%~5%"
            elif sell_days <= 50:
                price_advice = "+1%~3%"
        else:
            difference = float(sell_days) - arrival_days
            if difference <= -30:
                price_advice = "+15%~20%"
            elif difference <= -15:
                price_advice = "+8%~15%"
            elif difference <= -5:
                price_advice = "+5%~8%"
            elif difference < 0:
                price_advice = "+3%~5%"
            elif difference <= 10:
                price_advice = "+1%~3%"
    status = "紧急" if price_advice in {"+15%~20%", "+8%~15%"} else "重要" if price_advice in {"+5%~8%", "+3%~5%"} else "一般" if price_advice == "+1%~3%" else ""
    cycle = _numeric_or_zero(source_row.get("BE"))
    safety = _numeric_or_zero(source_row.get("BG"))
    warning_days = _numeric_or_zero(source_row.get("BH"))
    purchase_qty = 0 if product_type == "B" else max(0, round((cycle + warning_days) * daily_sales * safety - available - transit))
    if purchase_qty <= 0:
        replenish_advice = "不补货"
    elif current_stock + transit < daily_sales * cycle * safety:
        replenish_advice = "急需补货"
    elif current_stock + transit <= daily_sales * (cycle + warning_days) * safety:
        replenish_advice = "预警补货"
    else:
        replenish_advice = "正常补货"
    return {
        "sku": str(source_row.get("B") or ""),
        "name": str(source_row.get("A") or ""),
        "stock": compact_number(current_stock),
        "days": sell_days,
        "status": status,
        "advice": replenish_advice,
        "purchaseQty": purchase_qty,
        "priority": replenish_advice in {"急需补货", "预警补货"},
    }


def aggregate_by_sku(rows: Iterable[dict[str, Any]], sku_key: str, qty_key: str) -> dict[str, int | float]:
    totals: defaultdict[str, float] = defaultdict(float)
    for row in rows:
        sku = normalize_sku(row.get(sku_key))
        if sku:
            totals[sku] += number(row.get(qty_key))
    return {sku: compact_number(value) for sku, value in totals.items()}


def aggregate_by_sku_store(
    rows: Iterable[dict[str, Any]], sku_key: str, store_key: str, qty_key: str
) -> dict[tuple[str, str], int | float]:
    totals: defaultdict[tuple[str, str], float] = defaultdict(float)
    for row in rows:
        sku = normalize_sku(row.get(sku_key))
        store = normalize_store(row.get(store_key))
        if sku and store:
            totals[(sku, store)] += number(row.get(qty_key))
    return {key: compact_number(value) for key, value in totals.items()}


def aggregate_oms_by_sku(
    rows: Iterable[dict[str, Any]],
    sku_key: str,
    locked_key: str,
    available_key: str,
    defective_key: str,
) -> dict[str, dict[str, int | float]]:
    totals: defaultdict[str, dict[str, float]] = defaultdict(
        lambda: {"locked": 0.0, "available": 0.0, "defective": 0.0}
    )
    for row in rows:
        sku = normalize_sku(row.get(sku_key))
        if not sku:
            continue
        totals[sku]["locked"] += number(row.get(locked_key))
        totals[sku]["available"] += number(row.get(available_key))
        totals[sku]["defective"] += number(row.get(defective_key))
    return {
        sku: {key: compact_number(value) for key, value in values.items()}
        for sku, values in totals.items()
    }


def aggregate_inventory_by_sku(
    rows: Iterable[dict[str, Any]],
    sku_key: str,
    occupied_key: str,
    available_key: str,
    unavailable_key: str,
) -> dict[str, dict[str, int | float]]:
    """Build the per-SKU third-party inventory pivot required by the SOP."""
    totals: defaultdict[str, dict[str, float]] = defaultdict(
        lambda: {"occupied": 0.0, "available": 0.0, "unavailable": 0.0}
    )
    for row in rows:
        sku = normalize_sku(row.get(sku_key))
        if not sku:
            continue
        totals[sku]["occupied"] += number(row.get(occupied_key))
        totals[sku]["available"] += number(row.get(available_key))
        totals[sku]["unavailable"] += number(row.get(unavailable_key))
    return {
        sku: {key: compact_number(value) for key, value in values.items()}
        for sku, values in totals.items()
    }


def build_target_updates(
    target_skus: list[str],
    sales_by_period: dict[str, dict[str, Any]],
    sales_by_store: dict[tuple[str, str], Any],
    full_by_store: dict[tuple[str, str], Any],
    inventory_by_sku: dict[str, dict[str, Any]],
) -> dict[str, dict[str, Any]]:
    updates: dict[str, dict[str, Any]] = {}
    for sku in target_skus:
        row: dict[str, Any] = {}
        for period, column in PERIOD_COLUMNS.items():
            row[column] = sales_by_period.get(period, {}).get(sku, 0)
        for store, column in STORE_COLUMNS.items():
            row[column] = sales_by_store.get((sku, store), 0)
        for store, column in FULL_COLUMNS.items():
            row[column] = full_by_store.get((sku, store), "")

        inventory = inventory_by_sku.get(sku, {})
        # SOP: the current Stock_Order sheet has no pending-invoice column.
        # V is Full store 8; W/X/Y are occupied/unavailable/available.
        row["W"] = inventory.get("occupied", 0)
        row["X"] = inventory.get("unavailable", 0)
        row["Y"] = inventory.get("available", 0)
        updates[sku] = row
    return updates


def _column_number(reference: str) -> int:
    letters = re.match(r"[A-Z]+", reference).group(0)
    result = 0
    for char in letters:
        result = result * 26 + ord(char) - 64
    return result


def _read_xml_text(data: bytes) -> ET.Element:
    return ET.fromstring(data)


def _shared_strings(archive: zipfile.ZipFile) -> list[str]:
    try:
        root = _read_xml_text(archive.read("xl/sharedStrings.xml"))
    except KeyError:
        return []
    return ["".join(node.text or "" for node in item.findall(".//m:t", NS)) for item in root.findall("m:si", NS)]


def _sheet_paths(archive: zipfile.ZipFile) -> dict[str, str]:
    workbook = _read_xml_text(archive.read("xl/workbook.xml"))
    rels = _read_xml_text(archive.read("xl/_rels/workbook.xml.rels"))
    relation_map = {item.attrib["Id"]: item.attrib["Target"] for item in rels.findall("pr:Relationship", NS)}
    paths: dict[str, str] = {}
    for sheet in workbook.findall("m:sheets/m:sheet", NS):
        rel_id = sheet.attrib[f"{{{REL_NS}}}id"]
        target = relation_map[rel_id]
        paths[sheet.attrib["name"]] = target if target.startswith("xl/") else f"xl/{target}"
    return paths


def _cell_value(cell: ET.Element, shared: list[str]) -> Any:
    value = cell.find("m:v", NS)
    inline = cell.find("m:is", NS)
    raw = value.text if value is not None else ("".join(node.text or "" for node in inline.findall(".//m:t", NS)) if inline is not None else "")
    if cell.attrib.get("t") == "s" and raw:
        return shared[int(raw)]
    if cell.attrib.get("t") in {"str", "inlineStr"}:
        return raw
    if raw == "":
        return ""
    try:
        parsed = float(raw)
        return int(parsed) if parsed.is_integer() else parsed
    except ValueError:
        return raw


def read_xlsx_rows(
    path: str | Path, sheet_name: str | None = None, header_row_index: int = 0,
    *, strict_headers: bool = False,
) -> tuple[list[dict[str, Any]], str]:
    with zipfile.ZipFile(path) as archive:
        paths = _sheet_paths(archive)
        selected = sheet_name or next(iter(paths))
        if selected not in paths:
            raise ValueError(f"找不到工作表: {selected}")
        root = _read_xml_text(archive.read(paths[selected]))
        shared = _shared_strings(archive)
        raw_rows: list[dict[str, Any]] = []
        for row in root.findall(".//m:sheetData/m:row", NS):
            cells: dict[str, Any] = {}
            for cell in row.findall("m:c", NS):
                cells[cell.attrib["r"]] = _cell_value(cell, shared)
            raw_rows.append(cells)

    if not raw_rows:
        return [], selected
    if header_row_index >= len(raw_rows):
        return [], selected
    headers = {ref: value for ref, value in raw_rows[header_row_index].items() if value not in (None, "")}
    header_by_column = {re.match(r"[A-Z]+", ref).group(0): str(value).strip() for ref, value in headers.items()}
    if strict_headers:
        labels = list(header_by_column.values())
        if len(labels) != len(set(labels)):
            raise ValueError(f"{Path(path).name}: 重复表头，停止生成和写回")
        for raw in raw_rows[header_row_index + 1:]:
            for ref, value in raw.items():
                if value not in (None, "") and re.match(r"[A-Z]+", ref).group(0) not in header_by_column:
                    raise ValueError(f"{Path(path).name}: 表头与数据列不一致，{ref} 有值但没有表头，停止生成和写回")
    rows: list[dict[str, Any]] = []
    for raw in raw_rows[header_row_index + 1:]:
        record = {header_by_column[column]: value for ref, value in raw.items() if (column := re.match(r"[A-Z]+", ref).group(0)) in header_by_column}
        if any(value not in (None, "") for value in record.values()):
            rows.append(record)
    return rows, selected


def _required(rows: list[dict[str, Any]], candidates: list[str], label: str) -> str:
    headers = set(rows[0]) if rows else set()
    for candidate in candidates:
        if candidate in headers:
            return candidate
    raise ValueError(f"{label}缺少字段，需要其中一个: {', '.join(candidates)}")


def load_sources(files: dict[str, str | Path]) -> tuple[dict[str, Any], dict[str, Any]]:
    report = {"errors": [], "warnings": [], "files": {}}
    def read(key: str, sheet: str | None = None):
        if key not in files:
            report["errors"].append(f"缺少文件: {key}")
            return []
        rows, selected = read_xlsx_rows(files[key], sheet, strict_headers=True)
        report["files"][key] = {"sheet": selected, "rows": len(rows)}
        if not rows:
            report["errors"].append(f"文件没有数据行: {key}")
        return rows

    inventory_rows = read("inventory")
    full_rows = read("full")
    sales_rows = {period: read(period) for period in PERIOD_COLUMNS}
    target_tracking_rows, target_tracking_sheet = read_xlsx_rows(files["target"], "装柜到仓时间跟踪表", 2)
    report["files"]["target_tracking"] = {"sheet": target_tracking_sheet, "rows": len(target_tracking_rows)}

    inventory_sku = _required(inventory_rows, ["SKU", "商品SKU"], "库存数据 SKU") if inventory_rows else "SKU"
    occupied = _required(inventory_rows, ["UpSeller占用数"], "库存数据 UpSeller占用数") if inventory_rows else "UpSeller占用数"
    inventory_available = _required(inventory_rows, ["UpSeller可用库存"], "库存数据 UpSeller可用库存") if inventory_rows else "UpSeller可用库存"
    unavailable = _required(inventory_rows, ["三方仓不可用"], "库存数据 三方仓不可用") if inventory_rows else "三方仓不可用"
    full_sku = _required(full_rows, ["SKU", "商品SKU"], "FULL SKU") if full_rows else "SKU"
    full_store = _required(full_rows, ["店铺", "店铺名称"], "FULL 店铺") if full_rows else "店铺"
    full_available = _required(full_rows, ["可用库存", "可用"], "FULL 可用库存") if full_rows else "可用库存"
    tracking_product = _required(target_tracking_rows, ["产品"], "装柜到仓产品") if target_tracking_rows else "产品"
    tracking_sku = _required(target_tracking_rows, ["SKU", "商品SKU"], "装柜到仓 SKU") if target_tracking_rows else "SKU"
    tracking_next_arrival = _required(target_tracking_rows, ["下一批到货天数"], "装柜到仓下一批到货天数") if target_tracking_rows else "下一批到货天数"
    sales_configs = {}
    for period, rows in sales_rows.items():
        sales_configs[period] = {
            "rows": rows,
            "sku": _required(rows, sales_sku_candidates(), f"{period} SKU") if rows else "SKU",
            "store": _required(rows, ["店铺", "店铺名称"], f"{period} 店铺") if rows else "店铺",
            "qty": _required(rows, ["销量", "销售数量", "数量"], f"{period} 销量") if rows else "销量",
        }
    data = {
        "inventory": aggregate_inventory_by_sku(
            inventory_rows, inventory_sku, occupied, inventory_available, unavailable
        ),
        "full": aggregate_by_sku_store(full_rows, full_sku, full_store, full_available),
        "sales_period": {period: aggregate_by_sku(config["rows"], config["sku"], config["qty"]) for period, config in sales_configs.items()},
        "sales_store": {period: aggregate_by_sku_store(config["rows"], config["sku"], config["store"], config["qty"]) for period, config in sales_configs.items()},
        "product_to_sku": {
            str(row.get(tracking_product) or "").strip(): normalize_sku(row.get(tracking_sku))
            for row in target_tracking_rows
            if str(row.get(tracking_product) or "").strip()
        },
        "product_to_sku_status": map_products_to_skus_with_status(
            [row.get(tracking_product) for row in target_tracking_rows],
            target_tracking_rows,
            tracking_product,
            tracking_sku,
        ),
        "next_arrival_by_sku": {
            normalize_sku(row.get(tracking_sku)): row.get(tracking_next_arrival)
            for row in target_tracking_rows
            if normalize_sku(row.get(tracking_sku))
        },
    }
    # Store columns use the 30-day mapping; all periods are retained for total sales.
    data["sales_store_30"] = data["sales_store"].get("30天", {})
    return data, report


def index_sheet_rows(root: ET.Element) -> dict[int, ET.Element]:
    return {int(row.attrib["r"]): row for row in root.findall(".//m:sheetData/m:row", NS)}


def _replace_cell(root: ET.Element, reference: str, value: Any, row_index: dict[int, ET.Element] | None = None) -> None:
    row_number = int(re.search(r"\d+", reference).group(0))
    row = (row_index or {}).get(row_number)
    if row is None and row_index is None:
        row = next((candidate for candidate in root.findall(".//m:sheetData/m:row", NS) if int(candidate.attrib["r"]) == row_number), None)
    if row is None:
        sheet_data = root.find("m:sheetData", NS)
        row = ET.SubElement(sheet_data, f"{{{MAIN_NS}}}row", {"r": str(row_number)})
        if row_index is not None:
            row_index[row_number] = row
    cell = next((candidate for candidate in row.findall("m:c", NS) if candidate.attrib.get("r") == reference), None)
    if cell is None:
        cell = ET.SubElement(row, f"{{{MAIN_NS}}}c", {"r": reference})
        existing = list(row.findall("m:c", NS))
        existing.sort(key=lambda item: _column_number(item.attrib["r"]))
        for item in existing:
            row.remove(item)
        for item in existing:
            row.append(item)
    for child in list(cell):
        if child.tag in {f"{{{MAIN_NS}}}v", f"{{{MAIN_NS}}}f", f"{{{MAIN_NS}}}is"}:
            cell.remove(child)
    if value == "":
        cell.attrib["t"] = "str"
        ET.SubElement(cell, f"{{{MAIN_NS}}}v").text = ""
    elif isinstance(value, (int, float)):
        cell.attrib.pop("t", None)
        ET.SubElement(cell, f"{{{MAIN_NS}}}v").text = str(value)
    else:
        cell.attrib["t"] = "str"
        ET.SubElement(cell, f"{{{MAIN_NS}}}v").text = str(value)


def _replace_formula(root: ET.Element, reference: str, formula: str, row_index: dict[int, ET.Element] | None = None) -> None:
    row_number = int(re.search(r"\d+", reference).group(0))
    row = (row_index or {}).get(row_number)
    if row is None and row_index is None:
        row = next((candidate for candidate in root.findall(".//m:sheetData/m:row", NS) if int(candidate.attrib["r"]) == row_number), None)
    if row is None:
        sheet_data = root.find("m:sheetData", NS)
        row = ET.SubElement(sheet_data, f"{{{MAIN_NS}}}row", {"r": str(row_number)})
        if row_index is not None:
            row_index[row_number] = row
    cell = next((candidate for candidate in row.findall("m:c", NS) if candidate.attrib.get("r") == reference), None)
    if cell is None:
        cell = ET.SubElement(row, f"{{{MAIN_NS}}}c", {"r": reference})
    for child in list(cell):
        if child.tag in {f"{{{MAIN_NS}}}v", f"{{{MAIN_NS}}}f", f"{{{MAIN_NS}}}is"}:
            cell.remove(child)
    cell.attrib.pop("t", None)
    ET.SubElement(cell, f"{{{MAIN_NS}}}f").text = formula


def build_update_label_style(styles_xml: bytes, base_style_index: int) -> tuple[bytes, int]:
    """Clone a cell style and set only its font to 18pt standard red."""
    styles = _read_xml_text(styles_xml)
    fonts = styles.find("m:fonts", NS)
    cell_xfs = styles.find("m:cellXfs", NS)
    if fonts is None or cell_xfs is None:
        raise ValueError("Stock_Order 模板缺少字体或单元格样式定义")
    xfs = cell_xfs.findall("m:xf", NS)
    if base_style_index < 0 or base_style_index >= len(xfs):
        raise ValueError("Stock_Order A1 样式编号无效")
    base_font_index = int(xfs[base_style_index].attrib.get("fontId", "0"))
    font_items = fonts.findall("m:font", NS)
    if base_font_index < 0 or base_font_index >= len(font_items):
        raise ValueError("Stock_Order A1 字体编号无效")

    update_font = deepcopy(font_items[base_font_index])
    size = update_font.find("m:sz", NS)
    if size is None:
        size = ET.SubElement(update_font, f"{{{MAIN_NS}}}sz")
    size.attrib["val"] = "18"
    color = update_font.find("m:color", NS)
    if color is None:
        color = ET.SubElement(update_font, f"{{{MAIN_NS}}}color")
    color.attrib.clear()
    color.attrib["rgb"] = "FFFF0000"
    fonts.append(update_font)
    fonts.attrib["count"] = str(len(fonts.findall("m:font", NS)))

    update_xf = deepcopy(xfs[base_style_index])
    update_xf.attrib["fontId"] = str(len(font_items))
    cell_xfs.append(update_xf)
    cell_xfs.attrib["count"] = str(len(cell_xfs.findall("m:xf", NS)))
    return ET.tostring(styles, encoding="utf-8", xml_declaration=True), len(xfs)


def patch_target_workbook(target_path: str | Path, target_skus: list[tuple[int, str]], updates: dict[str, dict[str, Any]]) -> bytes:
    source = Path(target_path).read_bytes()
    input_buffer = io.BytesIO(source)
    output_buffer = io.BytesIO()
    with zipfile.ZipFile(input_buffer, "r") as source_zip, zipfile.ZipFile(output_buffer, "w", zipfile.ZIP_DEFLATED) as output_zip:
        paths = _sheet_paths(source_zip)
        target_sheet_path = paths.get("订单库存总表") or next(iter(paths.values()))
        target_xml = _read_xml_text(source_zip.read(target_sheet_path))
        row_index = index_sheet_rows(target_xml)
        _replace_cell(target_xml, "A1", beijing_update_label(), row_index)
        a1 = next(cell for cell in row_index[1].findall("m:c", NS) if cell.attrib.get("r") == "A1")
        styled_xml, style_index = build_update_label_style(
            source_zip.read("xl/styles.xml"), int(a1.attrib.get("s", "0"))
        )
        a1.attrib["s"] = str(style_index)
        for row_number, sku in target_skus:
            for column, value in updates.get(sku, {}).items():
                _replace_cell(target_xml, f"{column}{row_number}", value, row_index)
            # Keep the supplied template business formulas intact.
        patched = ET.tostring(target_xml, encoding="utf-8", xml_declaration=True)
        recalculated_workbook = enable_full_recalculation(source_zip.read("xl/workbook.xml"))
        for item in source_zip.infolist():
            if item.filename == target_sheet_path:
                content = patched
            elif item.filename == "xl/workbook.xml":
                content = recalculated_workbook
            elif item.filename == "xl/styles.xml":
                content = styled_xml
            else:
                content = source_zip.read(item.filename)
            output_zip.writestr(item, content)
    return output_buffer.getvalue()


def attach_writeback_updates(
    report: dict[str, Any], updates: dict[str, dict[str, Any]], include: bool
) -> dict[str, Any]:
    if include:
        report["writeback_updates"] = updates
    return report


def _same_writeback_value(current: Any, desired: Any) -> bool:
    if current in (None, "") and desired in (None, ""):
        return True
    try:
        return float(current) == float(desired)
    except (TypeError, ValueError):
        return str(current or "").strip() == str(desired or "").strip()


def filter_changed_updates(
    current_by_sku: dict[str, dict[str, Any]], desired_by_sku: dict[str, dict[str, Any]]
) -> dict[str, dict[str, Any]]:
    changed: dict[str, dict[str, Any]] = {}
    for sku, desired_columns in desired_by_sku.items():
        current_columns = current_by_sku.get(sku, {})
        changed_columns = {
            column: value for column, value in desired_columns.items()
            if not _same_writeback_value(current_columns.get(column), value)
        }
        if changed_columns:
            changed[sku] = changed_columns
    return changed


def process_files(
    files: dict[str, str | Path], include_writeback_updates: bool = False
) -> tuple[bytes | None, dict[str, Any]]:
    source_validation = validate_source_files(files)
    if not source_validation["ok"]:
        return None, {
            "errors": [f"缺少数据源文件: {', '.join(source_validation['missing'])}"],
            "warnings": [],
            "files": {},
        }
    layout_errors = validate_target_layout(files["target"])
    if layout_errors:
        return None, {
            "errors": ["目标 Stock_Order 不是当前标准在线表格结构，请先使用企业微信普通在线表格作为模板"] + layout_errors,
            "warnings": [],
            "files": {},
        }
    data, report = load_sources(files)
    target_rows, target_sheet = read_xlsx_rows(files["target"], "订单库存总表")
    with zipfile.ZipFile(files["target"]) as archive:
        paths = _sheet_paths(archive)
        root = _read_xml_text(archive.read(paths[target_sheet]))
        shared = _shared_strings(archive)
        target_skus: list[tuple[int, str]] = []
        target_preview_sources: list[tuple[int, str, dict[str, Any]]] = []
        mapping_counts = defaultdict(int)
        for row in root.findall(".//m:sheetData/m:row", NS):
            row_number = int(row.attrib["r"])
            if row_number < 4:
                continue
            row_values = {
                re.match(r"[A-Z]+", cell.attrib["r"]).group(0): _cell_value(cell, shared)
                for cell in row.findall("m:c", NS)
            }
            sku = normalize_sku(row_values.get("B"))
            if not sku:
                product = str(row_values.get("A") or "").strip()
                if product:
                    report.setdefault("warnings", []).append({"type": "missing_sku", "row": row_number, "product": product})
                continue
            mapping_counts["stock_sku"] += 1
            target_skus.append((row_number, sku))
            target_preview_sources.append((row_number, sku, row_values))
    sales_store = data["sales_store_30"]
    unknown_stores = sorted({store for _, store in sales_store if store not in STORE_COLUMNS and store not in PHYSICAL_STORES})
    if unknown_stores:
        report["warnings"].append({"type": "unknown_store", "stores": unknown_stores})
    updates = build_target_updates(
        [sku for _, sku in target_skus],
        data["sales_period"],
        sales_store,
        data["full"],
        data["inventory"],
    )
    preview_rows = [
        build_workbench_preview_row(row_values, updates[sku], data["next_arrival_by_sku"].get(sku, 0))
        for _, sku, row_values in target_preview_sources
    ]
    preview_rows.sort(key=lambda row: (not row["priority"], row["days"] if isinstance(row["days"], (int, float)) else float("inf")))
    report["priority_replenishment"] = sum(1 for row in preview_rows if row["priority"])
    report["preview_rows"] = preview_rows[:50]
    report["generated_at"] = datetime.now(ZoneInfo("Asia/Shanghai")).strftime("北京时间 %Y-%m-%d %H:%M")
    report["summary"] = {
        "target_rows": len(target_skus),
        "target_skus_matched_in_inventory": sum(1 for _, sku in target_skus if sku in data["inventory"]),
        "target_skus_matched_in_sales": sum(1 for _, sku in target_skus if any(sku in values for values in data["sales_period"].values())),
        "unknown_stores": unknown_stores,
        "product_mapping_status": dict(mapping_counts),
    }
    current_by_sku = {sku: row_values for _, sku, row_values in target_preview_sources}
    changed_updates = filter_changed_updates(current_by_sku, updates)
    attach_writeback_updates(report, changed_updates, include_writeback_updates)
    if include_writeback_updates:
        report["writeback_all_updates"] = updates
        report["writeback_layout_rows"] = [
            {"row_index": row_number - 1, "SKU": sku}
            for row_number, sku in target_skus
        ]
    if report["errors"]:
        return None, report
    # A generated Stock workbook is a complete daily snapshot.  Write all
    # calculated values (including zero sales) so newly added store columns
    # do not look empty simply because the template had blank cells.
    return patch_target_workbook(files["target"], target_skus, updates), report


def report_json(report: dict[str, Any]) -> str:
    return json.dumps(report, ensure_ascii=False, indent=2)
