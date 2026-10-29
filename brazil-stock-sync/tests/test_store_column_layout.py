from __future__ import annotations

import unittest
import re
import zipfile
import tempfile
from pathlib import Path
from xml.etree import ElementTree as ET

from inventory_pipeline import NS, STORE_COLUMNS, _cell_value, _read_xml_text, _shared_strings, _sheet_paths


class StoreColumnLayoutTests(unittest.TestCase):
    def test_layout_validation_rejects_a_total_formula_left_on_the_pre_insert_range(self) -> None:
        from inventory_pipeline import validate_target_layout

        template = Path(__file__).resolve().parents[1] / "runtime" / "stock_order_template_fixed_layout.xlsx"
        with tempfile.TemporaryDirectory() as temp_dir:
            corrupted = Path(temp_dir) / "wrong_total_formula.xlsx"
            with zipfile.ZipFile(template) as source, zipfile.ZipFile(corrupted, "w", zipfile.ZIP_DEFLATED) as target:
                main_sheet_path = _sheet_paths(source)["订单库存总表"]
                for item in source.infolist():
                    data = source.read(item.filename)
                    if item.filename == main_sheet_path:
                        root = _read_xml_text(data)
                        cell = next(cell for cell in root.findall(".//m:c", NS) if cell.attrib.get("r") == "AB4")
                        cell.find("m:f", NS).text = "SUM(S4:Z4)"
                        data = ET.tostring(root, encoding="utf-8", xml_declaration=True)
                    target.writestr(item, data)

            errors = validate_target_layout(corrupted)
            self.assertTrue(any("AB4" in error and "公式" in error for error in errors))

    def test_generation_preserves_new_template_formulas_and_other_sheets(self):
        import io
        import server
        from inventory_pipeline import process_files
        files = server.portal_source_manifest()
        workbook, report = process_files(files, include_writeback_updates=True)
        self.assertEqual(report.get("errors"), [])
        self.assertEqual(report["summary"]["target_rows"], 1212)
        self.assertIn("NW250401DEFCQI", report["writeback_all_updates"])
        self.assertIn("NW250402DEFVER", report["writeback_all_updates"])
        with zipfile.ZipFile(files["target"]) as original, zipfile.ZipFile(io.BytesIO(workbook)) as generated:
            paths = _sheet_paths(original)
            for title, path in paths.items():
                if title != "订单库存总表":
                    self.assertEqual(original.read(path), generated.read(path))
            def formulas(archive):
                root = _read_xml_text(archive.read(paths["订单库存总表"]))
                return {cell.attrib["r"]: cell.find("m:f", NS).text for cell in root.findall(".//m:c", NS) if cell.find("m:f", NS) is not None}
            self.assertEqual(formulas(original), formulas(generated))

    @staticmethod
    def _normalize_store_label(value: str | None) -> str:
        return str(value or "").replace("\u92ea", "\u94fa")

    def test_current_stock_store_columns_include_shopee7_and_temu7(self) -> None:
        expected = {
            "Meli 店铺7": "AQ",
            "Meli 店铺8": "AR",
            "Shopee 店铺7": "AY",
            "Shopee 店铺8": "AZ",
            "Temu店铺1": "BA",
            "Temu店铺2": "BB",
            "Temu店铺6": "BC",
            "Temu 店铺6": "BC",
            "Temu店铺7": "BD",
        }
        for store, column in expected.items():
            self.assertEqual(STORE_COLUMNS.get(store), column)

        from inventory_pipeline import FULL_COLUMNS, PERIOD_COLUMNS
        self.assertEqual(FULL_COLUMNS, {
            "Meli 店铺1": "S", "Meli 店铺2": "T", "Meli 店铺3": "U",
            "Meli 店铺6": "V", "Meli 店铺7": "W", "Meli 店铺8": "X",
        })
        self.assertEqual(PERIOD_COLUMNS, {"3天": "AD", "7天": "AE", "15天": "AF", "30天": "AG"})

    def test_template_matches_current_store_column_layout(self) -> None:
        template = Path(__file__).resolve().parents[1] / "runtime" / "stock_order_template_fixed_layout.xlsx"
        with zipfile.ZipFile(template) as archive:
            worksheet = _read_xml_text(archive.read(_sheet_paths(archive)["订单库存总表"]))
            shared = _shared_strings(archive)
            cells = {
                cell.attrib["r"]: _cell_value(cell, shared)
                for cell in worksheet.findall(".//m:c", NS)
            if re.fullmatch(r"(?:W|X|AQ|AR|AY|AZ|BA|BB|BC|BD)[23]", cell.attrib.get("r", ""))
            }
        self.assertEqual(cells.get("W2"), "Meli Full店铺7库存")
        self.assertEqual(cells.get("X2"), "Meli Full店铺8库存")
        self.assertEqual(self._normalize_store_label(cells.get("AQ3")), "店铺7")
        self.assertEqual(self._normalize_store_label(cells.get("AR3")), "店铺8")
        self.assertEqual(self._normalize_store_label(cells.get("AY3")), "店铺7")
        self.assertEqual(self._normalize_store_label(cells.get("AZ3")), "店铺8")
        self.assertEqual(self._normalize_store_label(cells.get("BA3")), "店铺1")
        self.assertEqual(self._normalize_store_label(cells.get("BB3")), "店铺2")
        self.assertEqual(self._normalize_store_label(cells.get("BC3")), "店铺6")
        self.assertEqual(self._normalize_store_label(cells.get("BD3")), "店铺7")


if __name__ == "__main__":
    unittest.main()
