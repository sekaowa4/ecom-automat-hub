import json
import unittest
from unittest.mock import patch

from shop_auth_codes.core import StoreCode
from shop_auth_codes.notify import WeComNotifier


class FakeResponse:
    def __enter__(self):
        return self

    def __exit__(self, *args):
        return None

    def read(self):
        return b'{"errcode":0,"errmsg":"ok"}'


class NotifyTests(unittest.TestCase):
    def test_official_wecom_endpoint_uses_direct_https_without_local_proxy(self):
        with patch("shop_auth_codes.notify.build_opener") as build:
            notifier = WeComNotifier("https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=test-only")
        self.assertEqual({}, build.call_args.args[0].proxies)
        self.assertIs(notifier.opener, build.return_value.open)

    def test_sends_success_message_with_authorization_codes(self):
        captured = {}

        def opener(request, timeout):
            captured["url"] = request.full_url
            captured["timeout"] = timeout
            captured["body"] = json.loads(request.data.decode("utf-8"))
            return FakeResponse()

        notifier = WeComNotifier("https://example.com/webhook", opener=opener)
        report = type("Report", (), {
            "business_date": "2026/9/21",
            "store_statuses": {"Store A": "validated", "Store B": "validated"},
            "message_id": "<batch@example>",
            "authorization_codes": (
                StoreCode("Store A", "A1B2C3D4"),
                StoreCode("Store B", "E5F6G7H8"),
            ),
        })()

        notifier.send_success(report)

        self.assertEqual(captured["url"], "https://example.com/webhook")
        self.assertEqual(captured["body"]["msgtype"], "text")
        content = captured["body"]["text"]["content"]
        self.assertIn("2026/9/21", content)
        self.assertIn("2", content)
        self.assertIn("Store A: A1B2C3D4", content)
        self.assertIn("Store B: E5F6G7H8", content)


if __name__ == "__main__":
    unittest.main()
