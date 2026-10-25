import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import server
from inventory_pipeline import validate_target_layout
from server import (
    build_inventory_preview,
    apply_export_progress_event,
    create_export_job,
    get_export_job,
    parse_portal_preview_output,
    portal_source_manifest,
    publish_export_file,
    validate_export_headers,
)


class PortalPreviewOutputTests(unittest.TestCase):
    def test_parses_json_preview_output(self):
        payload = {"mode": "preview_only", "overallStatus": "ready", "readyCount": 2, "blockedCount": 0, "portals": []}
        self.assertEqual(parse_portal_preview_output(json.dumps(payload)), payload)

    def test_rejects_invalid_preview_output(self):
        with self.assertRaises(ValueError):
            parse_portal_preview_output("not-json")

    def test_validates_required_headers_for_each_export(self):
        self.assertEqual(
            validate_export_headers("upseller", ["SKU", "店铺", "销量"]),
            {"ok": True, "missing": []},
        )
    def test_rejects_unknown_export_header_profile(self):
        with self.assertRaises(ValueError):
            validate_export_headers("unknown", ["SKU"])

    def test_builds_inventory_preview_from_engine_report(self):
        preview = build_inventory_preview({
            "errors": [],
            "warnings": [{"type": "unmatched_product"}, {"type": "unmatched_product"}],
            "summary": {
                "target_rows": 100,
                "target_skus_matched_in_inventory": 90,
                "target_skus_matched_in_sales": 80,
            },
        })
        self.assertEqual(preview["ok"], True)
        self.assertEqual(preview["targetRows"], 100)
        self.assertEqual(preview["priorityReplenishment"], 0)
        self.assertEqual(preview["dataTime"], "")
        self.assertEqual(preview["unmatchedCount"], 2)
        self.assertEqual(preview["canConfirm"], True)
        self.assertEqual(preview["unmatched"][0]["type"], "unmatched_product")

    def test_preview_copy_saves_a_downloadable_workbook_without_unblocking_confirmation(self):
        report = {
            "errors": [],
            "warnings": [{"type": "unmatched_product", "row": 8, "product": "Missing"}],
            "summary": {"target_rows": 1, "target_skus_matched_in_inventory": 0, "target_skus_matched_in_sales": 0},
        }
        with tempfile.TemporaryDirectory() as temp_dir:
            runtime = Path(temp_dir)
            with patch.object(server, "RUNTIME", runtime), patch.object(
                server, "portal_source_manifest", return_value={"inventory": runtime / "inventory.xlsx"}
            ), patch.object(server, "_portal_export_files_ready", return_value=True), patch.object(
                server, "validate_source_files", return_value={"ok": True, "missing": []}
            ), patch.object(server, "process_files", return_value=(b"preview-bytes", report)):
                result = server.run_local_inventory_preview_copy()

            self.assertTrue(result["ok"])
            self.assertTrue(result["preview"]["canConfirm"])
            self.assertTrue(result["workbook_url"].startswith("/downloads/preview_stock_order_"))
            output = runtime / result["workbook_url"].removeprefix("/downloads/")
            self.assertEqual(output.read_bytes(), b"preview-bytes")

    def test_wecom_status_masks_the_configured_mcp_url(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            config_path = Path(temp_dir) / "wecom_config.json"
            with patch.object(server, "WECOM_CONFIG_PATH", config_path):
                server.save_wecom_config(
                    "https://qyapi.weixin.qq.com/mcp/robot-doc?apikey=REDACTED",
                    "https://doc.weixin.qq.com/sheet/e3_stock",
                )
                status = server.wecom_status()

        self.assertTrue(status["configured"])
        self.assertEqual(status["sheetUrl"], "https://doc.weixin.qq.com/sheet/e3_stock")
        self.assertNotIn("secret-value", status["endpointHint"])

    def test_creates_and_reads_export_job(self):
        with tempfile.TemporaryDirectory() as temp_dir, patch.object(server, "JOB_DIR", Path(temp_dir)):
            job_id = create_export_job()["job_id"]
            job = get_export_job(job_id)
        self.assertEqual(job["status"], "queued")
        self.assertEqual(len(job["steps"]), 6)

    def test_unknown_export_job_returns_none(self):
        self.assertIsNone(get_export_job("missing-job"))

    def test_stale_running_job_is_reported_as_failed(self):
        with tempfile.TemporaryDirectory() as temp_dir, patch.object(server, "JOB_DIR", Path(temp_dir)), patch.object(server, "EXPORT_STEP_TIMEOUT_SECONDS", 1):
            job_id = create_export_job()["job_id"]
            state = get_export_job(job_id)
            state["status"] = "running"
            state["startedAt"] = 0
            Path(temp_dir, f"{job_id}.json").write_text(json.dumps(state), encoding="utf-8")
            server.EXPORT_JOBS.clear()

            recovered = get_export_job(job_id)

        self.assertEqual(recovered["status"], "failed")
        self.assertIn("超时", recovered["error"])

    def test_corrupt_job_file_is_reported_as_failed_instead_of_missing(self):
        with tempfile.TemporaryDirectory() as temp_dir, patch.object(server, "JOB_DIR", Path(temp_dir)):
            Path(temp_dir, "broken.json").write_text('{"status":"running"', encoding="utf-8")

            recovered = get_export_job("broken")

        self.assertEqual(recovered["status"], "failed")
        self.assertIn("状态文件损坏", recovered["error"])

    def test_export_job_can_be_recovered_from_its_persisted_json_after_service_restart(self):
        with tempfile.TemporaryDirectory() as temp_dir, patch.object(server, "JOB_DIR", Path(temp_dir)):
            job_id = create_export_job()["job_id"]
            server.EXPORT_JOBS.clear()

            recovered = get_export_job(job_id)

        self.assertEqual(recovered["job_id"], job_id)
        self.assertEqual(recovered["status"], "queued")

    def test_portal_manifest_prefers_newly_exported_files(self):
        manifest = portal_source_manifest()
        self.assertEqual(manifest["30天"].name, "近30天销售数据.xlsx")
        self.assertEqual(manifest["inventory"].name, "库存数据.xlsx")

    def test_portal_manifest_uses_the_validated_runtime_template_for_preview(self):
        manifest = portal_source_manifest()

        self.assertEqual(manifest["target"], server.RUNTIME / "stock_order_template_fixed_layout.xlsx")
        self.assertEqual(validate_target_layout(manifest["target"]), [])


    def test_old_export_file_is_not_counted_for_a_new_job(self):
        with tempfile.NamedTemporaryFile(delete=False) as handle:
            path = handle.name
        try:
            baseline = {path: os.stat(path).st_mtime_ns}
            self.assertFalse(server.is_new_export_file(path, baseline))
            with open(path, "ab") as handle:
                handle.write(b"new")
            newer = baseline[path] + 1_000_000
            os.utime(path, ns=(newer, newer))
            self.assertTrue(server.is_new_export_file(path, baseline))
        finally:
            os.unlink(path)

    def test_export_timeout_is_based_on_last_progress_not_total_batch_age(self):
        self.assertFalse(server.is_export_step_timed_out(last_progress=100, now=250, timeout_seconds=180))
        self.assertTrue(server.is_export_step_timed_out(last_progress=100, now=281, timeout_seconds=180))

    def test_default_export_step_timeout_allows_a_three_minute_portal_task(self):
        self.assertFalse(server.is_export_step_timed_out(last_progress=100, now=161))

    def test_progress_event_marks_the_reported_step_complete_without_waiting_for_file_timestamps(self):
        with tempfile.TemporaryDirectory() as temp_dir, patch.object(server, "JOB_DIR", Path(temp_dir)):
            state = create_export_job()
            apply_export_progress_event(state, {"event": "step-complete", "index": 2, "path": "runtime/portal-downloads/近15天销售数据.xlsx"})

        self.assertEqual(state["steps"][2]["status"], "completed")
        self.assertEqual(state["currentIndex"], 3)
        self.assertEqual(state["steps"][3]["status"], "running")

    def test_publishes_each_latest_export_by_overwriting_the_review_copy(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            source = Path(temp_dir) / "source.xlsx"
            review_dir = Path(temp_dir) / "导出文件"
            source.write_bytes(b"latest-export")
            destination = review_dir / source.name
            review_dir.mkdir()
            destination.write_bytes(b"old-export")

            result = publish_export_file(source, review_dir)

            self.assertEqual(result, destination)
            self.assertEqual(destination.read_bytes(), b"latest-export")

    def test_retries_publishing_when_the_destination_is_temporarily_locked(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            source = Path(temp_dir) / "source.xlsx"
            review_dir = Path(temp_dir) / "导出文件"
            source.write_bytes(b"latest-export")
            real_copy = server.shutil.copy2
            calls = {"count": 0}

            def flaky_copy(src, dst):
                calls["count"] += 1
                if calls["count"] < 3:
                    raise PermissionError(32, "file locked")
                return real_copy(src, dst)

            with patch.object(server.shutil, "copy2", side_effect=flaky_copy), patch.object(server.time, "sleep"):
                result = publish_export_file(source, review_dir)

            self.assertEqual(result.read_bytes(), b"latest-export")
            self.assertEqual(calls["count"], 3)


if __name__ == "__main__":
    unittest.main()
