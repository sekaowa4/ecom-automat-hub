import json
import tempfile
import unittest
from pathlib import Path

from shop_auth_codes.audit import AuditLog


class AuditLogTests(unittest.TestCase):
    def test_appends_json_events_without_storing_code_values(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "run.log"
            AuditLog(path).write({
                "status": "sent_verified",
                "store": "ECO AVENTURA LTDA",
                "code_mask": "A1B2****",
            })

            event = json.loads(path.read_text(encoding="utf-8"))

        self.assertEqual(event["status"], "sent_verified")
        self.assertNotIn("A1B2C3D4", event.values())


if __name__ == "__main__":
    unittest.main()
