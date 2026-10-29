"""Copy only the dashboard connection files into a local coworker package."""

import argparse
import json
import shutil
from pathlib import Path


def _source_file(source: Path, value: object, field: str) -> Path:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"Dashboard config is missing {field}.")
    candidate = Path(value)
    if not candidate.is_absolute():
        candidate = source / candidate
    if not candidate.is_file():
        raise FileNotFoundError(f"Required dashboard workbook is unavailable: {candidate.name}")
    return candidate


def prepare(source: Path, destination: Path, batch_mapping: Path) -> dict:
    source = Path(source).resolve()
    destination = Path(destination).resolve()
    batch_mapping = Path(batch_mapping).resolve()
    config_path = source / "config.json"
    if not config_path.is_file():
        raise FileNotFoundError("The selected dashboard source has no config.json.")
    if not batch_mapping.is_file():
        raise FileNotFoundError("The built-in batch mapping workbook was not found.")

    config = json.loads(config_path.read_text(encoding="utf-8-sig"))
    shops = {}
    for name, shop in config.get("shops", {}).items():
        if not name.lower().startswith("meli"):
            continue
        if not isinstance(shop, dict) or not shop.get("upseller_name") or "target_cny" not in shop:
            raise ValueError(f"Meli dashboard entry is incomplete: {name}")
        shops[name] = {"upseller_name": shop["upseller_name"], "target_cny": shop["target_cny"]}
    if not shops:
        raise ValueError("The selected dashboard config contains no Meli shops.")
    if "brl_to_cny" not in config:
        raise ValueError("The selected dashboard config is missing brl_to_cny.")

    cookie = next((source / name for name in (".upseller_cookie", ".upseller_cookie.txt") if (source / name).is_file()), None)
    if cookie is None:
        raise FileNotFoundError("The selected dashboard source has no UpSeller login file.")
    classification = _source_file(source, config.get("sku_classification_file"), "sku_classification_file")
    profit = _source_file(source, config.get("profit_workbook_file"), "profit_workbook_file")

    bundled = destination / "每日消息推送"
    bundled.mkdir(parents=True, exist_ok=True)
    safe_config = {
        "brl_to_cny": config["brl_to_cny"],
        "shops": shops,
        "sku_classification_file": "SKU分类表.xlsx",
        "profit_workbook_file": "利润工作簿.xlsx",
    }
    (bundled / "config.json").write_text(json.dumps(safe_config, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    shutil.copyfile(cookie, bundled / cookie.name)
    shutil.copyfile(classification, bundled / "SKU分类表.xlsx")
    shutil.copyfile(profit, bundled / "利润工作簿.xlsx")
    shutil.copyfile(batch_mapping, destination / "批次对应表.xlsx")
    return {"files": 5, "shop_count": len(shops)}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--destination", required=True, type=Path)
    parser.add_argument("--batch-mapping", required=True, type=Path)
    args = parser.parse_args()
    result = prepare(args.source, args.destination, args.batch_mapping)
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    main()
