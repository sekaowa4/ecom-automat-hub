import tempfile
import unittest
from pathlib import Path

from shop_auth_codes.config import load_config


class ConfigTests(unittest.TestCase):
    def test_loads_store_mail_and_rule_settings_from_toml(self):
        content = """
[rules]
timezone = "Asia/Shanghai"
cutoff = "13:00"
date_format = "%Y/%-m/%-d"
code_regex = "^[A-Z0-9]{8}$"

[mail]
sender = "sender@example.com"
recipients = ["receiver@example.com"]
subject = "Authorization codes"
template = "template.txt"
imap_host = "imap.example.com"
smtp_host = "smtp.example.com"

[[stores]]
name = "ECO AVENTURA LTDA"
account = "eco-account"
profile_dir = "profiles/eco"
start_url = "https://example.com"

[[stores]]
name = "SANY INTERNACIONAL"
account = "sany-account"
profile_dir = "profiles/sany"
start_url = "https://example.com"
"""
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "config.toml"
            path.write_text(content, encoding="utf-8")
            settings = load_config(path)

        self.assertEqual(settings.rules.cutoff, "13:00")
        self.assertEqual([store.name for store in settings.stores], [
            "ECO AVENTURA LTDA",
            "SANY INTERNACIONAL",
        ])
        self.assertEqual(settings.mail.recipients, ("receiver@example.com",))


if __name__ == "__main__":
    unittest.main()
