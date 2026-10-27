import json
import unittest

from inventory_pipeline import build_target_updates, filter_changed_updates
from wecom_sync import filter_online_changed_updates, write_ordinary_sheet_updates


class CategoryColumnLayoutTests(unittest.TestCase):
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
        self.assertEqual({c: row[c] for c in ("S", "W", "X", "Y", "Z")},
                         {"S": 4, "W": "", "X": 11, "Y": 12, "Z": 13})
        self.assertEqual([row[c] for c in ("AC", "AD", "AE", "AF")], [3, 7, 15, 30])
        self.assertEqual((row["AX"], row["BC"]), (17, 27))

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
            {"SKU-1": {"S": 4, "AC": 3, "AL": 9, "R": 999, "AA": 999}},
        )
        self.assertEqual([g["start_column"] for g in client.grids], [18, 28, 37])
        self.assertEqual([len(g["rows"][0]["values"]) for g in client.grids], [8, 4, 18])
        self.assertEqual([g["rows"][0]["values"][0]["cell_value"]["number"]
                          for g in client.grids], [4, 3, 9])

    def test_unmatched_full_stock_zero_must_be_cleared_to_blank(self):
        desired = {"SKU-1": {"S": ""}}
        self.assertEqual(filter_changed_updates({"SKU-1": {"S": 0}}, desired), desired)
        self.assertEqual(filter_online_changed_updates(
            [{"SKU": "SKU-1", "S": "0"}], desired), desired)


if __name__ == "__main__":
    unittest.main()
