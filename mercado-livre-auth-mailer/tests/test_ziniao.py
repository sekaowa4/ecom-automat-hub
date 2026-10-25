import unittest

from shop_auth_codes.ziniao import (
    EXPECTED_STORES,
    is_get_code_text,
    select_subaccount_stores,
    wait_for_core,
)
from shop_auth_codes.collector import ZINIAO_STORE_NAMES


class ZiniaoStoreSelectionTests(unittest.TestCase):
    def test_waits_until_core_update_finishes(self):
        class FakeClient:
            def __init__(self):
                self.statuses = [-10000, 0]

            def call_raw(self, action):
                self.assert_action = action
                return {"statusCode": self.statuses.pop(0), "err": ""}

        client = FakeClient()
        wait_for_core(client, attempts=2, delay=0)
        self.assertEqual(client.assert_action, "updateCore")

    def test_recognizes_chinese_and_portuguese_get_code_buttons(self):
        self.assertTrue(is_get_code_text("获取代码"))
        self.assertTrue(is_get_code_text("Obter código"))
        self.assertFalse(is_get_code_text("联系我"))

    def test_selects_exactly_six_subaccount_entries_in_email_order(self):
        browser_list = [
            {"browserName": "Mastertrade 店铺8"},
            {"browserName": "Smartech 店铺3-子账号"},
            {"browserName": "Eco Aventura 店铺1-子账号"},
            {"browserName": "Apolotech 店铺6-子账号"},
            {"browserName": "Sany店铺2-子账号"},
            {"browserName": "Planetechnology 店铺7-子账号"},
            {"browserName": "Mastertrade 店铺8-子账号"},
        ]

        selected = select_subaccount_stores(browser_list)

        self.assertEqual(list(selected), list(EXPECTED_STORES))
        self.assertTrue(all(item["browserName"].endswith("-子账号") for item in selected.values()))

    def test_rejects_incomplete_store_access(self):
        with self.assertRaises(ValueError):
            select_subaccount_stores([{"browserName": "Eco Aventura 店铺1-子账号"}])

    def test_agent_mapping_contains_all_configured_mail_names(self):
        self.assertEqual(
            set(ZINIAO_STORE_NAMES),
            {
                "ECO AVENTURA LTDA",
                "SANY INTERNACIONAL",
                "SMARTECH VARIEDADES LTDA",
                "APOLOTECH VARIEDADES LTDA",
                "Planetechnology",
                "Mastertrade",
            },
        )


if __name__ == "__main__":
    unittest.main()
