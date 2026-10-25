import csv
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from oms_automation import (
    load_box_rules_csv,
    merge_container_with_box_rules,
    next_status,
    validate_batch,
    write_import_files,
)


def valid_batch():
    return {
        "batch_no": "SEA-2026-001",
        "items": [
            {
                "sku": "SKU-1",
                "name_pt": "Produto um",
                "name_zh": "产品一",
                "image_url": "https://example.com/1.jpg",
                "length_cm": 10,
                "width_cm": 20,
                "height_cm": 30,
                "weight_g": 500,
                "quantity": 4,
                "platform_price": 100,
            }
        ],
    }


class ValidationTests(unittest.TestCase):
    def test_valid_batch_has_no_errors(self):
        self.assertEqual(validate_batch(valid_batch()), [])

    def test_duplicate_sku_blocks_batch(self):
        batch = valid_batch()
        batch["items"].append(dict(batch["items"][0]))
        self.assertIn("重复 SKU: SKU-1", validate_batch(batch))

    def test_non_positive_measurement_blocks_batch(self):
        batch = valid_batch()
        batch["items"][0]["weight_g"] = 0
        self.assertIn("SKU-1: weight_g 必须大于 0", validate_batch(batch))


class BoxRulesImportTests(unittest.TestCase):
    def test_converts_box_rules_columns_and_kg_to_g(self):
        source = (
            "产品,SKU,葡语名称,单套包装长,单套包装宽,单套包装高,单套含包装重量（kg）\n"
            "示例商品,SKU-BOX,Produto,10,20,30,0.5\n"
        )
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "箱规汇总表.csv"
            path.write_text(source, encoding="utf-8-sig")
            batch = load_box_rules_csv(path, "SEA-BOX-001")
        self.assertEqual(batch["batch_no"], "SEA-BOX-001")
        self.assertEqual(batch["items"][0]["name_zh"], "示例商品")
        self.assertEqual(batch["items"][0]["weight_g"], 500)
        self.assertEqual(batch["items"][0]["image_url"], "")

    def test_merges_container_quantity_with_box_rules_dimensions(self):
        master = (
            "产品,SKU,葡语名称,单套包装长,单套包装宽,单套包装高,单套含包装重量（kg）\n"
            "主数据商品,SKU-MERGE,Produto mestre,10,20,30,0.5\n"
        )
        container = (
            "产品,SKU,葡语名称,数量,长（整箱）,宽（整箱）,高（整箱）,个/箱,重量（整箱）,单价\n"
            "货柜商品,SKU-MERGE,Produto lote,40,50,60,70,4,8,100\n"
        )
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            master_path, container_path = root / "master.csv", root / "container.csv"
            master_path.write_text(master, encoding="utf-8-sig")
            container_path.write_text(container, encoding="utf-8-sig")
            batch, warnings = merge_container_with_box_rules(container_path, master_path, "SEA-MERGE-001")
        self.assertEqual(warnings, [])
        self.assertEqual(batch["items"][0]["quantity"], 40)
        self.assertEqual(batch["items"][0]["length_cm"], 10)
        self.assertEqual(batch["items"][0]["weight_g"], 500)
        self.assertEqual(batch["items"][0]["platform_price"], 100)


class WorkflowTests(unittest.TestCase):
    def test_review_approval_advances_to_processing(self):
        self.assertEqual(next_status("待入库审核", "approve"), "入库处理中")

    def test_invalid_transition_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "不允许的状态转换"):
            next_status("资料待补充", "approve")


class ExportTests(unittest.TestCase):
    def test_writes_oms_and_upseller_csv(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = write_import_files(valid_batch(), Path(directory))
            self.assertEqual([p.name for p in paths], ["SEA-2026-001-oms.csv", "SEA-2026-001-upseller.csv"])
            with paths[0].open(encoding="utf-8-sig", newline="") as handle:
                row = next(csv.DictReader(handle))
            self.assertEqual(row["SKU"], "SKU-1")
            self.assertEqual(row["数量"], "4")

    def test_validation_errors_prevent_export(self):
        batch = valid_batch()
        batch["items"][0]["quantity"] = 0
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, "quantity 必须大于 0"):
                write_import_files(batch, Path(directory))


class CommandLineTests(unittest.TestCase):
    def test_check_command_writes_report_and_import_files(self):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run(
                [sys.executable, "oms_automation.py", "check", "example_batch.json", "--out", directory],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
            )
            self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
            self.assertEqual(
                sorted(path.name for path in Path(directory).iterdir()),
                ["SEA-2026-001-oms.csv", "SEA-2026-001-report.json", "SEA-2026-001-upseller.csv"],
            )
            report = json.loads((Path(directory) / "SEA-2026-001-report.json").read_text(encoding="utf-8"))
            self.assertEqual(report["status"], "待校验")
            self.assertEqual(report["sku_data"], "SKU-001")

    def test_import_box_rules_command_writes_standard_batch_json(self):
        source = (
            "产品,SKU,葡语名称,单套包装长,单套包装宽,单套包装高,单套含包装重量（kg）\n"
            "示例商品,SKU-BOX,Produto,10,20,30,0.5\n"
        )
        with tempfile.TemporaryDirectory() as directory:
            source_path = Path(directory) / "box.csv"
            output_path = Path(directory) / "batch.json"
            source_path.write_text(source, encoding="utf-8-sig")
            result = subprocess.run(
                [sys.executable, "oms_automation.py", "import-box-rules", str(source_path),
                 "--batch-no", "SEA-BOX-001", "--out", str(output_path)],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
            )
            self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
            self.assertEqual(json.loads(output_path.read_text(encoding="utf-8"))["items"][0]["weight_g"], 500)

    def test_merge_container_command_writes_a_batch_and_warnings(self):
        master = (
            "产品,SKU,葡语名称,单套包装长,单套包装宽,单套包装高,单套含包装重量（kg）\n"
            "主数据商品,SKU-MERGE,Produto mestre,10,20,30,0.5\n"
        )
        container = (
            "产品,SKU,葡语名称,数量,长（整箱）,宽（整箱）,高（整箱）,个/箱,重量（整箱）\n"
            "货柜商品,SKU-MERGE,Produto lote,40,50,60,70,4,8\n"
        )
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            master_path, container_path, output_path = root / "master.csv", root / "container.csv", root / "batch.json"
            master_path.write_text(master, encoding="utf-8-sig")
            container_path.write_text(container, encoding="utf-8-sig")
            result = subprocess.run(
                [sys.executable, "oms_automation.py", "merge-container", str(container_path),
                 "--master", str(master_path), "--batch-no", "SEA-MERGE-001", "--out", str(output_path)],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
            )
            self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
            self.assertEqual(json.loads(output_path.read_text(encoding="utf-8"))["items"][0]["quantity"], 40)


if __name__ == "__main__":
    unittest.main()
