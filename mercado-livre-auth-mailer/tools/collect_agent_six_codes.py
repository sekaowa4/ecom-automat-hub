from __future__ import annotations

import ctypes
import hashlib
import json
import re
import subprocess
import sys
import time
from pathlib import Path

from playwright.sync_api import sync_playwright


CLI = Path(r"D:\application\ziniao\resources\app.asar.unpacked\envkit\cli\win32\x64\ziniao.exe")
PREFERENCES_URL = "https://vendedores.mercadolivre.com.br/preferencias-de-venda"
EXPECTED = {
    "ECO AVENTURA LTDA": "Eco Aventura 店铺1-子账号",
    "SANY INTERNACIONAL": "Sany店铺2-子账号",
    "SMARTECH VARIEDADES LTDA": "Smartech 店铺3-子账号",
    "APOLOTECH VARIEDADES LTDA": "Apolotech 店铺6-子账号",
    "Planetechnology": "Planetechnology 店铺7-子账号",
    "Mastertrade": "Mastertrade 店铺8-子账号",
}


def cli(*args: str) -> dict:
    result = subprocess.run(
        [str(CLI), *args, "--json"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=180,
    )
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or result.stdout.strip())
    for line in reversed(result.stdout.splitlines()):
        try:
            payload = json.loads(line)
        except json.JSONDecodeError:
            continue
        if "ok" in payload:
            if not payload["ok"]:
                raise RuntimeError(payload.get("error", "ZiNiao command failed"))
            return payload.get("data", {})
    raise RuntimeError("ZiNiao did not return a final result")


def list_stores() -> dict[str, dict]:
    items = cli("browser", "list", "--all").get("items", [])
    by_name = {item.get("storeName"): item for item in items}
    selected = {}
    for mail_name, store_name in EXPECTED.items():
        item = by_name.get(store_name)
        if item is None:
            raise RuntimeError(f"Missing ZiNiao store: {store_name}")
        selected[mail_name] = item
    return selected


def find_debug_port(store_id: str, timeout: int = 45) -> int:
    query = (
        "Get-CimInstance -ClassName Win32_Process -Filter \"Name='ziniaobrowser.exe'\" | "
        f"Where-Object {{ $_.CommandLine -match 'chrome_{store_id}' }} | "
        "Select-Object -ExpandProperty CommandLine"
    )
    deadline = time.time() + timeout
    while time.time() < deadline:
        result = subprocess.run(
            ["powershell.exe", "-NoProfile", "-Command", query],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
        )
        match = re.search(r"--remote-debugging-port=(\d+)", result.stdout)
        if match:
            return int(match.group(1))
        time.sleep(1)
    raise RuntimeError(f"No agent debugging port for store {store_id}")


def collect_code(port: int) -> tuple[str, str | None]:
    with sync_playwright() as playwright:
        browser = playwright.chromium.connect_over_cdp(f"http://127.0.0.1:{port}")
        page = browser.contexts[0].pages[0]
        page.goto(PREFERENCES_URL, wait_until="domcontentloaded", timeout=60000)
        if "vendedores.mercadolivre.com.br" not in page.url:
            raise RuntimeError(f"Store session is not logged in: {page.url}")
        code_locator = page.locator(".sc-settings-copy-textfield__text:visible")
        if code_locator.count() == 0:
            button = page.get_by_role("button", name=re.compile(r"^(获取代码|Obter código)$"))
            button.first.wait_for(state="visible", timeout=60000)
            if button.count() != 1:
                raise RuntimeError(f"Expected one get-code button; found {button.count()}")
            button.click()
        code_locator.wait_for(state="visible", timeout=30000)
        code = code_locator.first.inner_text().strip()
        if re.fullmatch(r"[A-Z0-9]{8}", code) is None:
            raise RuntimeError("Authorization code format is invalid")
        helper = page.locator(".sc-settings-copy-textfield__helper-text:visible").first
        helper_text = helper.inner_text().strip() if helper.count() else ""
        date_match = re.search(r"(\d{1,4}[/-]\d{1,2}[/-]\d{1,4})", helper_text)
        browser.close()
        return code, date_match.group(1) if date_match else None


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    stores = list_stores()
    collected = []
    for mail_name, item in stores.items():
        store_id = str(item["storeId"])
        started = False
        try:
            cli("browser", "start", "--id", store_id, "--mode", "agent", "--headless")
            started = True
            port = find_debug_port(store_id)
            code, valid_date = collect_code(port)
            collected.append({"store": mail_name, "code": code, "valid_date": valid_date})
            print(json.dumps({
                "store": mail_name,
                "mask": f"{code[:2]}****{code[-2:]}",
                "sha256_prefix": hashlib.sha256(code.encode()).hexdigest()[:12],
                "valid_date": valid_date,
            }, ensure_ascii=False))
        finally:
            if started:
                try:
                    cli("browser", "close", "--id", store_id)
                except Exception:
                    pass
    if len({item["code"] for item in collected}) != len(collected):
        raise RuntimeError("Authorization codes are not unique")
    dates = {item["valid_date"] for item in collected}
    if len(dates) != 1:
        raise RuntimeError("Authorization code dates do not match")
    display = "\n".join([next(iter(dates)) or ""] + [f"{x['store']}: {x['code']}" for x in collected])
    ctypes.windll.user32.MessageBoxW(0, display, "Six authorization codes", 0x40)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
