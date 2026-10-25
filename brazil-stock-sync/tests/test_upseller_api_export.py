import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from upseller_api_export import UpSellerClient, extract_download_url, extract_job_id, run_upseller_full_inventory_export, sales_date_range


class UpSellerApiExportTests(unittest.TestCase):
    def test_inventory_export_headers_match_current_platform_positions(self):
        from upseller_api_export import run_upseller_api_exports
        captured = []
        class Client:
            def request_json(self, path, *args, **kwargs):
                return {"data": [{"idStr": "warehouse", "skuCount": 1}]}
            def export_and_download(self, path, data, output, *args, **kwargs):
                if path == "/api/tpl-warehouse-sku/export":
                    captured.append(data["heads"])
                return output
        with tempfile.TemporaryDirectory() as directory, patch("upseller_api_export.UpSellerClient", return_value=Client()), patch("upseller_api_export.load_us_u", return_value="test"):
            run_upseller_api_exports(Path(directory))
        headers = captured[0]
        self.assertEqual(len(headers), 22)
        self.assertEqual(headers[6:8], ["UpSeller占用数", "UpSeller可用库存"])
        self.assertEqual(headers[11], "三方仓不可用")
        self.assertEqual(headers[21], "创建时间")

    def test_extracts_job_id_from_export_response(self):
        self.assertEqual(extract_job_id({"code": 0, "data": "ORDER_EXPORT:1"}), "ORDER_EXPORT:1")

    def test_extracts_nested_download_url(self):
        payload = {"data": {"result": {"downloadUrl": "https://print-label.upseller.cn/file.xlsx"}}}
        self.assertEqual(extract_download_url(payload), "https://print-label.upseller.cn/file.xlsx")

    def test_sales_range_excludes_today(self):
        start, end = sales_date_range(3, "2026-08-20")
        self.assertEqual((start, end), ("2026-08-17", "2026-08-19"))

    def test_retries_a_reset_connection_once(self):
        calls = []

        def run(*_args, **_kwargs):
            calls.append(1)
            if len(calls) == 1:
                return SimpleNamespace(returncode=35, stderr=b"curl: (35) Recv failure: Connection was reset", stdout=b"")
            return SimpleNamespace(returncode=0, stderr=b"", stdout=b"{}")

        with patch("upseller_api_export.subprocess.run", side_effect=run), patch("upseller_api_export.time.sleep"):
            self.assertEqual(UpSellerClient("test-cookie")._curl("/api/is-login"), b"{}")
        self.assertEqual(len(calls), 2)

    def test_direct_requests_bypass_unreliable_local_proxy(self):
        command = []

        def run(args, **_kwargs):
            command.extend(args)
            return SimpleNamespace(returncode=0, stderr=b"", stdout=b"{}")

        with patch("upseller_api_export.subprocess.run", side_effect=run):
            UpSellerClient("test-cookie")._curl("/api/is-login")

        self.assertIn("--noproxy", command)
        self.assertIn("*", command)

    def test_retries_transient_empty_export_creation_response(self):
        client = UpSellerClient("test-cookie")
        responses = iter([
            {"code": 0, "data": None},
            {"code": 0, "data": "ORDER_EXPORT:2"},
            {"code": 0, "data": {"downloadUrl": "/file.xlsx"}},
        ])
        with tempfile.TemporaryDirectory() as directory, patch.object(client, "request_json", side_effect=lambda *_args, **_kwargs: next(responses)), patch.object(client, "download", return_value=b"xlsx"), patch("upseller_api_export.time.sleep"):
            output = client.export_and_download("/api/export", {}, Path(directory) / "file.xlsx", "https://app.upseller.com/")
            self.assertEqual(output.read_bytes(), b"xlsx")

    def test_full_export_always_requests_a_new_full_inventory_file(self):
        calls = []

        class FakeClient:
            def export_and_download(self, path, data, output, referer, as_form=False):
                calls.append((path, data, output, referer, as_form))
                output.write_bytes(b"latest-full")
                return output

        with tempfile.TemporaryDirectory() as directory:
            output = run_upseller_full_inventory_export(Path(directory), client=FakeClient())

        self.assertEqual(output.name, "FULL库存数据.xlsx")
        self.assertEqual(len(calls), 1)
        self.assertEqual(calls[0][0], "/api/full-inventory/export")
        self.assertEqual(calls[0][1]["fullStatus"], "all_full")

    def test_full_export_can_use_a_timestamped_output_when_standard_file_is_locked(self):
        class FakeClient:
            def export_and_download(self, _path, _data, output, _referer, as_form=False):
                output.write_bytes(b"latest-full")
                return output

        with tempfile.TemporaryDirectory() as directory:
            output = run_upseller_full_inventory_export(
                Path(directory), client=FakeClient(), filename="FULL库存数据_20260922_1740.xlsx"
            )

        self.assertEqual(output.name, "FULL库存数据_20260922_1740.xlsx")
