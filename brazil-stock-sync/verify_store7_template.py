from __future__ import annotations

import json
import re
import zipfile

import server
from inventory_pipeline import NS, _cell_value, _read_xml_text, _shared_strings, _sheet_paths


workbook, report = server.process_files(server.portal_source_manifest(), include_writeback_updates=True)
if workbook is None or report.get("errors"):
    raise RuntimeError(str(report.get("errors")))
with zipfile.ZipFile(__import__("io").BytesIO(workbook)) as archive:
    sheet = _read_xml_text(archive.read(_sheet_paths(archive)["订单库存总表"]))
    shared = _shared_strings(archive)
    wanted = {"AU5", "AV5", "AW5", "AX5", "AY5", "K6", "L6"}
    values = {}
    for cell in sheet.findall(".//m:c", NS):
        if cell.attrib.get("r") in wanted:
            formula = cell.find("m:f", NS)
            values[cell.attrib["r"]] = formula.text if formula is not None else _cell_value(cell, shared)
print(json.dumps({"rows": report["summary"]["target_rows"], "values": values}, ensure_ascii=False))
