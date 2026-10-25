from __future__ import annotations

import imaplib
import smtplib
from dataclasses import dataclass
from email.message import EmailMessage
from pathlib import Path

from .core import StoreCode, render_message


@dataclass(frozen=True)
class MailSettings:
    sender: str
    recipients: tuple[str, ...] | list[str]
    subject: str
    template: Path | str
    imap_host: str
    smtp_host: str
    imap_port: int = 993
    smtp_port: int = 465
    imap_folder: str = "Sent"


@dataclass(frozen=True)
class SendResult:
    status: str
    message_id: str
    detail: str = ""


class MailError(RuntimeError):
    """Raised when the mail operation fails before its final state is known."""


def compose_message(
    settings: MailSettings,
    date_text: str,
    codes: list[StoreCode],
    template: str,
    message_id: str,
) -> EmailMessage:
    message = EmailMessage()
    message["From"] = settings.sender
    message["To"] = ", ".join(settings.recipients)
    message["Subject"] = settings.subject
    message["Message-ID"] = message_id
    message.set_content(render_message(template, date_text, codes))
    return message


class MailClient:
    def __init__(self, settings: MailSettings, secret: str, timeout: int = 30):
        self.settings = settings
        self.secret = secret
        self.timeout = timeout

    def test_connection(self) -> None:
        smtp = smtplib.SMTP_SSL(self.settings.smtp_host, self.settings.smtp_port, timeout=self.timeout)
        try:
            smtp.login(self.settings.sender, self.secret)
        finally:
            smtp.quit()

        imap = imaplib.IMAP4_SSL(self.settings.imap_host, self.settings.imap_port)
        try:
            imap.login(self.settings.sender, self.secret)
        finally:
            imap.logout()

    def send_and_verify(self, message: EmailMessage, message_id: str) -> SendResult:
        try:
            smtp = smtplib.SMTP_SSL(
                self.settings.smtp_host,
                self.settings.smtp_port,
                timeout=self.timeout,
            )
            try:
                smtp.login(self.settings.sender, self.secret)
                smtp.send_message(message)
            finally:
                smtp.quit()
        except Exception as exc:
            try:
                if self._is_in_sent(message_id):
                    return SendResult("sent_verified_after_smtp_error", message_id)
            except Exception as verification_error:
                return SendResult(
                    "sent_unverified",
                    message_id,
                    f"SMTP error: {exc}; IMAP verification error: {verification_error}",
                )
            raise MailError(f"SMTP send failed: {exc}") from exc

        try:
            verified = self._is_in_sent(message_id)
        except Exception as exc:
            return SendResult(
                "sent_accepted_unverified",
                message_id,
                f"IMAP verification failed: {exc}",
            )
        return SendResult("sent_verified" if verified else "sent_unverified", message_id)

    def _is_in_sent(self, message_id: str) -> bool:
        imap = imaplib.IMAP4_SSL(self.settings.imap_host, self.settings.imap_port)
        try:
            imap.login(self.settings.sender, self.secret)
            status, _ = imap.select(self.settings.imap_folder, readonly=True)
            if status != "OK":
                raise MailError(f"Cannot select IMAP folder {self.settings.imap_folder}")
            status, data = imap.search(None, "HEADER", "Message-ID", message_id)
            return status == "OK" and bool(data and data[0])
        finally:
            imap.logout()
