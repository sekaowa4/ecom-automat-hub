from __future__ import annotations

import copy
import re
import shutil
import tempfile
import zipfile
from pathlib import Path
from xml.etree import ElementTree as ET

from inventory_pipeline import MAIN_NS, NS, _cell_value, _read_xml_text, _shared_strings, _sheet_paths


ROOT = Path(__file__).resolve().parent
TEMPLATE = ROOT / "runtime" / "stock_order_template_fixed_layout.xlsx"
BACKUP = ROOT / "runtime" / "stock_order_template_fixed_layout.before_store7.xlsx"
TEMU6_BACKUP = ROOT / "runtime" / "stock_order_template_fixed_layout.before_temu6_20260922.xlsx"
CELL_REF = re.compile(r"(\$?)([A-Z]+)(\$?\d+)")


def column_number(column: str) -> int:
    result = 0
    for letter in column:
        result = result * 26 + ord(letter) - 64
    return result


def column_name(number: int) -> str:
    result = ""
    while number:
        number, remainder = divmod(number - 1, 26)
        result = chr(65 + remainder) + result
    return result


def shift_reference(value: str, start_column: int) -> str:
    def replace(match: re.Match[str]) -> str:
        absolute, column, row = match.groups()
        index = column_number(column)
        return f"{absolute}{column_name(index + 1 if index >= start_column else index)}{row}"

    return CELL_REF.sub(replace, value)


def insert_column(root: ET.Element, start_column: int) -> None:
    for cell in root.findall(".//m:c", NS):
        cell.attrib["r"] = shift_reference(cell.attrib["r"], start_column)
        formula = cell.find("m:f", NS)
        if formula is not None and formula.text:
            formula.text = shift_reference(formula.text, start_column)
    for element in root.iter():
        for attribute in ("ref", "sqref"):
            if attribute in element.attrib:
                element.attrib[attribute] = shift_reference(element.attrib[attribute], start_column)
    for row in root.findall(".//m:sheetData/m:row", NS):
        cells = list(row.findall("m:c", NS))
        cells.sort(key=lambda cell: column_number(re.match(r"[A-Z]+", cell.attrib["r"]).group(0)))
        for cell in cells:
            row.remove(cell)
            row.append(cell)


def set_inline_text(row: ET.Element, reference: str, text: str, source_reference: str) -> None:
    source = next(cell for cell in row.findall("m:c", NS) if cell.attrib.get("r") == source_reference)
    for existing in list(row.findall("m:c", NS)):
        if existing.attrib.get("r") == reference:
            row.remove(existing)
    cell = copy.deepcopy(source)
    cell.attrib["r"] = reference
    cell.attrib["t"] = "inlineStr"
    for child in list(cell):
        cell.remove(child)
    inline = ET.SubElement(cell, f"{{{MAIN_NS}}}is")
    ET.SubElement(inline, f"{{{MAIN_NS}}}t").text = text
    row.append(cell)
    cells = list(row.findall("m:c", NS))
    cells.sort(key=lambda candidate: column_number(re.match(r"[A-Z]+", candidate.attrib["r"]).group(0)))
    for candidate in cells:
        row.remove(candidate)
        row.append(candidate)


def _insert_column_dimensions(root: ET.Element, start_column: int) -> None:
    cols = root.find("m:cols", NS)
    if cols is None:
        return
    dimensions = list(cols.findall("m:col", NS))
    source = next(
        (item for item in dimensions
         if int(item.attrib.get("min", "0")) <= start_column - 1 <= int(item.attrib.get("max", "0"))),
        None,
    )
    for item in dimensions:
        minimum = int(item.attrib.get("min", "0"))
        maximum = int(item.attrib.get("max", "0"))
        if minimum >= start_column:
            minimum += 1
            maximum += 1
        elif maximum >= start_column:
            maximum += 1
        item.attrib["min"] = str(minimum)
        item.attrib["max"] = str(maximum)
    if source is not None:
        inserted = copy.deepcopy(source)
        inserted.attrib["min"] = str(start_column)
        inserted.attrib["max"] = str(start_column)
        cols.append(inserted)
    ordered = sorted(cols.findall("m:col", NS), key=lambda item: int(item.attrib.get("min", "0")))
    for item in list(cols):
        cols.remove(item)
    for item in ordered:
        cols.append(item)


def _add_blank_column_cells(root: ET.Element, target_column: str, source_column: str) -> None:
    for row in root.findall(".//m:sheetData/m:row", NS):
        row_number = row.attrib["r"]
        target_reference = f"{target_column}{row_number}"
        if any(cell.attrib.get("r") == target_reference for cell in row.findall("m:c", NS)):
            continue
        source_reference = f"{source_column}{row_number}"
        source = next((cell for cell in row.findall("m:c", NS) if cell.attrib.get("r") == source_reference), None)
        if source is None:
            continue
        cell = copy.deepcopy(source)
        cell.attrib["r"] = target_reference
        cell.attrib.pop("t", None)
        for child in list(cell):
            if child.tag in {f"{{{MAIN_NS}}}v", f"{{{MAIN_NS}}}f", f"{{{MAIN_NS}}}is"}:
                cell.remove(child)
        row.append(cell)
        cells = sorted(
            row.findall("m:c", NS),
            key=lambda candidate: column_number(re.match(r"[A-Z]+", candidate.attrib["r"]).group(0)),
        )
        for item in list(row.findall("m:c", NS)):
            row.remove(item)
        for item in cells:
            row.append(item)


def add_temu6_column(template: Path = TEMPLATE) -> None:
    """Add the missing Temu 店铺6 column to the active template once."""
    with zipfile.ZipFile(template) as source:
        sheet_path = _sheet_paths(source)["订单库存总表"]
        sheet = _read_xml_text(source.read(sheet_path))
        rows = {int(row.attrib["r"]): row for row in sheet.findall(".//m:sheetData/m:row", NS)}
        header = next(
            (cell for cell in rows[5].findall("m:c", NS) if cell.attrib.get("r") == "AY5"),
            None,
        )
        if header is None:
            raise ValueError("Stock_Order 模板缺少 AY5，无法识别当前 Temu 列布局")
        shared_strings = _shared_strings(source)
        header_text = _cell_value(header, shared_strings)
        if header_text == "\u5e97\u94fa6":
            return
        if header_text not in {"\u5e97\u92ea7", "\u5e97\u94fa7"}:
            raise ValueError(f"AY5 is not a Temu store 7 header: {header_text!r}")
        if not TEMU6_BACKUP.exists():
            shutil.copy2(template, TEMU6_BACKUP)
        insert_column(sheet, column_number("AY"))
        _insert_column_dimensions(sheet, column_number("AY"))
        _add_blank_column_cells(sheet, "AY", "AZ")
        rows = {int(row.attrib["r"]): row for row in sheet.findall(".//m:sheetData/m:row", NS)}
        set_inline_text(rows[5], "AY5", "\u5e97\u94fa6", "AW5")
        merge_cells = sheet.find("m:mergeCells", NS)
        for merge in merge_cells.findall("m:mergeCell", NS) if merge_cells is not None else []:
            if merge.attrib.get("ref") == "AW4:AY4":
                merge.attrib["ref"] = "AW4:AZ4"
        updated_sheet = ET.tostring(sheet, encoding="utf-8", xml_declaration=True)
        with tempfile.NamedTemporaryFile(delete=False, suffix=".xlsx") as handle:
            temporary = Path(handle.name)
        try:
            with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED) as output:
                for item in source.infolist():
                    output.writestr(item, updated_sheet if item.filename == sheet_path else source.read(item.filename))
            shutil.move(temporary, template)
        finally:
            temporary.unlink(missing_ok=True)


def upgrade_template(template: Path = TEMPLATE) -> None:
    if not BACKUP.exists():
        shutil.copy2(template, BACKUP)
    with zipfile.ZipFile(template) as source:
        sheet_path = _sheet_paths(source)["订单库存总表"]
        sheet = _read_xml_text(source.read(sheet_path))
        insert_column(sheet, column_number("AU"))
        insert_column(sheet, column_number("AY"))
        rows = {int(row.attrib["r"]): row for row in sheet.findall(".//m:sheetData/m:row", NS)}
        set_inline_text(rows[5], "AU5", "店铺7", "AV5")
        set_inline_text(rows[5], "AY5", "店铺7", "AX5")
        merge_cells = sheet.find("m:mergeCells", NS)
        for merge in merge_cells.findall("m:mergeCell", NS) if merge_cells is not None else []:
            if merge.attrib.get("ref") == "AW4:AX4":
                merge.attrib["ref"] = "AW4:AY4"
        updated_sheet = ET.tostring(sheet, encoding="utf-8", xml_declaration=True)
        with tempfile.NamedTemporaryFile(delete=False, suffix=".xlsx") as handle:
            temporary = Path(handle.name)
        try:
            with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED) as output:
                for item in source.infolist():
                    output.writestr(item, updated_sheet if item.filename == sheet_path else source.read(item.filename))
            shutil.move(temporary, template)
        finally:
            temporary.unlink(missing_ok=True)


if __name__ == "__main__":
    add_temu6_column()
