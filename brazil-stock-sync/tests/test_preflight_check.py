import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import preflight_check


class PreflightCheckTests(unittest.TestCase):
    def test_marks_the_configured_test_table_as_valid(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            config = Path(temp_dir) / "wecom_config.json"
            config.write_text(json.dumps({"sheet_url": "https://doc.weixin.qq.com/sheet/e3_EXAMPLE_713109ebddea"}), encoding="utf-8")
            with patch.object(preflight_check.server, "WECOM_CONFIG_PATH", config), patch.object(preflight_check.server, "JOB_DIR", Path(temp_dir) / "jobs"), patch.object(preflight_check.server, "WECOM_CLI_CONFIG_DIR", Path(temp_dir) / "cli"):
                checks = dict((name, ok) for name, _, ok in preflight_check.check_local_state())
        self.assertTrue(checks["目标表为测试表"])

    def test_reports_a_stale_running_job(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            job_dir = Path(temp_dir)
            (job_dir / "stale.json").write_text(json.dumps({"status": "running"}), encoding="utf-8")
            with patch.object(preflight_check.server, "WECOM_CONFIG_PATH", Path(temp_dir) / "missing.json"), patch.object(preflight_check.server, "JOB_DIR", job_dir), patch.object(preflight_check.Path, "exists", return_value=False):
                checks = dict((name, ok) for name, _, ok in preflight_check.check_local_state())
        self.assertFalse(checks["残留运行任务"])
