from __future__ import annotations

import json
from urllib.request import Request, urlopen


class NotificationError(RuntimeError):
    """Raised when WeCom does not accept a success notification."""


class WeComNotifier:
    def __init__(self, webhook_url: str, opener=urlopen, timeout: int = 15):
        self.webhook_url = webhook_url
        self.opener = opener
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
