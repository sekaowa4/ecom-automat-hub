import tempfile
import unittest
from datetime import datetime
from pathlib import Path

from shop_auth_codes.collector import CollectionError
from shop_auth_codes.audit import AuditLog
from shop_auth_codes.config import PortalLabels, Rules, Settings, StoreSettings
from shop_auth_codes.core import DuplicateBatchError, StoreCode
from shop_auth_codes.ledger import BatchLedger
from shop_auth_codes.mail import MailSettings, SendResult
from shop_auth_codes.runner import AutomationRunner


def make_settings(base: Path, template: Path) -> Settings:
    stores = (
        StoreSettings("ECO AVENTURA LTDA", "eco", base / "eco", "https://example.com"),
        StoreSettings("SANY INTERNACIONAL", "sany", base / "sany", "https://example.com"),
    )
    mail = MailSettings(
        "sender@example.com",
        ("receiver@example.com",),
        "Authorization codes",
        template,
        "imap.example.com",
        "smtp.example.com",
    )
    return Settings(base, stores, mail, Rules(), PortalLabels())


class FakeCollector:
    def __init__(self, codes):
        self.codes = codes

    def collect(self):
        return self.codes


class FakeMailClient:
    def __init__(self, status="sent_verified"):
        self.messages = []
        self.status = status

    def send_and_verify(self, message, message_id):
        self.messages.append((message, message_id))
        return SendResult(self.status, message_id)


class FakeNotifier:
    def __init__(self):
        self.reports = []

    def send_success(self, report):
        self.reports.append(report)


class RunnerTests(unittest.TestCase):
    def test_dry_run_composes_without_sending_or_recording(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            template = base / "template.txt"
            template.write_text("{{date}}\n{{store_lines}}\n", encoding="utf-8")
            mail = FakeMailClient()
            runner = AutomationRunner(
                make_settings(base, template),
                FakeCollector([
                    StoreCode("ECO AVENTURA LTDA", "A1B2C3D4"),
                    StoreCode("SANY INTERNACIONAL", "E5F6G7H8"),
                ]),
                mail,
                BatchLedger(base / "runs.json"),
                now=lambda: datetime(2026, 9, 14, 13, 1),
                audit_log=AuditLog(base / "run.log"),
            )

            report = runner.execute(send=False)

            self.assertEqual(report.send_status, "dry_run")
            self.assertEqual(mail.messages, [])
            self.assertFalse((base / "runs.json").exists())
            self.assertIn('"status": "dry_run"', (base / "run.log").read_text(encoding="utf-8"))

    def test_run_sends_complete_batch_and_blocks_duplicate(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            template = base / "template.txt"
            template.write_text("{{date}}\n{{store_lines}}\n", encoding="utf-8")
            mail = FakeMailClient()
            settings = make_settings(base, template)
            runner = AutomationRunner(
                settings,
                FakeCollector([
                    StoreCode("ECO AVENTURA LTDA", "A1B2C3D4"),
                    StoreCode("SANY INTERNACIONAL", "E5F6G7H8"),
                ]),
                mail,
                BatchLedger(base / "runs.json"),
                now=lambda: datetime(2026, 9, 14, 13, 1),
            )

            report = runner.execute(send=True)

            self.assertEqual(report.send_status, "sent_verified")
            self.assertEqual(len(mail.messages), 1)
            with self.assertRaises(DuplicateBatchError):
                runner.execute(send=True)

    def test_does_not_send_when_collector_returns_incomplete_batch(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            template = base / "template.txt"
            template.write_text("{{date}}\n{{store_lines}}\n", encoding="utf-8")
            mail = FakeMailClient()
            runner = AutomationRunner(
                make_settings(base, template),
                FakeCollector([StoreCode("ECO AVENTURA LTDA", "A1B2C3D4")]),
                mail,
                BatchLedger(base / "runs.json"),
                now=lambda: datetime(2026, 9, 14, 13, 1),
            )

            with self.assertRaises(ValueError):
                runner.execute(send=True)
            self.assertEqual(mail.messages, [])

    def test_notifies_only_after_verified_send(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            template = base / "template.txt"
            template.write_text("{{date}}\n{{store_lines}}\n", encoding="utf-8")
            notifier = FakeNotifier()
            runner = AutomationRunner(
                make_settings(base, template),
                FakeCollector([
                    StoreCode("ECO AVENTURA LTDA", "A1B2C3D4"),
                    StoreCode("SANY INTERNACIONAL", "E5F6G7H8"),
                ]),
                FakeMailClient("sent_verified"),
                BatchLedger(base / "runs.json"),
                now=lambda: datetime(2026, 9, 14, 13, 1),
                success_notifier=notifier,
            )

            runner.execute(send=True)

            self.assertEqual(len(notifier.reports), 1)
            self.assertEqual(
                [item.code for item in notifier.reports[0].authorization_codes],
                ["A1B2C3D4", "E5F6G7H8"],
            )

    def test_does_not_notify_when_send_is_unverified(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            template = base / "template.txt"
            template.write_text("{{date}}\n{{store_lines}}\n", encoding="utf-8")
            notifier = FakeNotifier()
            runner = AutomationRunner(
                make_settings(base, template),
                FakeCollector([
                    StoreCode("ECO AVENTURA LTDA", "A1B2C3D4"),
                    StoreCode("SANY INTERNACIONAL", "E5F6G7H8"),
                ]),
                FakeMailClient("sent_unverified"),
                BatchLedger(base / "runs.json"),
                now=lambda: datetime(2026, 9, 14, 13, 1),
                success_notifier=notifier,
            )

            runner.execute(send=True)

            self.assertEqual(notifier.reports, [])

    def test_notifies_when_smtp_accepted_but_imap_is_unavailable(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            template = base / "template.txt"
            template.write_text("{{date}}\n{{store_lines}}\n", encoding="utf-8")
            notifier = FakeNotifier()
            runner = AutomationRunner(
                make_settings(base, template),
                FakeCollector([
                    StoreCode("ECO AVENTURA LTDA", "A1B2C3D4"),
                    StoreCode("SANY INTERNACIONAL", "E5F6G7H8"),
                ]),
                FakeMailClient("sent_accepted_unverified"),
                BatchLedger(base / "runs.json"),
                now=lambda: datetime(2026, 9, 14, 13, 1),
                success_notifier=notifier,
            )

            runner.execute(send=True)

            self.assertEqual(len(notifier.reports), 1)


if __name__ == "__main__":
    unittest.main()
