from __future__ import annotations

import asyncio
import json
import re
import subprocess
import time
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlparse

from .config import PortalLabels, StoreSettings
from .core import StoreCode


ZINIAO_CLI = Path(
    r"D:\application\ziniao\resources\app.asar.unpacked\envkit\cli\win32\x64\ziniao.exe"
)
PREFERENCES_URL = "https://vendedores.mercadolivre.com.br/preferencias-de-venda"
ZINIAO_STORE_NAMES = {
    "ECO AVENTURA LTDA": ("Eco Aventura 店铺1-子账号", "Eco Aventura 店铺1"),
    "SANY INTERNACIONAL": ("Sany店铺2-子账号", "Sany 店铺2"),
    "SMARTECH VARIEDADES LTDA": ("Smartech 店铺3-子账号", "Smartech 店铺3"),
    "APOLOTECH VARIEDADES LTDA": ("Apolotech 店铺6-子账号", "Apolotech 店铺6"),
    "Planetechnology": ("Planetechnology 店铺7-子账号", "Planetechnology 店铺7"),
    "Mastertrade": ("Mastertrade 店铺8-子账号", "Mastertrade 店铺8"),
}


class CollectionError(RuntimeError):
    """Raised when an account or page cannot be verified safely."""


class StoreNeedsVerification(CollectionError):
    """Raised when Mercado Livre redirects a store session to login verification."""


class StoreAccessDenied(CollectionError):
    """Raised when an account is denied access to the authorization page."""


def extract_code_from_text(text: str, code_regex: str) -> str:
    pattern = re.compile(code_regex)
    tokens = re.findall(r"[^\s,;:()<>\"']+", text)
    matches = [token.strip("[]{}.") for token in tokens if pattern.fullmatch(token.strip("[]{}."))]
    matches = list(dict.fromkeys(matches))
    if len(matches) != 1:
        raise ValueError(f"Expected exactly one authorization code, found {len(matches)}")
    return matches[0]


@dataclass
class BrowserCollector:
    stores: tuple[StoreSettings, ...]
    portal: PortalLabels
    code_regex: str
    timeout_ms: int = 30_000

    def collect(self) -> list[StoreCode]:
        return asyncio.run(self._collect())

    def setup_sessions(self) -> None:
        asyncio.run(self._setup_sessions())

    async def _setup_sessions(self) -> None:
        try:
            from playwright.async_api import async_playwright
        except ImportError as exc:
            raise CollectionError(
                "Playwright is not installed. Install requirements.txt first."
            ) from exc

        async with async_playwright() as playwright:
            for store in self.stores:
                store.profile_dir.mkdir(parents=True, exist_ok=True)
                context = await playwright.chromium.launch_persistent_context(
                    str(store.profile_dir),
                    headless=False,
                )
                try:
                    page = context.pages[0] if context.pages else await context.new_page()
                    await page.goto(store.start_url, wait_until="domcontentloaded", timeout=self.timeout_ms)
                    print(f"请在浏览器中登录 {store.name}，完成验证码后按 Enter 继续。")
                    await asyncio.to_thread(input)
                    body = await page.locator("body").inner_text()
                    if store.account not in body:
                        raise CollectionError(f"未检测到预期账号标识: {store.name}")
                finally:
                    await context.close()

    async def _collect(self) -> list[StoreCode]:
        try:
            from playwright.async_api import async_playwright
        except ImportError as exc:
            raise CollectionError(
                "Playwright is not installed. Install requirements.txt first."
            ) from exc

        results: list[StoreCode] = []
        async with async_playwright() as playwright:
            for store in self.stores:
                context = None
                try:
                    store.profile_dir.mkdir(parents=True, exist_ok=True)
                    context = await playwright.chromium.launch_persistent_context(
                        str(store.profile_dir),
                        headless=False,
                    )
                    page = context.pages[0] if context.pages else await context.new_page()
                    await page.goto(store.start_url, wait_until="domcontentloaded", timeout=self.timeout_ms)
                    body = await page.locator("body").inner_text()
                    if store.account not in body:
                        print(f"{store.name} 登录已失效，请在当前浏览器完成登录后按 Enter。")
                        await asyncio.to_thread(input)
                        body = await page.locator("body").inner_text()
                        if store.account not in body:
                            raise CollectionError(f"Account identity mismatch for {store.name}")

                    for label in (
                        self.portal.settings,
                        self.portal.sales_preferences,
                        self.portal.authorization_code,
                    ):
                        await page.get_by_text(label, exact=True).first.click(timeout=self.timeout_ms)
                        await page.wait_for_timeout(300)
                    await page.get_by_text(self.portal.get_code, exact=True).first.click(
                        timeout=self.timeout_ms
                    )
                    await page.wait_for_timeout(500)
                    source = (
                        await page.locator(store.code_selector).inner_text()
                        if store.code_selector
                        else await page.locator("body").inner_text()
                    )
                    results.append(StoreCode(store.name, extract_code_from_text(source, self.code_regex)))
                except CollectionError:
                    raise
                except Exception as exc:
                    raise CollectionError(f"Failed to collect {store.name}: {exc}") from exc
                finally:
                    if context is not None:
                        await context.close()
        return results


@dataclass
class ZiniaoAgentCollector:
    """Collect codes through ZiNiao's logged-in official agent channel."""

    stores: tuple[StoreSettings, ...]
    code_regex: str
    timeout_ms: int = 60_000

    def setup_sessions(self) -> None:
        self._selected_stores()
        print("紫鸟账号由紫鸟客户端维护；setup 不会再次要求店铺密码。")

    def collect(self) -> list[StoreCode]:
        selected = self._selected_stores()
        results: list[StoreCode] = []
        for store in self.stores:
            unavailable_accounts = []
            for account in selected[store.name]:
                store_id = str(account["storeId"])
                started = False
                try:
                    self._cli("browser", "start", "--id", store_id, "--mode", "agent", "--headless")
                    started = True
                    port = self._find_debug_port(store_id)
                    code = self._collect_from_page(port)
                    results.append(StoreCode(store.name, code))
                    print(f"{store.name}: {code[:2]}****{code[-2:]}", flush=True)
                    break
                except (StoreNeedsVerification, StoreAccessDenied) as exc:
                    unavailable_accounts.append(f"{account['storeName']}: {exc}")
                except Exception as exc:
                    raise CollectionError(f"Failed to collect {store.name}: {exc}") from exc
                finally:
                    if started:
                        try:
                            self._cli("browser", "close", "--id", store_id)
                        except Exception:
                            pass
            else:
                raise CollectionError(
                    f"All ZiNiao accounts unavailable for {store.name}: "
                    f"{'; '.join(unavailable_accounts)}"
                )
        return results

    def _selected_stores(self) -> dict[str, list[dict]]:
        if not ZINIAO_CLI.exists():
            raise CollectionError(f"ZiNiao CLI not found: {ZINIAO_CLI}")
        items = self._cli("browser", "list", "--all").get("items", [])
        by_name = {item.get("storeName"): item for item in items}
        selected: dict[str, list[dict]] = {}
        for store in self.stores:
            expected = ZINIAO_STORE_NAMES.get(store.name)
            if expected is None:
                raise CollectionError(f"No ZiNiao mapping configured for {store.name}")
            accounts = [by_name[name] for name in expected if name in by_name]
            if not accounts:
                raise CollectionError(f"Missing ZiNiao stores: {' or '.join(expected)}")
            selected[store.name] = accounts
        return selected

    @staticmethod
    def _cli(*args: str) -> dict:
        result = subprocess.run(
            [str(ZINIAO_CLI), *args, "--json"],
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

    def _find_debug_port(self, store_id: str) -> int:
        query = (
            "Get-CimInstance -ClassName Win32_Process -Filter \"Name='ziniaobrowser.exe'\" | "
            f"Where-Object {{ $_.CommandLine -match 'chrome_{store_id}' }} | "
            "Select-Object -ExpandProperty CommandLine"
        )
        deadline = time.time() + self.timeout_ms / 1000
        while time.time() < deadline:
            result = subprocess.run(
                ["powershell.exe", "-NoProfile", "-Command", query],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=self.timeout_ms / 1000,
            )
            match = re.search(r"--remote-debugging-port=(\d+)", result.stdout)
            if match:
                return int(match.group(1))
            time.sleep(1)
        raise RuntimeError(f"No agent debugging port for store {store_id}")

    def _collect_from_page(self, port: int) -> str:
        try:
            from playwright.sync_api import sync_playwright
        except ImportError as exc:
            raise CollectionError("Playwright is not installed") from exc

        with sync_playwright() as playwright:
            browser = playwright.chromium.connect_over_cdp(f"http://127.0.0.1:{port}")
            try:
                page = browser.contexts[0].pages[0]
                page.goto(PREFERENCES_URL, wait_until="domcontentloaded", timeout=self.timeout_ms)
                self._check_seller_url(page.url)
                return self._read_authorization_code(page)
            finally:
                browser.close()

    @staticmethod
    def _check_seller_url(url: str) -> None:
        parsed_url = urlparse(url)
        host = (parsed_url.hostname or "").lower()
        if host == "vendedores.mercadolivre.com.br" or host.endswith(".vendedores.mercadolivre.com.br"):
            return
        marketplace_host = any(
            host == domain or host.endswith(f".{domain}")
            for domain in ("mercadolivre.com.br", "mercadolivre.com")
        )
        if marketplace_host and any(path in parsed_url.path.lower() for path in ("/login", "/challenges")):
            raise StoreNeedsVerification("Mercado Livre account requires verification")
        raise CollectionError(f"Store session is not on seller settings: {host}{parsed_url.path}")

    def _read_authorization_code(self, page) -> str:
        code_locator = page.locator(
            ".sc-settings-copy-textfield__text:visible, .handshake-authorization-code-token:visible"
        )
        if code_locator.count() == 0:
            old_button = page.locator("button#link_shipping_token:visible").or_(
                page.get_by_role("button", name=re.compile(r"^(获取代码|Obter código)$"))
            )
            new_entry = page.get_by_role(
                "button",
                name=re.compile(r"用于授权货件及关联账户的代码|c[oó]digo.*(?:autoriza|conta|envio)", re.I),
            )
            action = old_button.or_(new_entry)
            action.first.wait_for(state="visible", timeout=self.timeout_ms)
            action_count = action.count()
            if action_count != 1:
                raise CollectionError(f"Expected one authorization entry; found {action_count}")
            tutorial = page.locator("button.andes-coach-marks__step__close-button:visible")
            if tutorial.count():
                tutorial.click(timeout=self.timeout_ms)
            if new_entry.is_visible():
                with page.expect_navigation(wait_until="domcontentloaded", timeout=self.timeout_ms) as navigation:
                    new_entry.click(timeout=self.timeout_ms)
                response = navigation.value
                if response is not None and response.status == 403:
                    raise StoreAccessDenied("Authorization page denied access (HTTP 403)")
                if response is not None and response.status >= 400:
                    raise CollectionError(f"Authorization page failed (HTTP {response.status})")
                self._check_seller_url(page.url)
            else:
                old_button.click(timeout=self.timeout_ms)
        code_locator.first.wait_for(state="visible", timeout=self.timeout_ms)
        code_count = code_locator.count()
        if code_count != 1:
            raise CollectionError(f"Expected one authorization code; found {code_count}")
        code = code_locator.first.inner_text().strip()
        if re.fullmatch(self.code_regex, code) is None:
            raise CollectionError("Authorization code format is invalid")
        return code
