import json
import base64
import os
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch

from cryptography.hazmat.primitives.ciphers.aead import AESGCM

from wecom_sync import (
    WeComMCPClient,
    build_record_batches,
    build_sheet_grid_data,
    build_sheet_row_index,
    decode_mcp_response,
    filter_online_changed_updates,
    load_local_doc_mcp_endpoint,
    parse_ordinary_sheet_markdown,
    read_ordinary_sheet_rows,
    verify_online_updates_by_sku,
    verify_fixed_layout_updates,
    write_ordinary_sheet_column_updates,
    write_fixed_layout_updates,
    write_ordinary_sheet_updates,
)


class WeComSyncTests(unittest.TestCase):
    def test_new_template_first_sku_is_physical_row_four(self):
        content = "订单库存总表\n|最近更新时间：10.13||\n|---|---|\n|产品|SKU|\n|||\n|产品A|NW-A|\n|产品B|NW-B|\n"
        rows = parse_ordinary_sheet_markdown(content, "订单库存总表")
        self.assertEqual([(row["SKU"], row["row_index"]) for row in rows], [("NW-A", 3), ("NW-B", 4)])

    @staticmethod
    def mcp_result(payload):
        return {"content": [{"type": "text", "text": json.dumps(payload, ensure_ascii=False)}]}

    def test_indexes_normal_sheet_rows_by_sku(self):
        self.assertEqual(build_sheet_row_index([{"row_index": 6, "SKU": "A"}, {"row_index": 7, "SKU": "B"}]), {"A": 6, "B": 7})

    def test_loads_document_endpoint_from_encrypted_cli_cache(self):
        with tempfile.TemporaryDirectory() as temporary:
            config_dir = Path(temporary)
            key = os.urandom(32)
            config_dir.joinpath(".encryption_key").write_text(base64.b64encode(key).decode("ascii"), encoding="utf-8")
            config = [{"url": "https://qyapi.weixin.qq.com/mcp/bot/doc?uaKey=test", "biz_type": "doc", "is_authed": True}]
            nonce = os.urandom(12)
            encrypted = AESGCM(key).encrypt(nonce, json.dumps(config).encode("utf-8"), None)
            config_dir.joinpath("mcp_config.enc").write_bytes(nonce + encrypted)
            self.assertEqual(load_local_doc_mcp_endpoint(config_dir), config[0]["url"])

    def test_mcp_client_bypasses_system_proxy(self):
        class FakeOpener:
            def open(self, request, timeout):
                return FakeResponse()

        class FakeResponse:
            headers = {}

            def __init__(self):
                self.lines = iter([b'{"jsonrpc":"2.0","id":1,"result":{}}'])

            def read(self):
                return b'{"jsonrpc":"2.0","id":1,"result":{}}'

            def readline(self):
                return next(self.lines, b"")

            def __enter__(self):
                return self

            def __exit__(self, *args):
                return False

        with patch("wecom_sync.build_opener") as build_opener:
            client = WeComMCPClient("https://qyapi.weixin.qq.com/mcp/bot/doc")
            client._opener = FakeOpener()
            result = client._post("initialize")

        self.assertEqual(result, {})
        self.assertEqual(build_opener.call_args.args[0].proxies, {})

    def test_mcp_client_returns_when_stream_result_arrives(self):
        class FakeResponse:
            headers = {}

            def __init__(self):
                self.lines = iter([
                    b"event: message\n",
                    b'data: {"jsonrpc":"2.0","id":1,"result":{"ok":true}}\n',
                    b"\n",
                ])

            def read(self):
                raise AssertionError("stream responses must not wait for connection close")

            def readline(self):
                return next(self.lines, b"")

            def __enter__(self):
                return self

            def __exit__(self, *args):
                return False

        with patch("wecom_sync.build_opener") as build_opener:
            opener = build_opener.return_value
            opener.open.return_value = FakeResponse()
            result = WeComMCPClient("https://qyapi.weixin.qq.com/mcp/bot/doc")._post("tools/call")

        self.assertEqual(result, {"ok": True})

    def test_mcp_client_sets_a_read_timeout_for_stream_responses(self):
        class FakeSocket:
            def __init__(self):
                self.timeout = None

            def settimeout(self, value):
                self.timeout = value

        class FakeResponse:
            headers = {}

            def __init__(self):
                self.socket = FakeSocket()
                self.fp = type("FP", (), {
                    "raw": type("Raw", (), {"_sock": self.socket})(),
                })()
                self.lines = iter([b'data: {"jsonrpc":"2.0","id":1,"result":{}}\n'])

            def readline(self):
                return next(self.lines, b"")

            def __enter__(self):
                return self

            def __exit__(self, *args):
                return False

        response = FakeResponse()
        with patch("wecom_sync.build_opener") as build_opener:
            build_opener.return_value.open.return_value = response
            WeComMCPClient("https://qyapi.weixin.qq.com/mcp/bot/doc")._post("tools/call")

        self.assertEqual(response.socket.timeout, 90)

    def test_mcp_client_closes_a_silent_stream_at_the_response_deadline(self):
        class FakeResponse:
            headers = {}

            def __init__(self):
                self.closed = False

            def readline(self):
                time.sleep(0.05)
                return b""

            def close(self):
                self.closed = True

            def __enter__(self):
                return self

            def __exit__(self, *args):
                return False

        response = FakeResponse()
        with patch("wecom_sync.build_opener") as build_opener, patch.object(WeComMCPClient, "RESPONSE_READ_TIMEOUT_SECONDS", 0.01):
            build_opener.return_value.open.return_value = response
            client = WeComMCPClient("https://qyapi.weixin.qq.com/mcp/bot/doc")
            with self.assertRaisesRegex(TimeoutError, "MCP 响应超时"):
                client._post("tools/call")

        self.assertTrue(response.closed)

    def test_parses_named_ordinary_sheet_rows_with_zero_based_indexes(self):
        content = """订单库存总表
|库存更新时间|2026-08-05|
|---|---|
|不同天数维度订单量||

|产品|SKU|库存|
|---|---|---|
|产品A|A-1|3|
|产品B|B-2||
||| 

其他工作表
|字段|
|---|
"""

        rows = parse_ordinary_sheet_markdown(content, "订单库存总表")

        self.assertEqual(rows, [
            {"row_index": 3, "产品": "产品A", "SKU": "A-1", "库存": "3"},
            {"row_index": 4, "产品": "产品B", "SKU": "B-2", "库存": ""},
        ])

    def test_preserves_fixed_writeback_columns_when_merged_headers_are_blank(self):
        headers = ["产品", "SKU"] + [""] * 51
        values = ["产品A", "A-1"] + [""] * 51
        for index, value in {17: "11", 27: "3", 28: "7", 29: "15", 30: "30", 36: "8", 52: "9"}.items():
            values[index] = value
        content = "订单库存总表\n|" + "|".join(headers) + "|\n|" + "|".join(["---"] * len(headers)) + "|\n|" + "|".join(values) + "|\n"

        row = parse_ordinary_sheet_markdown(content, "订单库存总表")[0]

        self.assertEqual({column: row[column] for column in ["R", "AB", "AC", "AD", "AE", "AK", "BA"]}, {
            "R": "11", "AB": "3", "AC": "7", "AD": "15", "AE": "30", "AK": "8", "BA": "9",
        })

    def test_builds_grid_data_for_one_contiguous_column_range(self):
        rows = [{"row_index": 5, "SKU": "A"}, {"row_index": 6, "SKU": "B"}]
        updates = {"A": {"R": 3, "S": ""}, "B": {"R": 4, "S": 2}}

        grid = build_sheet_grid_data(rows, updates, ["R", "S"])

        self.assertEqual(grid["start_row"], 5)
        self.assertEqual(grid["start_column"], 17)
        self.assertEqual(grid["rows"][0]["values"], [
            {"cell_value": {"number": 3}, "data_type": "NUMBER"},
            {"cell_value": {"text": ""}, "data_type": "TEXT"},
        ])
        self.assertEqual(grid["rows"][1]["values"][1]["cell_value"]["number"], 2)

    def test_grid_data_rejects_mismatched_sku_sets(self):
        with self.assertRaisesRegex(ValueError, "SKU"):
            build_sheet_grid_data([{"row_index": 6, "SKU": "A"}], {"B": {"R": 1}}, ["R"])

    def test_reads_ordinary_sheet_rows_after_async_export(self):
        owner = self
        class FakeClient:
            def call_tool(self, name, arguments):
                if name == "sheet_get_info":
                    return owner.mcp_result({"errcode": 0, "sheets": [{"sheet_id": "s1", "title": "订单库存总表"}]})
                if "task_id" not in arguments:
                    return owner.mcp_result({"errcode": 0, "task_id": "t1", "task_done": False})
                return owner.mcp_result({"errcode": 0, "task_done": True, "content": "订单库存总表\n|产品|SKU|\n|---|---|\n|产品A|A|\n"})

        sheet_id, rows = read_ordinary_sheet_rows(FakeClient(), "https://doc.weixin.qq.com/sheet/x", poll_interval=0)
        self.assertEqual((sheet_id, rows[0]["SKU"], rows[0]["row_index"]), ("s1", "A", 3))

    def test_writes_only_the_three_allowed_ranges(self):
        owner = self
        class FakeClient:
            def __init__(self): self.calls = []
            def call_tool(self, name, arguments):
                self.calls.append((name, arguments))
                return owner.mcp_result({"errcode": 0, "errmsg": "ok"})
        client = FakeClient()
        columns = ["R", "S", "T", "U", "V", "W", "X", "Y", "AB", "AC", "AD", "AE", "AK", "AL", "AM", "AN", "AO", "AP", "AQ", "AR", "AS", "AT", "AU", "AV", "AW", "AX", "AY"]
        result = write_ordinary_sheet_updates(client, "https://doc.weixin.qq.com/sheet/x", "s1", [{"row_index": 6, "SKU": "A"}], {"A": {column: 1 for column in columns}})

        self.assertEqual(result["updated_rows"], 1)
        calls = [arguments for name, arguments in client.calls if name == "sheet_update_range_data"]
        self.assertEqual([call["grid_data"]["start_column"] for call in calls], [17, 27, 36])

    def test_fixed_layout_writeback_uses_three_ranges_without_reading_online_rows(self):
        owner = self
        class FakeClient:
            def __init__(self): self.calls = []
            def call_tool(self, name, arguments):
                self.calls.append((name, arguments))
                return owner.mcp_result({"errcode": 0, "errmsg": "ok"})

        client = FakeClient()
        result = write_fixed_layout_updates(
            client,
            "https://doc.weixin.qq.com/sheet/x",
            "s1",
            [{"row_index": 5, "SKU": "A"}, {"row_index": 6, "SKU": "B"}],
            {
                "A": {"R": 1, "AB": 2, "AK": 3},
                "B": {"R": 4, "AB": 5, "AK": 6},
            },
        )

        calls = [arguments["grid_data"] for name, arguments in client.calls if name == "sheet_update_range_data"]
        self.assertEqual(result, {"updated_rows": 2, "ranges": 3, "written_cells": 60})
        self.assertEqual([(call["start_row"], call["start_column"], len(call["rows"])) for call in calls], [(5, 17, 2), (5, 27, 2), (5, 36, 2)])
        self.assertEqual(calls[0]["rows"][1]["values"][0]["cell_value"]["number"], 4)

    def test_fixed_layout_writeback_splits_into_fifty_row_requests(self):
        owner = self
        class FakeClient:
            def __init__(self): self.calls = []
            def call_tool(self, name, arguments):
                self.calls.append((name, arguments))
                return owner.mcp_result({"errcode": 0, "errmsg": "ok"})

        layout_rows = [{"row_index": index + 5, "SKU": f"SKU-{index}"} for index in range(1001)]
        updates = {row["SKU"]: {"R": index} for index, row in enumerate(layout_rows)}
        client = FakeClient()
        write_fixed_layout_updates(client, "https://doc.weixin.qq.com/sheet/x", "s1", layout_rows, updates)

        calls = [arguments["grid_data"] for name, arguments in client.calls if name == "sheet_update_range_data"]
        self.assertEqual(len(calls), 63)
        self.assertTrue(all(len(call["rows"]) <= 50 for call in calls))

    def test_fixed_layout_verification_rejects_shifted_or_wrong_values(self):
        layout = [{"row_index": 5, "SKU": "A"}, {"row_index": 6, "SKU": "B"}]
        updates = {"A": {"R": 1}, "B": {"R": 2}}
        with self.assertRaisesRegex(RuntimeError, "A:R"):
            verify_fixed_layout_updates(
                [{"row_index": 5, "SKU": "A", "R": 9}, {"row_index": 6, "SKU": "B", "R": 2}],
                layout,
                updates,
            )

    def test_fixed_layout_verification_accepts_matching_rows(self):
        result = verify_fixed_layout_updates(
            [{"row_index": 5, "SKU": "A", "R": "1"}, {"row_index": 6, "SKU": "B", "R": 2}],
            [{"row_index": 5, "SKU": "A"}, {"row_index": 6, "SKU": "B"}],
            {"A": {"R": 1}, "B": {"R": 2}},
        )
        self.assertEqual(result, {"verified_rows": 2})

    def test_keyed_verification_ignores_different_online_row_order(self):
        result = verify_online_updates_by_sku(
            [
                {"row_index": 6, "SKU": "B", "R": 2},
                {"row_index": 7, "SKU": "A", "R": "1"},
            ],
            {"A": {"R": 1}, "B": {"R": 2}},
        )
        self.assertEqual(result, {"verified_rows": 2})

    def test_writeback_does_not_overwrite_total_stock_formula(self):
        owner = self
        class FakeClient:
            def __init__(self): self.calls = []
            def call_tool(self, name, arguments):
                self.calls.append((name, arguments))
                return owner.mcp_result({"errcode": 0, "errmsg": "ok"})

        client = FakeClient()
        write_ordinary_sheet_updates(
            client,
            "https://doc.weixin.qq.com/sheet/x",
            "s1",
            [{"row_index": 6, "SKU": "A", "Z": 999}],
            {"A": {"R": 1, "Z": 2}},
        )

        written_columns = {
            call["grid_data"]["start_column"] + offset
            for name, call in client.calls if name == "sheet_update_range_data"
            for offset in range(len(call["grid_data"]["rows"][0]["values"]))
        }
        self.assertNotIn(25, written_columns)

    def test_writes_only_changed_cells(self):
        owner = self
        class FakeClient:
            def __init__(self): self.calls = []
            def call_tool(self, name, arguments):
                self.calls.append((name, arguments))
                return owner.mcp_result({"errcode": 0, "errmsg": "ok"})

        client = FakeClient()
        write_ordinary_sheet_updates(
            client,
            "https://doc.weixin.qq.com/sheet/x",
            "s1",
            [{"row_index": 6, "SKU": "A", "S": "old"}, {"row_index": 7, "SKU": "B", "R": "keep"}],
            {"A": {"R": 1}, "B": {"S": 2}},
        )
        calls = [arguments["grid_data"] for name, arguments in client.calls if name == "sheet_update_range_data"]
        self.assertEqual([(call["start_row"], call["start_column"], len(call["rows"])) for call in calls], [(6, 17, 2)])
        self.assertEqual(calls[0]["rows"][0]["values"][1]["cell_value"]["text"], "old")
        self.assertEqual(calls[0]["rows"][1]["values"][0]["cell_value"]["text"], "keep")

    def test_numeric_text_in_numeric_writeback_ranges_stays_numeric(self):
        owner = self
        class FakeClient:
            def __init__(self): self.calls = []
            def call_tool(self, name, arguments):
                self.calls.append((name, arguments))
                return owner.mcp_result({"errcode": 0, "errmsg": "ok"})

        client = FakeClient()
        write_ordinary_sheet_updates(
            client,
            "https://doc.weixin.qq.com/sheet/x",
            "s1",
            [{"row_index": 6, "SKU": "A", "S": "8"}],
            {"A": {"R": 1}},
        )

        q_cell = client.calls[0][1]["grid_data"]["rows"][0]["values"][1]
        self.assertEqual(q_cell, {"cell_value": {"number": 8}, "data_type": "NUMBER"})

    def test_bulk_write_keeps_rows_with_requested_updates_even_when_online_value_matches(self):
        owner = self
        class FakeClient:
            def __init__(self): self.calls = []
            def call_tool(self, name, arguments):
                self.calls.append((name, arguments))
                return owner.mcp_result({"errcode": 0, "errmsg": "ok"})

        client = FakeClient()
        result = write_ordinary_sheet_updates(
            client,
            "https://doc.weixin.qq.com/sheet/x",
            "s1",
            [{"row_index": 6, "SKU": "A", "R": "1"}],
            {"A": {"R": 1}},
        )

        self.assertEqual(result["ranges"], 3)
        self.assertEqual(len(client.calls), 1)

    def test_filters_updates_against_online_values_before_writing(self):
        changed = filter_online_changed_updates(
            [
                {"row_index": 6, "SKU": "A", "R": "1", "S": "旧值"},
                {"row_index": 7, "SKU": "B", "R": 0},
            ],
            {"A": {"R": 1, "S": "新值", "Z": 999}, "B": {"R": 0}},
        )
        self.assertEqual(changed, {"A": {"S": "新值"}})

    def test_splits_non_contiguous_changed_rows_without_filling_gaps(self):
        owner = self
        class FakeClient:
            def __init__(self): self.calls = []
            def call_tool(self, name, arguments):
                self.calls.append((name, arguments))
                return owner.mcp_result({"errcode": 0, "errmsg": "ok"})

        client = FakeClient()
        write_ordinary_sheet_updates(
            client,
            "https://doc.weixin.qq.com/sheet/x",
            "s1",
            [{"row_index": 6, "SKU": "A"}, {"row_index": 7, "SKU": "B"}, {"row_index": 8, "SKU": "C"}],
            {"A": {"R": 1}, "C": {"R": 3}},
        )
        calls = [arguments["grid_data"] for name, arguments in client.calls if name == "sheet_update_range_data"]
        self.assertEqual([(call["start_row"], len(call["rows"])) for call in calls], [(6, 1), (8, 1)])

    def test_column_only_write_splits_non_contiguous_rows_and_never_touches_other_columns(self):
        owner = self

        class FakeClient:
            def __init__(self): self.calls = []
            def call_tool(self, name, arguments):
                self.calls.append((name, arguments))
                return owner.mcp_result({"errcode": 0, "errmsg": "ok"})

        client = FakeClient()
        result = write_ordinary_sheet_column_updates(
            client,
            "https://doc.weixin.qq.com/sheet/x",
            "s1",
            [{"row_index": 6, "SKU": "A"}, {"row_index": 8, "SKU": "C"}],
            {"A": {"R": 1, "S": 2, "T": 3, "U": 4, "V": 5}, "C": {"R": 6, "S": 7, "T": 8, "U": 9, "V": 10}},
            ["R", "S", "T", "U", "V"],
        )

        calls = [arguments["grid_data"] for name, arguments in client.calls if name == "sheet_update_range_data"]
        self.assertEqual(result, {"updated_rows": 2, "written_cells": 10, "requests": 2})
        self.assertEqual([(call["start_row"], call["start_column"], len(call["rows"])) for call in calls], [(6, 17, 1), (8, 17, 1)])

    def test_splits_ordinary_sheet_writeback_requests_at_1000_rows(self):
        owner = self

        class FakeClient:
            def __init__(self): self.calls = []
            def call_tool(self, name, arguments):
                self.calls.append((name, arguments))
                return owner.mcp_result({"errcode": 0, "errmsg": "ok"})

        rows = [{"row_index": 6 + index, "SKU": f"SKU-{index}"} for index in range(1082)]
        columns = ["R", "S", "T", "U", "V", "W", "X", "Y", "AB", "AC", "AD", "AE", "AK", "AL", "AM", "AN", "AO", "AP", "AQ", "AR", "AS", "AT", "AU", "AV", "AW", "AX", "AY"]
        updates = {row["SKU"]: {column: 1 for column in columns} for row in rows}
        client = FakeClient()

        write_ordinary_sheet_updates(client, "https://doc.weixin.qq.com/sheet/x", "s1", rows, updates)

        calls = [arguments["grid_data"] for name, arguments in client.calls if name == "sheet_update_range_data"]
        columns = list(range(15, 23)) + list(range(25, 29)) + list(range(34, 49))
        self.assertEqual(len(calls), 6)
        self.assertEqual([len(call["rows"]) for call in calls], [1000, 82] * 3)
        self.assertEqual([call["start_row"] for call in calls], [6, 1006] * 3)
        self.assertEqual([call["start_column"] for call in calls], [17, 17, 27, 27, 36, 36])

    def test_decodes_jsonrpc_result_from_event_stream(self):
        raw = 'event: message\ndata: {"jsonrpc":"2.0","id":3,"result":{"ok":true}}\n\n'
        self.assertEqual(decode_mcp_response(raw), {"ok": True})

    def test_builds_record_updates_by_sku_and_skips_missing_sku(self):
        batches, skipped = build_record_batches(
            records=[
                {"record_id": "r1", "values": {"SKU": [{"text": "SKU-1"}]}},
                {"record_id": "r2", "values": {"SKU": [{"text": "SKU-2"}]}},
            ],
            updates_by_sku={
                "SKU-1": {"3天": 4, "第三方可用库存": 10, "不应写入": 99},
                "SKU-3": {"3天": 2},
            },
            writable_fields={"3天", "第三方可用库存"},
        )

        self.assertEqual(
            batches,
            [[{"record_id": "r1", "values": {"3天": 4, "第三方可用库存": 10}}]],
        )
        self.assertEqual(skipped, [{"sku": "SKU-3", "reason": "not_found_in_smartsheet"}])

    def test_splits_writeback_into_500_record_batches(self):
        records = [{"record_id": f"r{index}", "values": {"SKU": f"SKU-{index}"}} for index in range(501)]
        updates = {f"SKU-{index}": {"3天": index} for index in range(501)}

        batches, skipped = build_record_batches(records, updates, {"3天"})

        self.assertEqual([len(batch) for batch in batches], [500, 1])
        self.assertEqual(batches[0][0], {"record_id": "r0", "values": {"3天": 0}})
        self.assertEqual(skipped, [])


if __name__ == "__main__":
    unittest.main()
