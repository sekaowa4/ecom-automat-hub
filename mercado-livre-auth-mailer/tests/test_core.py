import unittest
from datetime import datetime

from shop_auth_codes.core import (
    BeforeCutoffError,
    DuplicateBatchError,
    StoreCode,
    build_batch_fingerprint,
    mask_code,
    render_message,
    validate_batch,
    validate_cutoff,
)


class CoreTests(unittest.TestCase):
    def test_rejects_runs_before_daily_cutoff(self):
        with self.assertRaises(BeforeCutoffError):
            validate_cutoff(datetime(2026, 9, 14, 12, 59), "13:00")

    def test_accepts_runs_after_daily_cutoff(self):
        validate_cutoff(datetime(2026, 9, 14, 13, 1), "13:00")

    def test_validates_all_configured_store_codes_in_order(self):
        stores = ["ECO AVENTURA LTDA", "SANY INTERNACIONAL"]
        codes = [
            StoreCode("SANY INTERNACIONAL", "E5F6G7H8"),
            StoreCode("ECO AVENTURA LTDA", "A1B2C3D4"),
        ]

        result = validate_batch(stores, codes, r"^[A-Z0-9]{8}$")

        self.assertEqual(
            [(item.store_name, item.code) for item in result],
            [
                ("ECO AVENTURA LTDA", "A1B2C3D4"),
                ("SANY INTERNACIONAL", "E5F6G7H8"),
            ],
        )

    def test_rejects_missing_store_code(self):
        with self.assertRaises(ValueError):
            validate_batch(
                ["ECO AVENTURA LTDA", "SANY INTERNACIONAL"],
                [StoreCode("ECO AVENTURA LTDA", "A1B2C3D4")],
                r"^[A-Z0-9]{8}$",
            )

    def test_rejects_duplicate_codes(self):
        with self.assertRaises(ValueError):
            validate_batch(
                ["ECO AVENTURA LTDA", "SANY INTERNACIONAL"],
                [
                    StoreCode("ECO AVENTURA LTDA", "A1B2C3D4"),
                    StoreCode("SANY INTERNACIONAL", "A1B2C3D4"),
                ],
                r"^[A-Z0-9]{8}$",
            )

    def test_renders_fixed_template_without_reordering_stores(self):
        template = "{{date}}\n{{store_lines}}\n"
        codes = [
            StoreCode("ECO AVENTURA LTDA", "A1B2C3D4"),
            StoreCode("SANY INTERNACIONAL", "E5F6G7H8"),
        ]

        self.assertEqual(
            render_message(template, "2026/9/14", codes),
            "2026/9/14\nECO AVENTURA LTDA: A1B2C3D4\nSANY INTERNACIONAL: E5F6G7H8\n",
        )

    def test_fingerprint_is_stable_for_same_batch(self):
        codes = [StoreCode("ECO AVENTURA LTDA", "A1B2C3D4")]
        self.assertEqual(
            build_batch_fingerprint("2026/9/14", codes),
            build_batch_fingerprint("2026/9/14", codes),
        )

    def test_duplicate_batch_error_is_available_for_ledger(self):
        self.assertTrue(issubclass(DuplicateBatchError, RuntimeError))

    def test_masks_code_in_logs(self):
        self.assertEqual(mask_code("A1B2C3D4"), "A1B2****")


if __name__ == "__main__":
    unittest.main()
