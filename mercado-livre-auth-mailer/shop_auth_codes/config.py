from __future__ import annotations

import tomllib
from dataclasses import dataclass
from pathlib import Path

from .mail import MailSettings


@dataclass(frozen=True)
class StoreSettings:
    name: str
    account: str
    profile_dir: Path
    start_url: str
    code_selector: str | None = None


@dataclass(frozen=True)
class Rules:
    timezone: str = "Asia/Shanghai"
    cutoff: str = "13:00"
    date_format: str = "YYYY/M/D"
    code_regex: str = r"^[A-Z0-9]{8}$"


@dataclass(frozen=True)
class PortalLabels:
    settings: str = "Configurações"
    sales_preferences: str = "Preferências de venda"
    authorization_code: str = "Código de autorização"
    get_code: str = "Obter código"


@dataclass(frozen=True)
class Settings:
    base_dir: Path
    stores: tuple[StoreSettings, ...]
    mail: MailSettings
    rules: Rules
    portal: PortalLabels


def _resolve(base_dir: Path, value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else (base_dir / path).resolve()


def load_config(path: str | Path) -> Settings:
    config_path = Path(path).resolve()
    data = tomllib.loads(config_path.read_text(encoding="utf-8"))
    base_dir = config_path.parent

    rule_data = data.get("rules", {})
    rules = Rules(
        timezone=rule_data.get("timezone", "Asia/Shanghai"),
        cutoff=rule_data.get("cutoff", "13:00"),
        date_format=rule_data.get("date_format", "YYYY/M/D"),
        code_regex=rule_data.get("code_regex", r"^[A-Z0-9]{8}$"),
    )

    mail_data = data.get("mail", {})
    mail = MailSettings(
        sender=mail_data["sender"],
        recipients=tuple(mail_data["recipients"]),
        subject=mail_data["subject"],
        template=_resolve(base_dir, mail_data["template"]),
        imap_host=mail_data.get("imap_host", "imap.163.com"),
        smtp_host=mail_data.get("smtp_host", "smtp.163.com"),
        imap_port=int(mail_data.get("imap_port", 993)),
        smtp_port=int(mail_data.get("smtp_port", 465)),
        imap_folder=mail_data.get("imap_folder", "Sent"),
    )

    stores = tuple(
        StoreSettings(
            name=item["name"],
            account=item["account"],
            profile_dir=_resolve(base_dir, item["profile_dir"]),
            start_url=item["start_url"],
            code_selector=item.get("code_selector") or None,
        )
        for item in data.get("stores", [])
    )
    if not stores:
        raise ValueError("At least one [[stores]] entry is required")

    portal_data = data.get("portal", {})
    portal = PortalLabels(
        settings=portal_data.get("settings", "Configurações"),
        sales_preferences=portal_data.get("sales_preferences", "Preferências de venda"),
        authorization_code=portal_data.get("authorization_code", "Código de autorização"),
        get_code=portal_data.get("get_code", "Obter código"),
    )
    return Settings(base_dir, stores, mail, rules, portal)
