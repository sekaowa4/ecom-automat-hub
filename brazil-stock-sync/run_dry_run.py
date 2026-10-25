import json
from pathlib import Path

from inventory_pipeline import process_files, report_json


ROOT = Path(__file__).resolve().parent
OUTPUT = ROOT / "预演结果"
OUTPUT.mkdir(exist_ok=True)

files = {
    "oms": Path(r"D:\Mic\OMS库存数据.xlsx"),
    "full": Path(r"D:\Mic\FULL库存数据.xlsx"),
    "pending": Path(r"D:\Mic\待开票数据.xlsx"),
    "3天": Path(r"D:\Mic\近3天销售数据.xlsx"),
    "7天": Path(r"D:\Mic\近7天销售数据.xlsx"),
    "15天": Path(r"D:\Mic\近15天销售数据.xlsx"),
    "30天": Path(r"D:\Mic\近30天销售数据.xlsx"),
    "target": Path(r"D:\桌面\Stock_Order.xlsx"),
}

workbook, report = process_files(files)
if workbook is None:
    raise SystemExit(report_json(report))

(OUTPUT / "Stock_Order_只读预演结果_自动重算版.xlsx").write_bytes(workbook)
(OUTPUT / "只读预演报告_自动重算版.json").write_text(report_json(report), encoding="utf-8")
print(json.dumps(report, ensure_ascii=False, indent=2))
