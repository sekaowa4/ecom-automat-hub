from __future__ import annotations

import argparse
import json
import sys
from getpass import getpass

from .audit import AuditLog
from .collector import ZiniaoAgentCollector
from .config import load_config
from .credentials import CredentialStore
from .ledger import BatchLedger
from .mail import MailClient
from .notify import WeComNotifier
from .runner import AutomationRunner


WECOM_WEBHOOK_CREDENTIAL = "wecom-webhook"


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Collect and send shop authorization codes")
    parser.add_argument("--config", default="config.toml", help="Path to TOML configuration")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("setup", help="Save mailbox token and establish browser sessions")
    commands.add_parser("dry-run", help="Collect and preview without sending")
    commands.add_parser("run", help="Collect, validate, send, and verify")
    return parser


def _make_collector(settings):
    return ZiniaoAgentCollector(
        settings.stores,
        settings.rules.code_regex,
    )


def _setup(settings) -> None:
    for store in settings.stores:
        store.profile_dir.mkdir(parents=True, exist_ok=True)

    credentials = CredentialStore()
    secret = getpass(f"请输入 163 邮箱客户端授权码 ({settings.mail.sender}): ")
    MailClient(settings.mail, secret).test_connection()
    credentials.set(settings.mail.sender, secret)
    _make_collector(settings).setup_sessions()
    print("setup 完成：邮箱连接和浏览器会话均已准备。")


def _execute(settings, send: bool) -> None:
    mail_client = None
    success_notifier = None
    if send:
        credentials = CredentialStore()
        secret = credentials.get(settings.mail.sender)
        mail_client = MailClient(settings.mail, secret)
        success_notifier = WeComNotifier(credentials.get(WECOM_WEBHOOK_CREDENTIAL))
    runner = AutomationRunner(
        settings,
        _make_collector(settings),
        mail_client,
        BatchLedger(settings.base_dir / "data" / "runs.json"),
        audit_log=AuditLog(settings.base_dir / "data" / "run.log"),
        success_notifier=success_notifier,
    )
    report = runner.execute(send=send)
    payload = report.as_dict()
    if not send:
        print(json.dumps(payload, ensure_ascii=False, indent=2))
    else:
        payload.pop("preview", None)
        print(json.dumps(payload, ensure_ascii=False, indent=2))


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        settings = load_config(args.config)
        if args.command == "setup":
            _setup(settings)
        elif args.command == "dry-run":
            _execute(settings, send=False)
        else:
            _execute(settings, send=True)
        return 0
    except KeyboardInterrupt:
        print("已取消。", file=sys.stderr)
        return 130
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
