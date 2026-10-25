"""Local-only setup helpers for moving this project to a new computer."""

from __future__ import annotations

import json
from pathlib import Path


def save_runtime_config(runtime_dir: Path, sheet_url: str, webhook_url: str) -> None:
    runtime_dir.mkdir(parents=True, exist_ok=True)
    (runtime_dir / "wecom_config.json").write_text(
        json.dumps({"sheet_url": sheet_url.strip()}, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    (runtime_dir / "automation_config.json").write_text(
        json.dumps({"webhook_url": webhook_url.strip()}, ensure_ascii=False, indent=2), encoding="utf-8"
    )


def main() -> int:
    root = Path(__file__).resolve().parent
    print("巴西库存更新：新电脑本机配置")
    print("请粘贴正式 Stock_Order 在线表链接和群机器人 Webhook；它们只保存到本机 runtime 目录。")
    sheet_url = input("正式 Stock_Order 链接: ").strip()
    webhook_url = input("企微群 Webhook: ").strip()
    if not sheet_url.startswith("https://doc.weixin.qq.com/"):
        raise SystemExit("未写入：Stock_Order 链接无效")
    if not webhook_url.startswith("https://qyapi.weixin.qq.com/cgi-bin/webhook/send?"):
        raise SystemExit("未写入：群 Webhook 链接无效")
    save_runtime_config(root / "runtime", sheet_url, webhook_url)
    print("在线表和群通知已配置。接下来请运行“配置UpSeller接口Cookie.cmd”，并在企业微信 CLI 中完成文档 MCP 授权。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
