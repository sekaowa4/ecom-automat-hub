# Unmatched Items Preview Copy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Generate a downloadable Stock_Order preview copy while preserving every unmatched product row unchanged.

**Architecture:** The pipeline already excludes unmatched products from `target_skus`, so the workbook patcher naturally leaves their data cells untouched. The API will expose a separate read-only preview-copy endpoint that runs the same pipeline, saves only a timestamped runtime copy, and returns its URL plus the existing warning report. Direct confirmation remains blocked while unmatched warnings exist.

**Tech Stack:** Python standard-library OOXML processing, `unittest`, local `http.server` API.

## Global Constraints

- Never overwrite `D:\桌面\Stock_Order.xlsx`.
- Unmatched and ambiguous product rows retain their original cell values and formulas.
- The preview copy is downloadable from `runtime/` only.
- Enterprise WeCom writeback remains out of scope.

---

### Task 1: Add a regression test for the preview-copy endpoint logic

**Files:**
- Modify: `tests/test_server.py`
- Modify: `server.py`

**Interfaces:**
- Consumes: `server.run_local_inventory_preview_copy()`.
- Produces: `{ok: bool, preview: dict, report: dict, workbook_url: str}`.

- [ ] **Step 1: Write the failing test**

```python
def test_preview_copy_saves_a_downloadable_workbook(self):
    with patch.object(server, "process_files", return_value=(b"workbook", {"errors": [], "warnings": [], "summary": {}})):
        with patch.object(server, "portal_source_manifest", return_value=valid_files):
            result = server.run_local_inventory_preview_copy()
    self.assertTrue(result["ok"])
    self.assertTrue(result["workbook_url"].startswith("/downloads/preview_stock_order_"))
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m unittest tests.test_server.PortalPreviewOutputTests.test_preview_copy_saves_a_downloadable_workbook -v`

Expected: FAIL because `run_local_inventory_preview_copy` does not exist.

- [ ] **Step 3: Write minimal implementation**

```python
def run_local_inventory_preview_copy() -> dict:
    files = portal_source_manifest() if _portal_export_files_ready() else local_source_manifest()
    files.pop("inventory", None)
    workbook, report = process_files(files)
    if workbook is None:
        return {"ok": False, "preview": build_inventory_preview(report), "report": report}
    name = f"preview_stock_order_{uuid.uuid4().hex[:8]}.xlsx"
    (RUNTIME / name).write_bytes(workbook)
    return {"ok": True, "preview": build_inventory_preview(report), "report": report, "workbook_url": f"/downloads/{name}"}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m unittest tests.test_server.PortalPreviewOutputTests.test_preview_copy_saves_a_downloadable_workbook -v`

Expected: PASS.

### Task 2: Add an HTTP route and exercise the real export files

**Files:**
- Modify: `server.py`
- Modify: `tests/test_server.py`

**Interfaces:**
- Consumes: `POST /api/inventory-preview-copy` with an empty JSON body.
- Produces: the Task 1 response payload.

- [ ] **Step 1: Write the failing test**

```python
def test_preview_copy_keeps_confirmation_blocked_for_unmatched_products(self):
    preview = build_inventory_preview({"errors": [], "warnings": [{"type": "unmatched_product"}], "summary": {}})
    self.assertFalse(preview["canConfirm"])
```

- [ ] **Step 2: Run test to verify it passes as a characterization**

Run: `python -m unittest tests.test_server.PortalPreviewOutputTests.test_preview_copy_keeps_confirmation_blocked_for_unmatched_products -v`

Expected: PASS; the existing preview policy is intentionally retained.

- [ ] **Step 3: Add route**

```python
if self.path == "/api/inventory-preview-copy":
    self._send_json(run_local_inventory_preview_copy())
    return
```

- [ ] **Step 4: Run focused and full test suites**

Run: `python -m unittest discover -s tests -v`

Expected: all Python tests pass.

- [ ] **Step 5: Run the route using the seven portal exports**

Run: `Invoke-WebRequest -UseBasicParsing -Method Post -ContentType 'application/json' -Body '{}' http://127.0.0.1:8765/api/inventory-preview-copy`

Expected: `ok: true`, a downloadable preview-copy URL, 21 warnings, and `canConfirm: false`.
