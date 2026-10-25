from __future__ import annotations

import json
import socket

import server
from daily_update import open_online_sheet
from upseller_api_export import load_us_u
from wecom_sync import WeComMCPClient, load_local_doc_mcp_endpoint


def check_local_state() -> list[tuple[str, str, bool]]:
    checks: list[tuple[str, str, bool]] = []
    config = json.loads(server.WECOM_CONFIG_PATH.read_text(encoding="utf-8")) if server.WECOM_CONFIG_PATH.exists() else {}
    sheet_url = str(config.get("sheet_url") or "")
    automation_config_path = server.ROOT / "runtime" / "automation_config.json"
    automation_config = json.loads(automation_config_path.read_text(encoding="utf-8")) if automation_config_path.exists() else {}
    webhook_url = str(automation_config.get("webhook_url") or "")
    checks.append(("目标在线表", "已配置正式表链接" if sheet_url.startswith("https://doc.weixin.qq.com/") else "未配置有效在线表链接", sheet_url.startswith("https://doc.weixin.qq.com/")))
    try:
        load_us_u()
        checks.append(("UpSeller 接口 Cookie", "已配置", True))
    except Exception:
        checks.append(("UpSeller 接口 Cookie", "未配置", False))
    checks.append(("企微群 Webhook", "已配置" if webhook_url.startswith("https://qyapi.weixin.qq.com/cgi-bin/webhook/send?") else "未配置有效 Webhook", webhook_url.startswith("https://qyapi.weixin.qq.com/cgi-bin/webhook/send?")))
    checks.append(("MCP 配置文件", "已找到本机授权配置" if (server.WECOM_CLI_CONFIG_DIR / "mcp_config.enc").exists() else "未找到授权配置", (server.WECOM_CLI_CONFIG_DIR / "mcp_config.enc").exists()))
    stale = []
    for path in sorted(server.JOB_DIR.glob("*.json"), key=lambda item: item.stat().st_mtime, reverse=True):
        try:
            if json.loads(path.read_text(encoding="utf-8")).get("status") == "running":
                stale.append(path.stem)
        except (OSError, json.JSONDecodeError):
            continue
    checks.append(("残留运行任务", "无残留任务" if not stale else f"发现残留任务：{', '.join(stale[:3])}", not stale))
    return checks


def _port_open(port: int) -> bool:
    with socket.socket() as sock:
        sock.settimeout(0.5)
        return sock.connect_ex(("127.0.0.1", port)) == 0


def run_preflight() -> int:
    print("巴西库存更新：运行前全面检查")
    results = check_local_state()
    for name, detail, ok in results:
        print(f"[{'通过' if ok else '失败'}] {name}：{detail}")

    try:
        endpoint = load_local_doc_mcp_endpoint(server.WECOM_CLI_CONFIG_DIR)
        config = json.loads(server.WECOM_CONFIG_PATH.read_text(encoding="utf-8"))
        client, sheet_id, rows = open_online_sheet(endpoint, config["sheet_url"], attempts=2, delay=1)
        print(f"[通过] 企业微信在线表读取：成功读取 {len(rows)} 行，sheet_id={sheet_id}")
    except Exception as exc:
        print(f"[失败] 企业微信在线表读取：{exc}")
        return 1

    return 1 if any(not ok for _, _, ok in results) else 0


if __name__ == "__main__":
    raise SystemExit(run_preflight())
