# 企业微信智能表格写回 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add confirmed, SKU-based smart-sheet writeback to the local workbench.

**Architecture:** Keep WeCom protocol calls separate from inventory calculations. `wecom_sync.py` receives a read/write client and produces safe batches from smart-sheet records and named update fields; `server.py` later provides routes and configuration. UI confirmation calls the writeback route only after preview.

**Tech Stack:** Python standard library, unittest, local HTML/JavaScript.

## Global Constraints

- Do not overwrite a local workbook or write WeCom data during preview.
- Skip unmatched SKU and batch writes at 500 records or fewer.
- Store the MCP address only in a locally ignored configuration file.
- Do not log the MCP address or API key.

---

### Task 1: Create pure SKU-to-record batching logic

**Files:**
- Create: `wecom_sync.py`
- Modify: `tests/test_wecom_sync.py`

**Interfaces:**
- Produces: `build_record_batches(records, updates_by_sku, fields, batch_size=500) -> (batches, skipped)`.

- [ ] Write failing tests for record-id conversion, skip handling and 500-row chunking.
- [ ] Implement the pure function with no HTTP calls.
- [ ] Run `python -m unittest tests.test_wecom_sync -v`.

### Task 2: Add local configuration and read-only connection check

**Files:**
- Modify: `server.py`
- Modify: `tests/test_server.py`

**Interfaces:**
- Produces: `GET /api/wecom-status` and `POST /api/wecom-config`.

- [ ] Write failing tests proving the status response redacts the secret endpoint.
- [ ] Save only the local configuration file and return a masked endpoint hint.
- [ ] Run the full Python suite.

### Task 3: Replace simulated UI confirmation with an actual API call

**Files:**
- Modify: `web/app.js`
- Modify: `web/index.html`

**Interfaces:**
- Consumes: `POST /api/wecom-writeback`.
- Produces: writeback count, skipped count and errors on screen.

- [ ] Make confirmation unavailable until a WeCom connection is configured.
- [ ] Render returned writeback outcomes instead of a simulated alert.
- [ ] Verify the front-end syntax and server tests.
