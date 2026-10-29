import json
import unittest

from inventory_pipeline import build_target_updates, filter_changed_updates
from wecom_sync import filter_online_changed_updates, write_ordinary_sheet_updates


class CategoryColumnLayoutTests(unittest.TestCase):
    def test_new_meli_full7_column_shifts_all_later_stock_fields_once(self):
        from inventory_pipeline import FULL_COLUMNS, PERIOD_COLUMNS, STORE_COLUMNS

        updates = build_target_updates(
            ["SKU-1"],
            {"3天": {"SKU-1": 3}, "7天": {"SKU-1": 7},
             "15天": {"SKU-1": 15}, "30天": {"SKU-1": 30}},
            {
                ("SKU-1", "Meli 店铺1"): 101,
                ("SKU-1", "Meli 店铺8"): 108,
                ("SKU-1", "Shopee 店铺7"): 207,
                ("SKU-1", "Temu店铺7"): 307,
            },
            {
                ("SKU-1", "Meli 店铺7"): 17,
                ("SKU-1", "Meli 店铺8"): 18,
            },
            {"SKU-1": {"occupied": 11, "unavailable": 12, "available": 13}},
        )["SKU-1"]

        self.assertEqual(FULL_COLUMNS["Meli 店铺7"], "W")
        self.assertEqual(FULL_COLUMNS["Meli 店铺8"], "X")
        self.assertEqual(PERIOD_COLUMNS, {"3天": "AD", "7天": "AE", "15天": "AF", "30天": "AG"})
        self.assertEqual((updates["W"], updates["X"], updates["Y"], updates["Z"], updates["AA"]), (17, 18, 11, 12, 13))
        self.assertEqual((updates["AD"], updates["AE"], updates["AF"], updates["AG"]), (3, 7, 15, 30))
        self.assertEqual((updates["AM"], updates["AR"], updates["AY"], updates["BD"]), (101, 108, 207, 307))
        self.assertEqual(STORE_COLUMNS["Meli 店铺1"], "AM")
        self.assertEqual(STORE_COLUMNS["Shopee 店铺7"], "AY")
        self.assertEqual(STORE_COLUMNS["Temu店铺7"], "BD")
        self.assertNotIn("AB", updates)  # total-stock formula stays in the template.

    def test_new_category_column_keeps_arrival_out_of_inventory_updates(self):
        row = build_target_updates(
            ["SKU-1"],
            {"3天": {"SKU-1": 3}, "7天": {"SKU-1": 7},
             "15天": {"SKU-1": 15}, "30天": {"SKU-1": 30}},
            {("SKU-1", "Shopee 店铺7"): 17, ("SKU-1", "Temu店铺7"): 27},
            {("SKU-1", "Meli 店铺1"): 4},
            {"SKU-1": {"occupied": 11, "unavailable": 12, "available": 13}},
        )["SKU-1"]
        self.assertNotIn("R", row)  # R is now the user's arrival quantity.
        self.assertEqual({c: row[c] for c in ("S", "W", "X", "Y", "Z", "AA")},
                         {"S": 4, "W": "", "X": "", "Y": 11, "Z": 12, "AA": 13})
        self.assertEqual([row[c] for c in ("AD", "AE", "AF", "AG")], [3, 7, 15, 30])
        self.assertEqual((row["AY"], row["BD"]), (17, 27))

    def test_mcp_targets_shifted_blocks_without_overwriting_arrival_or_formulas(self):
        class Client:
            def __init__(self): self.grids = []
            def call_tool(self, name, arguments):
                self.grids.append(arguments["grid_data"])
                return {"content": [{"type": "text", "text": json.dumps({"errcode": 0})}]}

        client = Client()
        write_ordinary_sheet_updates(
            client, "https://doc.weixin.qq.com/sheet/example", "s1",
            [{"row_index": 3, "SKU": "SKU-1"}],
            {"SKU-1": {"S": 4, "AD": 3, "AM": 9, "R": 999, "AB": 999}},
        )
        self.assertEqual([g["start_column"] for g in client.grids], [18, 29, 38])
        self.assertEqual([len(g["rows"][0]["values"]) for g in client.grids], [9, 4, 18])
        self.assertEqual([g["rows"][0]["values"][0]["cell_value"].get("number")
                          for g in client.grids], [4, 3, 9])

    def test_unmatched_full_stock_zero_must_be_cleared_to_blank(self):
        desired = {"SKU-1": {"S": ""}}
        self.assertEqual(filter_changed_updates({"SKU-1": {"S": 0}}, desired), desired)
        self.assertEqual(filter_online_changed_updates(
            [{"SKU": "SKU-1", "S": "0"}], desired), desired)


if __name__ == "__main__":
    unittest.main()
