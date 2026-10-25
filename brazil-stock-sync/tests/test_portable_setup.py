import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import preflight_check
from portable_setup import save_runtime_config


class PortableSetupTests(unittest.TestCase):
    def test_writes_only_runtime_connection_configs(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            runtime = Path(temp_dir) / "runtime"

            save_runtime_config(
                runtime,
                "https://doc.weixin.qq.com/sheet/formal-stock",
                "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=test",
            )

            self.assertEqual(
                json.loads((runtime / "wecom_config.json").read_text(encoding="utf-8"))["sheet_url"],
                "https://doc.weixin.qq.com/sheet/formal-stock",
            )
            self.assertEqual(
                json.loads((runtime / "automation_config.json").read_text(encoding="utf-8"))["webhook_url"],
                "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=test",
            )
            self.assertEqual(sorted(path.name for path in runtime.iterdir()), ["automation_config.json", "wecom_config.json"])

    def test_preflight_accepts_a_configured_formal_sheet(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            config = root / "wecom_config.json"
            config.write_text(json.dumps({"sheet_url": "https://doc.weixin.qq.com/sheet/formal-stock"}), encoding="utf-8")
            cli = root / "wecom-cli"
            cli.mkdir()
            (cli / "mcp_config.enc").write_bytes(b"configured")
            runtime = root / "runtime"
            runtime.mkdir()
            (runtime / "automation_config.json").write_text(
                json.dumps({"webhook_url": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=test"}), encoding="utf-8"
            )
            with patch.object(preflight_check.server, "WECOM_CONFIG_PATH", config), patch.object(
                preflight_check.server, "WECOM_CLI_CONFIG_DIR", cli
            ), patch.object(preflight_check.server, "JOB_DIR", root / "jobs"), patch.object(
                preflight_check.server, "ROOT", root
            ), patch(
                "preflight_check._port_open", return_value=False
            ), patch(
                "preflight_check.load_us_u", return_value="local-cookie"
            ):
                checks = {name: ok for name, _, ok in preflight_check.check_local_state()}

            self.assertTrue(checks["目标在线表"])
            self.assertTrue(checks["UpSeller 接口 Cookie"])
            self.assertTrue(checks["企微群 Webhook"])
