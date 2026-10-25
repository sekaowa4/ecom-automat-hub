"""OMS 海运批次校验与导入文件生成。仅使用 Python 标准库。"""

from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path


REQUIRED_TEXT = ("sku", "name_pt", "name_zh", "image_url")
POSITIVE_NUMBERS = ("length_cm", "width_cm", "height_cm", "weight_g", "quantity")
TRANSITIONS = {
    ("资料待补充", "complete"): "待校验",
    ("待校验", "pass"): "待入库审核",
    ("待入库审核", "approve"): "入库处理中",
    ("入库处理中", "complete"): "待开票审核",
    ("待开票审核", "approve"): "开票处理中",
    ("开票处理中", "complete"): "附件回传",
    ("附件回传", "complete"): "已完成",
}


def validate_batch(batch: dict) -> list[str]:
    errors: list[str] = []
    if not str(batch.get("batch_no", "")).strip():
        errors.append("batch_no 不能为空")
    items = batch.get("items")
    if not isinstance(items, list) or not items:
        return errors + ["items 至少包含一条商品"]

    seen: set[str] = set()
    for index, item in enumerate(items, 1):
        sku = str(item.get("sku", "")).strip()
        label = sku or f"第 {index} 行"
        if sku in seen:
            errors.append(f"重复 SKU: {sku}")
        if sku:
            seen.add(sku)
        for field in REQUIRED_TEXT:
            if not str(item.get(field, "")).strip():
                errors.append(f"{label}: {field} 不能为空")
        for field in POSITIVE_NUMBERS:
            value = item.get(field)
            if isinstance(value, bool) or not isinstance(value, (int, float)) or value <= 0:
                errors.append(f"{label}: {field} 必须大于 0")
    return errors


def load_box_rules_csv(path: Path, batch_no: str) -> dict:
    """Map the verified 箱规汇总表 headers into the standard batch format."""
    with path.open(encoding="utf-8-sig", newline="") as handle:
        rows = csv.DictReader(handle)
        items = [
            {
                "sku": row.get("SKU", "").strip(),
                "name_pt": row.get("葡语名称", "").strip(),
                "name_zh": row.get("产品", "").strip(),
                "image_url": "",
                "length_cm": _number(row.get("单套包装长")),
                "width_cm": _number(row.get("单套包装宽")),
                "height_cm": _number(row.get("单套包装高")),
                "weight_g": _number(row.get("单套含包装重量（kg）")) * 1000,
                "quantity": 0,
                "platform_price": 0,
            }
            for row in rows
            if any(row.values())
        ]
    return {"batch_no": batch_no, "items": items}


def merge_container_with_box_rules(
    container_path: Path, master_path: Path, batch_no: str
) -> tuple[dict, list[str]]:
    """Combine a container's quantities with the master per-unit measurements."""
    with master_path.open(encoding="utf-8-sig", newline="") as handle:
        master_rows = list(csv.DictReader(handle))
    master_by_sku = {str(row.get("SKU", "")).strip(): row for row in master_rows if row.get("SKU")}

    warnings: list[str] = []
    items: list[dict] = []
    with container_path.open(encoding="utf-8-sig", newline="") as handle:
        for row in csv.DictReader(handle):
            sku = str(row.get("SKU", "")).strip()
            quantity = _number(row.get("数量"))
            if not sku or quantity <= 0:
                continue
            master = master_by_sku.get(sku)
            if master:
                length = _number(master.get("单套包装长"))
                width = _number(master.get("单套包装宽"))
                height = _number(master.get("单套包装高"))
                weight = _number(master.get("单套含包装重量（kg）")) * 1000
            elif _number(row.get("个/箱")) == 1:
                length = _number(row.get("长（整箱）"))
                width = _number(row.get("宽（整箱）"))
                height = _number(row.get("高（整箱）"))
                weight = _number(row.get("重量（整箱）")) * 1000
                warnings.append(f"{sku}: 未匹配箱规汇总表，使用单件整箱数据")
            else:
                length = width = height = weight = 0
                warnings.append(f"{sku}: 未匹配箱规汇总表")
            master_name_pt = _text(master.get("葡语名称")) if master else ""
            master_name_zh = _text(master.get("产品")) if master else ""
            items.append(
                {
                    "sku": sku,
                    "name_pt": _text(row.get("葡语名称")) or master_name_pt,
                    "name_zh": _text(row.get("产品")) or master_name_zh,
                    "image_url": "",
                    "length_cm": length,
                    "width_cm": width,
                    "height_cm": height,
                    "weight_g": weight,
                    "quantity": quantity,
                    "platform_price": _number(row.get("单价")),
                }
            )
    return {"batch_no": batch_no, "items": items}, warnings


def _number(value: str | None) -> float:
    try:
        return float((value or "").replace(",", "").strip())
    except ValueError:
        return 0


def _text(value: str | None) -> str:
    return str(value or "").strip()


def next_status(status: str, event: str) -> str:
    try:
        return TRANSITIONS[(status, event)]
    except KeyError as exc:
        raise ValueError(f"不允许的状态转换: {status} + {event}") from exc


def write_import_files(batch: dict, output_dir: Path) -> list[Path]:
    errors = validate_batch(batch)
    if errors:
        raise ValueError("; ".join(errors))
    output_dir.mkdir(parents=True, exist_ok=True)
    batch_no = batch["batch_no"]
    oms_path = output_dir / f"{batch_no}-oms.csv"
    upseller_path = output_dir / f"{batch_no}-upseller.csv"

    _write_csv(
        oms_path,
        ["SKU", "中文名", "长(cm)", "宽(cm)", "高(cm)", "重量(g)", "数量"],
        (
            [item["sku"], item["name_zh"], item["length_cm"], item["width_cm"],
             item["height_cm"], item["weight_g"], item["quantity"]]
            for item in batch["items"]
        ),
    )
    _write_csv(
        upseller_path,
        ["SKU", "葡语名称", "图片", "数量", "开票单价"],
        (
            [item["sku"], item["name_pt"], item["image_url"], item["quantity"],
             round(float(item.get("platform_price", 0)) * 0.2, 2)]
            for item in batch["items"]
        ),
    )
    return [oms_path, upseller_path]


def _write_csv(path: Path, headers: list[str], rows) -> None:
    with path.open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.writer(handle)
        writer.writerow(headers)
        writer.writerows(rows)


def task_summary(batch: dict, errors: list[str]) -> dict:
    return {
        "batch_no": batch.get("batch_no"),
        "status": "待校验" if not errors else "资料待补充",
        "sku_data": ", ".join(str(item.get("sku", "")) for item in batch.get("items", [])),
        "validation_result": "通过" if not errors else "; ".join(errors),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="校验 OMS 海运批次并生成导入文件")
    parser.add_argument("command", choices=["check", "import-box-rules", "merge-container"])
    parser.add_argument("input", type=Path)
    parser.add_argument("--out", type=Path, default=Path("output"))
    parser.add_argument("--batch-no")
    parser.add_argument("--master", type=Path)
    args = parser.parse_args()

    if args.command == "import-box-rules":
        if not args.batch_no:
            parser.error("import-box-rules 需要 --batch-no")
        args.out.write_text(
            json.dumps(load_box_rules_csv(args.input, args.batch_no), ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        print(f"已生成批次文件：{args.out}")
        return 0

    if args.command == "merge-container":
        if not args.batch_no or not args.master:
            parser.error("merge-container 需要 --batch-no 和 --master")
        batch, warnings = merge_container_with_box_rules(args.input, args.master, args.batch_no)
        args.out.write_text(json.dumps(batch, ensure_ascii=False, indent=2), encoding="utf-8")
        args.out.with_suffix(".warnings.json").write_text(
            json.dumps({"warnings": warnings}, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        print(f"已生成批次文件：{args.out}")
        return 0

    batch = json.loads(args.input.read_text(encoding="utf-8"))
    errors = validate_batch(batch)
    args.out.mkdir(parents=True, exist_ok=True)
    report_path = args.out / f"{batch.get('batch_no', 'unknown')}-report.json"
    report = {"valid": not errors, "errors": errors, **task_summary(batch, errors)}
    report_path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    if errors:
        print(f"校验失败，详见 {report_path}")
        return 1
    paths = write_import_files(batch, args.out)
    print("校验通过：" + ", ".join(str(path) for path in [report_path, *paths]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
