import unittest
from unittest.mock import patch

from shop_auth_codes.core import StoreCode
from shop_auth_codes.mail import MailClient, MailSettings, compose_message


class MailTests(unittest.TestCase):
    def test_composes_plain_text_message_with_expected_headers(self):
        settings = MailSettings(
            sender="sender@example.com",
            recipients=["receiver@example.com"],
            subject="Authorization codes",
            template="template.txt",
            imap_host="imap.example.com",
            smtp_host="smtp.example.com",
        )
        message = compose_message(
            settings,
            "2026/9/14",
            [StoreCode("ECO AVENTURA LTDA", "A1B2C3D4")],
            "{{date}}\n{{store_lines}}\n",
            "<stable-message@example.com>",
        )

        self.assertEqual(message["From"], "sender@example.com")
        self.assertEqual(message["To"], "receiver@example.com")
        self.assertEqual(message["Subject"], "Authorization codes")
        self.assertEqual(message["Message-ID"], "<stable-message@example.com>")
        self.assertEqual(
            message.get_content(),
            "2026/9/14\nECO AVENTURA LTDA: A1B2C3D4\n",
        )

    def test_reports_smtp_acceptance_when_imap_verification_is_unavailable(self):
        settings = MailSettings(
            sender="sender@example.com",
            recipients=["receiver@example.com"],
            subject="Authorization codes",
            template="template.txt",
            imap_host="imap.example.com",
            smtp_host="smtp.example.com",
        )
        message = compose_message(
            settings,
            "2026/9/14",
            [StoreCode("ECO AVENTURA LTDA", "A1B2C3D4")],
            "{{date}}\n{{store_lines}}\n",
            "<stable-message@example.com>",
        )

        class FakeSMTP:
            def __init__(self, *args, **kwargs):
                pass

            def login(self, *args):
                pass

            def send_message(self, message):
                return {}

            def quit(self):
                pass

        class FakeIMAP:
            def __init__(self, *args, **kwargs):
                pass

            def login(self, *args):
                pass

            def select(self, *args, **kwargs):
                return "NO", []

            def logout(self):
                pass

        with patch("shop_auth_codes.mail.smtplib.SMTP_SSL", FakeSMTP), patch(
            "shop_auth_codes.mail.imaplib.IMAP4_SSL", FakeIMAP
        ):
            result = MailClient(settings, "secret").send_and_verify(
                message,
                "<stable-message@example.com>",
            )

        self.assertEqual(result.status, "sent_accepted_unverified")


if __name__ == "__main__":
    unittest.main()
