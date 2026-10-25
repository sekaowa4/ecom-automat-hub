from __future__ import annotations

import unittest
import re
import zipfile
from pathlib import Path

from inventory_pipeline import NS, STORE_COLUMNS, _cell_value, _read_xml_text, _shared_strings, _sheet_paths


class StoreColumnLayoutTests(unittest.TestCase):
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
            "Shopee 店铺7": "AW",
            "Shopee 店铺8": "AX",
            "Temu店铺1": "AY",
            "Temu店铺2": "AZ",
            "Temu店铺6": "BA",
            "Temu 店铺6": "BA",
            "Temu店铺7": "BB",
        }
        for store, column in expected.items():
            self.assertEqual(STORE_COLUMNS.get(store), column)

    def test_template_matches_current_store_column_layout(self) -> None:
        template = Path(__file__).resolve().parents[1] / "runtime" / "stock_order_template_fixed_layout.xlsx"
        with zipfile.ZipFile(template) as archive:
            worksheet = _read_xml_text(archive.read(_sheet_paths(archive)["订单库存总表"]))
            shared = _shared_strings(archive)
            cells = {
                cell.attrib["r"]: _cell_value(cell, shared)
                for cell in worksheet.findall(".//m:c", NS)
                if re.fullmatch(r"(?:AW|AX|AY|AZ|BA|BB)[23]", cell.attrib.get("r", ""))
            }
        self.assertEqual(self._normalize_store_label(cells.get("AW3")), "店铺7")
        self.assertEqual(self._normalize_store_label(cells.get("AX3")), "店铺8")
        self.assertEqual(self._normalize_store_label(cells.get("AY3")), "店铺1")
        self.assertEqual(self._normalize_store_label(cells.get("AZ3")), "店铺2")
        self.assertEqual(self._normalize_store_label(cells.get("BA3")), "店铺6")
        self.assertEqual(self._normalize_store_label(cells.get("BB3")), "店铺7")


if __name__ == "__main__":
    unittest.main()
