from __future__ import annotations

import json
import time
from collections import OrderedDict
from urllib.request import Request, urlopen


EXPECTED_STORES = OrderedDict(
    [
        ("ECO AVENTURA LTDA", "ecoaventura店铺1-子账号"),
        ("SANY INTERNACIONAL", "sany店铺2-子账号"),
        ("SMARTECH VARIEDADES LTDA", "smartech店铺3-子账号"),
        ("APOLOTECH VARIEDADES LTDA", "apolotech店铺6-子账号"),
        ("Planetechnology", "planetechnology店铺7-子账号"),
        ("Mastertrade", "mastertrade店铺8-子账号"),
    ]
)


def is_get_code_text(value: str) -> bool:
    return value.strip() in {"Obter código", "获取代码"}


def _normalized(value: str) -> str:
    return "".join(value.split()).casefold()


def select_subaccount_stores(browser_list: list[dict]) -> OrderedDict[str, dict]:
    selected: OrderedDict[str, dict] = OrderedDict()
    for mail_name, expected_browser_name in EXPECTED_STORES.items():
        matches = [
            item
            for item in browser_list
            if _normalized(str(item.get("browserName", ""))) == _normalized(expected_browser_name)
        ]
        if len(matches) != 1:
            raise ValueError(
                f"Expected one subaccount browser for {mail_name}; found {len(matches)}"
            )
        selected[mail_name] = matches[0]
    return selected


def wait_for_core(client, attempts: int = 24, delay: int = 5) -> None:
    for _ in range(attempts):
        result = client.call_raw("updateCore")
        if result.get("statusCode") == 0:
            return
        if result.get("statusCode") != -10000:
            raise RuntimeError(
                f"Ziniao updateCore failed: {result.get('statusCode')} {result.get('err', '')}"
            )
        time.sleep(delay)
    raise RuntimeError("Ziniao updateCore did not finish in time")


class ZiniaoClient:
    def __init__(self, company: str, username: str, password: str, port: int = 9515):
        self.company = company
        self.username = username
        self.password = password
        self.url = f"http://127.0.0.1:{port}"

    def call_raw(self, action: str, **fields) -> dict:
        payload = {
            "company": self.company,
            "username": self.username,
            "password": self.password,
            "action": action,
            "requestId": __import__("uuid").uuid4().hex,
            **fields,
        }
        request = Request(
            self.url,
            data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
            headers={"Content-Type": "application/json; charset=utf-8"},
            method="POST",
        )
        with urlopen(request, timeout=180) as response:
            return json.loads(response.read().decode("utf-8"))

    def call(self, action: str, **fields) -> dict:
        result = self.call_raw(action, **fields)
        if result.get("statusCode") != 0:
            raise RuntimeError(f"Ziniao {action} failed: {result.get('statusCode')} {result.get('err', '')}")
        return result
