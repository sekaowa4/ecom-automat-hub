from __future__ import annotations

from dataclasses import asdict, dataclass
from datetime import datetime
from pathlib import Path
from typing import Callable
from zoneinfo import ZoneInfo

from .config import Settings
from .core import (
    build_batch_fingerprint,
    format_business_date,
    validate_batch,
    validate_cutoff,
)
from .core import StoreCode
from .ledger import BatchLedger
from .mail import MailClient, SendResult, compose_message


@dataclass(frozen=True)
class RunReport:
    business_date: str
    fingerprint: str
    store_statuses: dict[str, str]
    send_status: str
    message_id: str
    preview: str = ""
    detail: str = ""
    authorization_codes: tuple[StoreCode, ...] = ()

    def as_dict(self) -> dict:
        payload = asdict(self)
        payload.pop("authorization_codes", None)
        return payload


class AutomationRunner:
    def __init__(
        self,
        settings: Settings,
        collector,
        mail_client: MailClient | None,
        ledger: BatchLedger,
        now: Callable[[], datetime] | None = None,
        audit_log=None,
        success_notifier=None,
    ):
        self.settings = settings
        self.collector = collector
        self.mail_client = mail_client
        self.ledger = ledger
        self.now = now or (lambda: datetime.now(ZoneInfo(settings.rules.timezone)))
        self.audit_log = audit_log
        self.success_notifier = success_notifier

    def execute(self, send: bool) -> RunReport:
        now = self.now()
        business_date = format_business_date(now, self.settings.rules.date_format)
        try:
            validate_cutoff(now, self.settings.rules.cutoff)
            template = Path(self.settings.mail.template).read_text(encoding="utf-8")
            collected = self.collector.collect()
            now = self.now()
            validate_cutoff(now, self.settings.rules.cutoff)
            business_date = format_business_date(now, self.settings.rules.date_format)
            codes = validate_batch(
                [store.name for store in self.settings.stores],
                collected,
                self.settings.rules.code_regex,
            )
            fingerprint = build_batch_fingerprint(business_date, codes)
            self.ledger.ensure_not_sent(business_date, fingerprint)

            message_id = f"<auth-code-{business_date.replace('/', '-')}-{fingerprint[:16]}@local>"
            message = compose_message(
                self.settings.mail,
                business_date,
                codes,
                template,
                message_id,
            )
            preview = message.get_content()
            if not send:
                report = RunReport(
                    business_date,
                    fingerprint,
                    {item.store_name: "validated" for item in codes},
                    "dry_run",
                    message_id,
                    preview,
                    authorization_codes=tuple(codes),
                )
                self._audit(report)
                return report
            if self.mail_client is None:
                raise RuntimeError("A mail client is required for a sending run")

            result: SendResult = self.mail_client.send_and_verify(message, message_id)
            self.ledger.mark_sent(business_date, fingerprint, result.message_id, result.status)
            report = RunReport(
                business_date,
                fingerprint,
                {item.store_name: "validated" for item in codes},
                result.status,
                result.message_id,
                preview,
                result.detail,
                tuple(codes),
            )
            self._audit(report)
            if self.success_notifier is not None and result.status in {
                "sent_verified",
                "sent_verified_after_smtp_error",
                "sent_accepted_unverified",
            }:
                self.success_notifier.send_success(report)
            return report
        except Exception as exc:
            if self.audit_log is not None:
                self.audit_log.write({
                    "business_date": business_date,
                    "status": "failed",
                    "detail": str(exc),
                })
            raise

    def _audit(self, report: RunReport) -> None:
        if self.audit_log is not None:
            self.audit_log.write({
                "business_date": report.business_date,
                "fingerprint": report.fingerprint,
                "store_statuses": report.store_statuses,
                "status": report.send_status,
                "message_id": report.message_id,
                "detail": report.detail,
            })
