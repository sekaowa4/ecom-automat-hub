import json
import tempfile
import unittest
from datetime import datetime
from pathlib import Path
from unittest.mock import patch

import daily_update
from daily_update import build_browser_writeback_payload, filter_updates_to_online_skus, format_notification, open_online_sheet, save_audit, send_wecom_notification, update_online_timestamp, verify_online_writeback, write_online_updates_by_sku


class DailyUpdateTests(unittest.TestCase):
    def test_browser_writeback_payload_uses_online_sku_order_and_preserves_existing_values(self):
        payload = build_browser_writeback_payload(
            [
                {"SKU": "B", "R": "7", "S": "old"},
                {"SKU": "A", "R": "3", "S": "old-a"},
            ],
            {"A": {"R": 9}},
        )

        self.assertEqual(payload["skus"], ["B", "A"])
        self.assertEqual(payload["updates_by_sku"]["B"]["R"], "7")
        self.assertEqual(payload["updates_by_sku"]["A"]["R"], 9)
        self.assertEqual(payload["updates_by_sku"]["A"]["S"], "old-a")

    def test_updates_online_timestamp_cell(self):
        client = unittest.mock.Mock()
        update_online_timestamp(client, "https://sheet", "s1", at=datetime(2026, 8, 12, 11, 0))
        client.call_tool.assert_called_once()
        args = client.call_tool.call_args.args
        self.assertEqual(args[0], "sheet_update_range_data")
        self.assertEqual(args[1]["grid_data"]["start_row"], 0)
        self.assertIn("8.12", str(args[1]))
        text_format = args[1]["grid_data"]["rows"][0]["values"][0]["cell_format"]["text_format"]
        self.assertEqual(text_format["font_size"], 18)
        self.assertEqual(text_format["color"], {"red": 255, "green": 0, "blue": 0, "alpha": 255})

    def test_reopens_mcp_session_when_online_sheet_read_is_reset(self):
        attempts = []
        def factory(endpoint):
            attempts.append(endpoint)
            return object()
        with patch("daily_update.read_ordinary_sheet_rows", side_effect=[ConnectionResetError(), ("s1", [{"SKU": "A"}])]):
            client, sheet_id, rows = open_online_sheet("https://mcp", "https://sheet", factory, attempts=2, delay=0)
        self.assertEqual((sheet_id, rows[0]["SKU"]), ("s1", "A"))
        self.assertEqual(len(attempts), 2)

    def test_online_writeback_verification_retries_stale_read(self):
        with patch("daily_update.open_online_sheet", return_value=(object(), "s1", [{"row_index": 5, "SKU": "A", "R": 1}])) as read, patch(
            "daily_update.verify_fixed_layout_updates", side_effect=[RuntimeError("stale"), {"verified_rows": 1}]
        ):
            result = verify_online_writeback("https://mcp", "https://sheet", [{"row_index": 5, "SKU": "A"}], {"A": {"R": 1}}, attempts=2, delay=0)
        self.assertEqual(result, {"verified_rows": 1})
        self.assertEqual(read.call_count, 2)

    def test_dynamic_writer_uses_online_sku_rows_not_template_positions(self):
        client = object()
        online_rows = [
            {"row_index": 6, "SKU": "B", "R": 0},
            {"row_index": 7, "SKU": "A", "R": 0},
        ]
        updates = {"A": {"R": 1}, "B": {"R": 2}}
        with patch("daily_update.write_ordinary_sheet_column_updates", return_value={"updated_rows": 2, "written_cells": 16, "requests": 1}) as write:
            result = write_online_updates_by_sku(client, "https://sheet", "s1", online_rows, updates, force_all=True)

        first_call_rows = write.call_args_list[0].args[3]
        self.assertEqual([row["SKU"] for row in first_call_rows], ["B", "A"])
        self.assertEqual(result["updated_rows"], 2)
    def test_formats_success_notification(self):
        text = format_notification(True, {"updated_rows": 1082}, "2026-08-05 11:03")
        self.assertIn("今日库存已更新", text)
        self.assertIn("1082", text)

    def test_filters_only_skus_that_exist_online(self):
        updates, skipped = filter_updates_to_online_skus(
            [{"SKU": "A"}, {"SKU": "B"}],
            {"A": {"R": 1}, "C": {"R": 3}},
        )
        self.assertEqual(updates, {"A": {"R": 1}})
        self.assertEqual(skipped, ["C"])

    def test_formats_failure_notification_without_secrets(self):
        text = format_notification(False, {"error": "UpSeller 导出超时"}, "2026-08-05 11:03")
        self.assertIn("今日库存更新失败", text)
        self.assertIn("UpSeller 导出超时", text)

    def test_wecom_notification_bypasses_unreliable_local_proxy(self):
        response = unittest.mock.Mock()
        response.__enter__ = unittest.mock.Mock(return_value=response)
        response.__exit__ = unittest.mock.Mock(return_value=False)
        response.read.return_value = b'{"errcode":0}'
        with patch("daily_update.build_opener") as build_opener:
            build_opener.return_value.open.return_value = response
            send_wecom_notification("https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=test", "ok")
        build_opener.assert_called_once()
        build_opener.return_value.open.assert_called_once()

    def test_saves_audit_json(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            path = save_audit(Path(temp_dir), {"status": "completed", "updated_rows": 2})
            self.assertEqual(json.loads(path.read_text(encoding="utf-8"))["updated_rows"], 2)

    def test_runs_complete_export_generate_writeback_and_notification_flow(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            runtime = Path(temp_dir)
            clock = {"now": datetime(2026, 10, 11, 10, 9)}
            automation_config = runtime / "automation_config.json"
            wecom_config = runtime / "wecom_config.json"
            automation_config.write_text(json.dumps({"webhook_url": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=test"}), encoding="utf-8")
            wecom_config.write_text(json.dumps({"mcp_url": "https://qyapi.weixin.qq.com/mcp/test", "sheet_url": "https://doc.weixin.qq.com/sheet/test"}), encoding="utf-8")
            report = {
                "errors": [],
                "writeback_updates": {"A": {"R": 1}},
                "writeback_all_updates": {"A": {"R": 1}},
                "writeback_layout_rows": [{"row_index": 5, "SKU": "A"}],
            }
            def finish_export(_job_id):
                clock["now"] = datetime(2026, 10, 11, 10, 39)

            with patch("daily_update.beijing_now", side_effect=lambda: clock["now"]), patch(
                "server.launch_portal_login"
            ), patch("server.create_export_job", return_value={"job_id": "j1"}), patch(
                "server.run_portal_batch", side_effect=finish_export
            ), patch("server.get_export_job", return_value={"status": "completed"}), patch(
                "server.portal_source_manifest", return_value={"target": runtime / "target.xlsx"}
            ), patch("server.process_files", return_value=(b"xlsx", report)), patch(
                "daily_update.load_local_doc_mcp_endpoint", return_value="https://qyapi.weixin.qq.com/mcp/bot/doc?uaKey=test"
            ), patch("daily_update.open_online_sheet", return_value=(object(), "s1", [{"row_index": 5, "SKU": "A", "R": 0}])), patch(
                "daily_update.update_online_timestamp"
            ) as update_timestamp, patch(
                "daily_update.write_online_updates_by_sku", return_value={"updated_rows": 1, "ranges": 3, "written_cells": 1, "requests": 1}
            ), patch(
                "daily_update.verify_online_writeback_by_sku", return_value={"verified_rows": 1}
            ) as verify, patch(
                "daily_update.send_wecom_notification"
            ) as notify:
                result = daily_update.run_daily_update(runtime, automation_config, wecom_config)

            self.assertEqual(result["status"], "completed")
            self.assertEqual(result["updated_rows"], 1)
            self.assertEqual(result["finished_at"], "2026-10-11 10:39")
            self.assertEqual(Path(result["workbook_path"]).name, "本次生成Stock表.xlsx")
            self.assertTrue(Path(result["workbook_path"]).exists())
            update_timestamp.assert_called_once()
            verify.assert_called_once()
            notify.assert_called_once()
            self.assertIn("更新时间：2026-10-11 10:39", notify.call_args.args[1])

    def test_failure_is_audited_and_not_notified_without_writeback(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            runtime = Path(temp_dir)
            automation_config = runtime / "automation_config.json"
            wecom_config = runtime / "wecom_config.json"
            automation_config.write_text(json.dumps({"webhook_url": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=test"}), encoding="utf-8")
            wecom_config.write_text(json.dumps({"mcp_url": "https://qyapi.weixin.qq.com/mcp/test", "sheet_url": "https://doc.weixin.qq.com/sheet/test"}), encoding="utf-8")
            with patch("server.create_export_job", side_effect=RuntimeError("login required")), patch(
                "daily_update.send_wecom_notification"
            ) as notify:
                with self.assertRaisesRegex(RuntimeError, "login required"):
                    daily_update.run_daily_update(runtime, automation_config, wecom_config)

            notify.assert_not_called()
            audits = list((runtime / "audits").glob("daily_update_*.json"))
            self.assertEqual(json.loads(audits[0].read_text(encoding="utf-8"))["status"], "failed")
            self.assertEqual(json.loads(audits[0].read_text(encoding="utf-8"))["notification_status"], "suppressed")

    def test_failure_audit_records_notification_send_error(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            runtime = Path(temp_dir)
            automation_config = runtime / "automation_config.json"
            wecom_config = runtime / "wecom_config.json"
            automation_config.write_text(json.dumps({"webhook_url": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=test"}), encoding="utf-8")
            wecom_config.write_text(json.dumps({"mcp_url": "https://qyapi.weixin.qq.com/mcp/test", "sheet_url": "https://doc.weixin.qq.com/sheet/test"}), encoding="utf-8")
            with patch("server.create_export_job", side_effect=RuntimeError("authorization expired")), patch(
                "daily_update.send_wecom_notification", side_effect=ConnectionError("webhook unavailable")
            ):
                with self.assertRaisesRegex(RuntimeError, "authorization expired"):
                    daily_update.run_daily_update(runtime, automation_config, wecom_config)

            audits = list((runtime / "audits").glob("daily_update_*.json"))
            audit = json.loads(audits[0].read_text(encoding="utf-8"))
            self.assertEqual(audit["notification_status"], "suppressed")
            self.assertNotIn("notification_error", audit)

    def test_mcp_preflight_is_silent_when_the_online_sheet_is_readable(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            runtime = Path(temp_dir)
            automation_config = runtime / "automation_config.json"
            wecom_config = runtime / "wecom_config.json"
            automation_config.write_text(json.dumps({"webhook_url": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=test"}), encoding="utf-8")
            wecom_config.write_text(json.dumps({"mcp_url": "https://qyapi.weixin.qq.com/mcp/test", "sheet_url": "https://doc.weixin.qq.com/sheet/test"}), encoding="utf-8")
            with patch("daily_update.read_ordinary_sheet_rows", return_value=("s1", [{"SKU": "A"}])), patch(
                "daily_update.send_wecom_notification"
            ) as notify:
                result = daily_update.run_mcp_authorization_preflight(runtime, automation_config, wecom_config)

            self.assertEqual(result["status"], "available")
            notify.assert_not_called()
            audits = list((runtime / "audits").glob("mcp_preflight_*.json"))
            self.assertEqual(json.loads(audits[0].read_text(encoding="utf-8"))["status"], "available")

    def test_mcp_preflight_notifies_and_audits_when_authorization_is_expired(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            runtime = Path(temp_dir)
            automation_config = runtime / "automation_config.json"
            wecom_config = runtime / "wecom_config.json"
            automation_config.write_text(json.dumps({"webhook_url": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=test"}), encoding="utf-8")
            wecom_config.write_text(json.dumps({"mcp_url": "https://qyapi.weixin.qq.com/mcp/test", "sheet_url": "https://doc.weixin.qq.com/sheet/test"}), encoding="utf-8")
            with patch("daily_update.read_ordinary_sheet_rows", side_effect=RuntimeError("authorization expired")), patch(
                "daily_update.send_wecom_notification"
            ) as notify:
                with self.assertRaisesRegex(RuntimeError, "authorization expired"):
                    daily_update.run_mcp_authorization_preflight(runtime, automation_config, wecom_config)

                notify.assert_not_called()
                audits = list((runtime / "audits").glob("mcp_preflight_*.json"))
                audit = json.loads(audits[0].read_text(encoding="utf-8"))
                self.assertEqual(audit["status"], "failed")
                self.assertEqual(audit["notification_status"], "suppressed")


if __name__ == "__main__":
    unittest.main()

