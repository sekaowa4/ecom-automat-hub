import tempfile
import unittest
from pathlib import Path
from xml.etree import ElementTree as ET

from inventory_pipeline import (
    aggregate_by_sku,
    aggregate_by_sku_store,
    aggregate_inventory_by_sku,
    aggregate_oms_by_sku,
    attach_writeback_updates,
    build_target_updates,
    build_update_label_style,
    filter_changed_updates,
    build_key_store_formulas,
    build_replenishment_formulas,
    beijing_update_label,
    build_workbench_preview_row,
    enable_full_recalculation,
    map_products_to_skus,
    map_products_to_skus_with_status,
    sales_sku_candidates,
    index_sheet_rows,
    normalize_store,
    validate_source_files,
)


class InventoryPipelineTests(unittest.TestCase):
    def test_replenishment_formulas_follow_current_stock_template_columns(self):
        formulas = build_replenishment_formulas(6)

        self.assertEqual(
            formulas["BE"],
            '=IF(BF6<=0,"无库存",IF(AB6+AC6<AH6*BG6*BI6,"建议补货",IF(AND(AH6*BG6*BI6<=AB6+AC6,AB6+AC6<=AH6*(BG6+BJ6)*BI6),"预警补货","无需补货")))',
        )
        self.assertEqual(
            formulas["BF"],
            '=IF(OR(G6="B"),0,ROUND((BG6+BJ6)*AH6*BI6-AA6-AC6,0))',
        )
        self.assertNotIn("BC", formulas)

    def test_update_label_style_uses_18_point_standard_red_font(self):
        styles = b'''<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="1"><font><sz val="10"/></font></fonts>
          <cellXfs count="1"><xf fontId="0"/></cellXfs>
        </styleSheet>'''

        updated, style_index = build_update_label_style(styles, 0)
        root = ET.fromstring(updated)
        ns = {"m": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
        fonts = root.find("m:fonts", ns)
        xfs = root.find("m:cellXfs", ns)

        self.assertEqual(style_index, 1)
        self.assertEqual(fonts.attrib["count"], "2")
        self.assertEqual(xfs.attrib["count"], "2")
        self.assertEqual(fonts.findall("m:font", ns)[1].find("m:sz", ns).attrib["val"], "18")
        self.assertEqual(fonts.findall("m:font", ns)[1].find("m:color", ns).attrib["rgb"], "FFFF0000")
        self.assertEqual(xfs.findall("m:xf", ns)[1].attrib["fontId"], "1")

    def test_filters_writeback_to_only_changed_cells(self):
        current = {"SKU-1": {"S": 10, "T": "2", "AC": 0}, "SKU-2": {"S": 1}}
        desired = {"SKU-1": {"S": 10, "T": 3, "AC": 0}, "SKU-2": {"S": 1}}

        self.assertEqual(filter_changed_updates(current, desired), {"SKU-1": {"T": 3}})

    def test_attaches_writeback_updates_only_when_requested(self):
        report = {"errors": []}
        updates = {"A": {"S": 1}}
        self.assertNotIn("writeback_updates", attach_writeback_updates(dict(report), updates, False))
        self.assertEqual(attach_writeback_updates(dict(report), updates, True)["writeback_updates"], updates)
    def test_beijing_update_label_uses_beijing_time(self):
        from datetime import datetime, timezone

        self.assertEqual(
            beijing_update_label(datetime(2026, 8, 2, 14, 5, tzinfo=timezone.utc)),
            "最近更新时间：8.2",
        )

    def test_sales_exports_accept_parent_sku_column(self):
        self.assertEqual(sales_sku_candidates(), ["SKU", "商品SKU", "父SKU"])

    def test_validate_source_files_reports_missing_inputs(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            (root / "oms.xlsx").write_bytes(b"placeholder")
            (root / "Stock_Order.xlsx").write_bytes(b"placeholder")
            result = validate_source_files({"target": root / "Stock_Order.xlsx"})
        self.assertEqual(result["ok"], False)
        self.assertEqual(
            result["missing"],
            ["inventory", "full", "\u0033\u5929", "\u0037\u5929", "\u0031\u0035\u5929", "\u0033\u0030\u5929"],
        )

    def test_product_to_sku_mapping_reports_exact_normalized_and_unmatched(self):
        result = map_products_to_skus_with_status(
            ["Exact", "Trimmed ", "Missing"],
            [
                {"Product": "Exact", "SKU": "SKU-1"},
                {"Product": " Trimmed ", "SKU": "SKU-2"},
            ],
            "Product",
            "SKU",
        )

        self.assertEqual(
            result,
            [
                {"product": "Exact", "sku": "SKU-1", "status": "exact"},
                {"product": "Trimmed ", "sku": "SKU-2", "status": "normalized"},
                {"product": "Missing", "sku": "", "status": "unmatched"},
            ],
        )

    def test_aggregate_oms_by_sku_sums_rows_from_multiple_warehouses(self):
        rows = [
            {"SKU": "A-1", "locked": 2, "available": 10, "defective": 1},
            {"SKU": "A-1", "locked": 3, "available": 7, "defective": 0},
        ]

        self.assertEqual(
            aggregate_oms_by_sku(rows, "SKU", "locked", "available", "defective"),
            {"A-1": {"locked": 5, "available": 17, "defective": 1}},
        )

    def test_aggregate_inventory_by_sku_uses_the_three_sop_columns(self):
        rows = [
            {"SKU": "A-1", "UpSeller占用数": 2, "UpSeller可用库存": 10, "三方仓不可用": 1},
            {"SKU": "A-1", "UpSeller占用数": 3, "UpSeller可用库存": 7, "三方仓不可用": 0},
        ]

        self.assertEqual(
            aggregate_inventory_by_sku(rows, "SKU", "UpSeller占用数", "UpSeller可用库存", "三方仓不可用"),
            {"A-1": {"occupied": 5, "available": 17, "unavailable": 1}},
        )

    def test_aggregate_by_sku_sums_duplicate_skus(self):
        rows = [
            {"SKU": "A-1", "数量": 2},
            {"SKU": "A-1", "数量": 3},
            {"SKU": "B-2", "数量": 4},
        ]

        self.assertEqual(aggregate_by_sku(rows, "SKU", "数量"), {"A-1": 5, "B-2": 4})

    def test_store_normalization_removes_marketplace_suffix(self):
        self.assertEqual(normalize_store("Meli 店铺1[mercado BR]"), "Meli 店铺1")
        self.assertEqual(normalize_store("Shopee 店铺2[shopee BR]"), "Shopee 店铺2")
        self.assertEqual(normalize_store("Meli 店铺1[Mercado Libre BR]"), "Meli 店铺1")

    def test_aggregate_by_sku_store_keeps_store_dimensions(self):
        rows = [
            {"SKU": "A-1", "店铺": "Meli 店铺1[mercado BR]", "销量": 2},
            {"SKU": "A-1", "店铺": "Meli 店铺1[mercado BR]", "销量": 3},
            {"SKU": "A-1", "店铺": "Meli 店铺2[mercado BR]", "销量": 4},
        ]

        self.assertEqual(
            aggregate_by_sku_store(rows, "SKU", "店铺", "销量"),
            {("A-1", "Meli 店铺1"): 5, ("A-1", "Meli 店铺2"): 4},
        )

    def test_store_aliases_that_share_a_stock_column_are_summed(self):
        updates = build_target_updates(
            target_skus=["A-1"],
            sales_by_period={},
            sales_by_store={
                ("A-1", "Temu店铺6"): 5,
                ("A-1", "Temu 店铺6"): 3,
            },
            full_by_store={},
            inventory_by_sku={},
        )

        self.assertEqual(updates["A-1"]["BC"], 8)

    def test_build_target_updates_leaves_missing_full_values_blank(self):
        target_skus = ["A-1", "MISSING"]
        updates = build_target_updates(
            target_skus=target_skus,
            sales_by_period={"3天": {"A-1": 2}},
            sales_by_store={("A-1", "Meli 店铺1"): 2},
            full_by_store={("A-1", "Meli 店铺1"): 7, ("A-1", "Meli 店铺8"): 3},
            inventory_by_sku={"A-1": {"occupied": 1, "unavailable": 0, "available": 10}},
        )

        self.assertEqual(updates["A-1"]["AD"], 2)
        self.assertEqual(updates["A-1"]["S"], 7)
        self.assertEqual(updates["A-1"]["X"], 3)
        self.assertEqual(updates["A-1"]["Y"], 1)
        self.assertEqual(updates["A-1"]["AA"], 10)
        self.assertEqual(updates["MISSING"]["AD"], 0)
        self.assertEqual(updates["MISSING"]["X"], "")

    def test_missing_meli_full_store_values_are_blank_for_all_stores(self):
        updates = build_target_updates(
            target_skus=["A-1", "MISSING"],
            sales_by_period={},
            sales_by_store={},
            full_by_store={
                ("A-1", "Meli 店铺1"): 7,
                ("A-1", "Meli 店铺8"): 3,
            },
            inventory_by_sku={},
        )

        self.assertEqual(updates["A-1"]["S"], 7)
        self.assertEqual(updates["A-1"]["T"], "")
        self.assertEqual(updates["A-1"]["U"], "")
        self.assertEqual(updates["A-1"]["V"], "")
        self.assertEqual(updates["A-1"]["W"], "")
        self.assertEqual(updates["A-1"]["X"], 3)
        self.assertEqual([updates["MISSING"][column] for column in ("S", "T", "U", "V", "W", "X")], ["", "", "", "", "", ""])

    def test_physical_store_does_not_populate_individual_store_column(self):
        updates = build_target_updates(
            target_skus=["A-1"],
            sales_by_period={"30天": {"A-1": 9}},
            sales_by_store={
                ("A-1", "Meli 店铺1"): 2,
                ("A-1", "默认店铺"): 7,
            },
            full_by_store={},
            inventory_by_sku={},
        )

        self.assertEqual(updates["A-1"]["AG"], 9)
        self.assertEqual(updates["A-1"]["AM"], 2)
        self.assertNotIn(7, [updates["A-1"].get(column) for column in ("AM", "AN", "AO", "AP", "AQ", "AR", "AS", "AT", "AU", "AV", "AW")])

    def test_missing_numeric_inputs_are_zero_for_formula_calculation(self):
        updates = build_target_updates(
            target_skus=["A-1"],
            sales_by_period={"3天": {}},
            sales_by_store={},
            full_by_store={},
            inventory_by_sku={},
        )

        self.assertEqual(updates["A-1"]["AD"], 0)
        self.assertEqual(updates["A-1"]["AE"], 0)
        self.assertEqual(updates["A-1"]["AF"], 0)
        self.assertEqual(updates["A-1"]["AG"], 0)

    def test_workbench_preview_uses_real_nw_sku_and_updated_inventory_values(self):
        preview = build_workbench_preview_row(
            {
                "A": "测试产品",
                "B": "NW260801TEST",
                "G": "A",
                "S": 0, "T": 0, "U": 0, "V": 0, "W": 0, "X": 0,
                "Y": 0, "Z": 0, "AA": 8,
                "AC": 0, "BG": 70, "BI": 1.1, "BJ": 30,
            },
            {"AD": 3, "AE": 7, "AF": 15, "AG": 30, "AA": 8},
            20,
        )

        self.assertEqual(preview["sku"], "NW260801TEST")
        self.assertEqual(preview["stock"], 8)
        self.assertEqual(preview["days"], 8)
        self.assertEqual(preview["status"], "重要")
        self.assertEqual(preview["advice"], "急需补货")
        self.assertTrue(preview["priority"])

    def test_workbench_preview_ignores_invalid_negative_arrival_days_for_status(self):
        preview = build_workbench_preview_row(
            {
                "A": "穿墙线",
                "B": "NW260457SA",
                "G": "Q",
                "S": 0, "T": 0, "U": 8, "V": 0, "W": 0, "X": 0,
                "Y": 0, "Z": 0, "AA": 3,
                "AC": 60, "BG": 70, "BI": 1.1, "BJ": 30,
            },
            {"AD": 1, "AE": 209, "AF": 114, "AG": 209, "AA": 3},
            -46213,
        )

        self.assertEqual(preview["status"], "紧急")

    def test_sheet_rows_can_be_indexed_once_for_bulk_patching(self):
        from xml.etree import ElementTree as ET

        root = ET.fromstring(
            '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
            '<sheetData><row r="6"/><row r="7"/></sheetData></worksheet>'
        )

        index = index_sheet_rows(root)

        self.assertEqual(sorted(index), [6, 7])
        self.assertEqual(index[6].attrib["r"], "6")

    def test_product_to_sku_mapping_uses_source_table_not_formula_cache(self):
        source_rows = [
            {"产品": "产品甲", "SKU": "SKU-甲"},
            {"产品": "产品乙", "SKU": "SKU-乙"},
        ]

        self.assertEqual(
            map_products_to_skus(["产品甲", "产品缺失"], source_rows, "产品", "SKU"),
            ["SKU-甲", ""],
        )

    def test_key_store_formulas_leave_blank_when_store_sales_are_zero(self):
        formulas = build_key_store_formulas(6)

        self.assertIn('IF(MAX(AM6:AR6)=0,"",', formulas["N"])
        self.assertIn('IF(MAX(AS6:AZ6)=0,"",', formulas["O"])
        self.assertIn('IF(MAX(BA6:BD6)=0,"",', formulas["P"])

    def test_key_store_formulas_are_valid_ooxml_horizontal_indexes(self):
        formulas = build_key_store_formulas(6)

        self.assertTrue(all(not formula.startswith("=") for formula in formulas.values()))
        self.assertIn(',1,MATCH(MAX(AM6:AR6)', formulas["N"])
        self.assertIn(',1,MATCH(MAX(AS6:AZ6)', formulas["O"])
        self.assertIn(',1,MATCH(MAX(BA6:BD6)', formulas["P"])
        self.assertEqual(formulas["AB"], "SUM(S6:AA6)")

    def test_workbook_recalculation_is_enabled_on_open(self):
        from xml.etree import ElementTree as ET

        root = ET.fromstring(
            '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheets/></workbook>'
        )
        updated = ET.fromstring(enable_full_recalculation(ET.tostring(root)))
        calc = updated.find("{http://schemas.openxmlformats.org/spreadsheetml/2006/main}calcPr")

        self.assertEqual(calc.attrib["calcMode"], "auto")
        self.assertEqual(calc.attrib["fullCalcOnLoad"], "1")
        self.assertEqual(calc.attrib["forceFullCalc"], "1")


if __name__ == "__main__":
    unittest.main()
