"""Narrow JSON bridge used by the browser worker for workbook operations."""
import json
import sys
from pathlib import Path
from .manifest import read_catalog, update_oms_export, build_single_import, verify_oms, prepare_attachments, verify_invoice_files


def main():
    action = sys.argv[1]
    args = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
    if action == "catalog":
        result = read_catalog(args["path"])
    elif action == "oms_update":
        result = update_oms_export(args["source"], args["destination"], args["items"])
    elif action == "oms_verify":
        result = verify_oms(args["source"], args["exported"], args["items"])
    elif action == "single_import":
        result = build_single_import(args["template"], args["destination"], args["catalog"], args["skus"])
    elif action == "attachments":
        result = prepare_attachments(args["source"], args["pdf"], args["xml"], args["output"], args["batch_name"])
    elif action == "invoice_verify":
        result = verify_invoice_files(args["pdf"], args["xml"], args["amount"])
    else:
        raise ValueError("未知文件处理操作")
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    main()
