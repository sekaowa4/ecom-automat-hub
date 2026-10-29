import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("prepare_colleague_package", ROOT / "scripts" / "prepare-colleague-package.py")
PACKAGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PACKAGE)


class DashboardPackageTests(unittest.TestCase):
    def test_copies_only_dashboard_connection_cookie_workbooks_and_batch_mapping(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "daily-push"
            source.mkdir()
            destination = root / "package"
            classification = root / "private" / "classes.xlsx"
            profit = root / "private" / "profit.xlsx"
            classification.parent.mkdir()
            classification.write_bytes(b"classification-workbook")
            profit.write_bytes(b"profit-workbook")
            (source / "config.json").write_text(json.dumps({
                "brl_to_cny": 1.3,
                "shops": {
                    "Meli Shop 2": {"upseller_name": "Shop 2", "target_cny": 780, "private_note": "drop"},
                    "Shopee Shop": {"upseller_name": "drop", "target_cny": 999},
                },
                "sku_classification_file": str(classification),
                "profit_workbook_file": str(profit),
                "webhook": "drop-this-secret",
            }), encoding="utf-8")
            cookie = b"synthetic-cookie-value"
            (source / ".upseller_cookie.txt").write_bytes(cookie)
            (source / "api_trace.json").write_text("private trace", encoding="utf-8")
            (source / ".webhook").write_text("private webhook", encoding="utf-8")
            (source / "browser_profile").mkdir()
            batch = root / "批次对应表.xlsx"
            batch.write_bytes(b"batch-workbook")

            result = PACKAGE.prepare(source, destination, batch)
            bundled = destination / "每日消息推送"
            config = json.loads((bundled / "config.json").read_text(encoding="utf-8"))

            self.assertEqual({"brl_to_cny", "shops", "sku_classification_file", "profit_workbook_file"}, set(config))
            self.assertEqual({"Meli Shop 2"}, set(config["shops"]))
            self.assertEqual({"upseller_name", "target_cny"}, set(config["shops"]["Meli Shop 2"]))
            self.assertEqual("SKU分类表.xlsx", config["sku_classification_file"])
            self.assertEqual("利润工作簿.xlsx", config["profit_workbook_file"])
            self.assertEqual(cookie, (bundled / ".upseller_cookie.txt").read_bytes())
            self.assertEqual(b"classification-workbook", (bundled / "SKU分类表.xlsx").read_bytes())
            self.assertEqual(b"profit-workbook", (bundled / "利润工作簿.xlsx").read_bytes())
            self.assertEqual(b"batch-workbook", (destination / "批次对应表.xlsx").read_bytes())
            paths = {path.relative_to(destination).as_posix() for path in destination.rglob("*") if path.is_file()}
            self.assertEqual({
                "每日消息推送/config.json",
                "每日消息推送/.upseller_cookie.txt",
                "每日消息推送/SKU分类表.xlsx",
                "每日消息推送/利润工作簿.xlsx",
                "批次对应表.xlsx",
            }, paths)
            self.assertEqual(5, result["files"])


if __name__ == "__main__":
    unittest.main()
