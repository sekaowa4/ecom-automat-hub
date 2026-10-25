import tempfile
import unittest
from pathlib import Path

from shop_auth_codes.core import DuplicateBatchError
from shop_auth_codes.ledger import BatchLedger


class LedgerTests(unittest.TestCase):
    def test_records_sent_batch_and_rejects_it_again(self):
        with tempfile.TemporaryDirectory() as directory:
            ledger = BatchLedger(Path(directory) / "runs.json")
            ledger.ensure_not_sent("2026/9/14", "fingerprint")
            ledger.mark_sent("2026/9/14", "fingerprint", "<message@example.com>")

            with self.assertRaises(DuplicateBatchError):
                ledger.ensure_not_sent("2026/9/14", "fingerprint")

    def test_allows_retry_of_unverified_batch(self):
        with tempfile.TemporaryDirectory() as directory:
            ledger = BatchLedger(Path(directory) / "runs.json")
            ledger.mark_sent(
                "2026/9/14",
                "fingerprint",
                "<message@example.com>",
                "sent_unverified",
            )

            ledger.ensure_not_sent("2026/9/14", "fingerprint")


if __name__ == "__main__":
    unittest.main()
