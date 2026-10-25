import unittest
import sys
from pathlib import Path
from types import ModuleType, SimpleNamespace
from unittest.mock import patch

from shop_auth_codes.collector import (
    CollectionError,
    StoreNeedsVerification,
    ZiniaoAgentCollector,
    extract_code_from_text,
)
from shop_auth_codes.config import StoreSettings
from shop_auth_codes.core import StoreCode


class CollectorTests(unittest.TestCase):
    def test_extracts_one_authorization_code_from_page_text(self):
        text = "Código de autorização\nA1B2C3D4\nObter código"
        self.assertEqual(
            extract_code_from_text(text, r"^[A-Z0-9]{8}$"),
            "A1B2C3D4",
        )

    def test_rejects_ambiguous_page_text(self):
        with self.assertRaises(ValueError):
            extract_code_from_text(
                "Código\nA1B2C3D4\nE5F6G7H8",
                r"^[A-Z0-9]{8}$",
            )

    def test_marks_mercado_libre_login_challenge_as_verification(self):
        challenge_url = "https://www.mercadolivre.com.br/login/challenges?platform_id=ml"

        class Page:
            url = ""

            def goto(self, *args, **kwargs):
                self.url = challenge_url

        page = Page()
        browser = SimpleNamespace(
            contexts=[SimpleNamespace(pages=[page])],
            close=lambda: None,
        )
        chromium = SimpleNamespace(connect_over_cdp=lambda _: browser)

        class Playwright:
            def __enter__(self):
                self.chromium = chromium
                return self

            def __exit__(self, *args):
                return False

        playwright_module = ModuleType("playwright")
        sync_api_module = ModuleType("playwright.sync_api")
        sync_api_module.sync_playwright = Playwright
        playwright_module.sync_api = sync_api_module

        with patch.dict(sys.modules, {
            "playwright": playwright_module,
            "playwright.sync_api": sync_api_module,
        }):
            collector = ZiniaoAgentCollector((), r"^[A-Z0-9]{8}$")
            with self.assertRaisesRegex(CollectionError, "requires verification"):
                collector._collect_from_page(9515)

    def test_selects_each_child_account_before_its_same_store_main_account(self):
        expected = {
            "ECO AVENTURA LTDA": ["Eco Aventura 店铺1-子账号", "Eco Aventura 店铺1"],
            "SANY INTERNACIONAL": ["Sany店铺2-子账号", "Sany 店铺2"],
            "SMARTECH VARIEDADES LTDA": ["Smartech 店铺3-子账号", "Smartech 店铺3"],
            "APOLOTECH VARIEDADES LTDA": ["Apolotech 店铺6-子账号", "Apolotech 店铺6"],
            "Planetechnology": ["Planetechnology 店铺7-子账号", "Planetechnology 店铺7"],
            "Mastertrade": ["Mastertrade 店铺8-子账号", "Mastertrade 店铺8"],
        }
        browser_list = [
            {
                "storeName": name,
                "storeId": f"store-{index}",
                "ip": "127.0.0.1",
                "platformName": "Mercado Livre",
            }
            for index, name in enumerate(
                [name for pair in expected.values() for name in pair], start=1
            )
        ]
        stores = tuple(
            StoreSettings(name, "", Path("."), "https://www.mercadolivre.com.br/")
            for name in expected
        )
        collector = ZiniaoAgentCollector(stores, r"^[A-Z0-9]{8}$")

        with (
            patch("shop_auth_codes.collector.ZINIAO_CLI", Path(__file__)),
            patch.object(collector, "_cli", return_value={"items": browser_list}),
        ):
            selected = collector._selected_stores()

        actual = {
            store_name: [
                item["storeName"]
                for item in (items if isinstance(items, (list, tuple)) else [items])
            ]
            for store_name, items in selected.items()
        }
        self.assertEqual(expected, actual)

    def test_retries_main_only_after_child_login_verification(self):
        store = StoreSettings(
            "SMARTECH VARIEDADES LTDA",
            "",
            Path("."),
            "https://www.mercadolivre.com.br/",
        )
        accounts = [
            {
                "storeName": "Smartech 店铺3-子账号",
                "storeId": "child-id",
                "ip": "127.0.0.1",
                "platformName": "Mercado Livre",
            },
            {
                "storeName": "Smartech 店铺3",
                "storeId": "main-id",
                "ip": "127.0.0.1",
                "platformName": "Mercado Livre",
            },
        ]

        def collect_with(outcomes):
            collector = ZiniaoAgentCollector((store,), r"^[A-Z0-9]{8}$")
            events = []

            def fake_cli(*args):
                if args[1] in {"start", "close"}:
                    events.append((args[1], args[3]))
                return {}

            with (
                patch.object(collector, "_selected_stores", return_value={store.name: accounts}),
                patch.object(collector, "_cli", side_effect=fake_cli),
                patch.object(collector, "_find_debug_port", side_effect=lambda store_id: 9515),
                patch.object(collector, "_collect_from_page", side_effect=outcomes),
            ):
                try:
                    return collector.collect(), events
                except CollectionError as exc:
                    return exc, events

        result, events = collect_with([
            StoreNeedsVerification("login challenge"),
            "A1B2C3D4",
        ])
        self.assertEqual([StoreCode(store.name, "A1B2C3D4")], result)
        self.assertEqual(
            [("start", "child-id"), ("close", "child-id"), ("start", "main-id"), ("close", "main-id")],
            events,
        )

        result, events = collect_with([CollectionError("page structure changed")])
        self.assertIsInstance(result, CollectionError)
        self.assertEqual([("start", "child-id"), ("close", "child-id")], events)

if __name__ == "__main__":
    unittest.main()
