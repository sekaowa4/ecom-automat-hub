from __future__ import annotations

import json
from urllib.parse import urlparse
from urllib.request import ProxyHandler, Request, build_opener, urlopen


class NotificationError(RuntimeError):
    """Raised when WeCom does not accept a success notification."""


class WeComNotifier:
    def __init__(self, webhook_url: str, opener=None, timeout: int = 15):
        self.webhook_url = webhook_url
        if opener is None and urlparse(webhook_url).hostname == "qyapi.weixin.qq.com":
            # The local overseas proxy rejects TLS to WeCom; use verified direct HTTPS.
            opener = build_opener(ProxyHandler({})).open
        self.opener = opener or urlopen
        self.timeout = timeout

    def send_success(self, report) -> None:
        codes = tuple(getattr(report, "authorization_codes", ()))
        if not codes:
            raise NotificationError("No authorization codes available for success notification")
        code_lines = "\n".join(f"{item.store_name}: {item.code}" for item in codes)
        content = (
            "店铺授权码邮件发送成功\n"
            f"日期：{report.business_date}\n"
            f"店铺数量：{len(report.store_statuses)}\n"
            f"邮件编号：{report.message_id}\n"
            "授权码：\n"
            f"{code_lines}"
        )
        body = json.dumps(
            {"msgtype": "text", "text": {"content": content}},
            ensure_ascii=False,
        ).encode("utf-8")
        request = Request(
            self.webhook_url,
            data=body,
            headers={"Content-Type": "application/json; charset=utf-8"},
            method="POST",
        )
        with self.opener(request, timeout=self.timeout) as response:
            result = json.loads(response.read().decode("utf-8"))
        if result.get("errcode") != 0:
            raise NotificationError(f"WeCom webhook failed: {result.get('errmsg', result)}")

    def send_verification_required(self, store_number: str) -> None:
        user_id = "paprika"
        content = (
            f"店铺{store_number}需要验证，无法获取授权码，请前往该店铺完成验证，"
            f"然后 @{user_id}。"
        )
        body = json.dumps(
            {
                "msgtype": "text",
                "text": {"content": content, "mentioned_list": [user_id]},
            },
            ensure_ascii=False,
        ).encode("utf-8")
        request = Request(
            self.webhook_url,
            data=body,
            headers={"Content-Type": "application/json; charset=utf-8"},
            method="POST",
        )
        with self.opener(request, timeout=self.timeout) as response:
            result = json.loads(response.read().decode("utf-8"))
        if result.get("errcode") != 0:
            raise NotificationError(f"WeCom webhook failed: {result.get('errmsg', result)}")
