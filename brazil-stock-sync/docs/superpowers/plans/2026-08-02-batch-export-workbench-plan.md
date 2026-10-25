# 一键批量导出与库存预览 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a local batch job that exports the seven portal files in order and feeds the latest exports into the workbench preview.

**Architecture:** The Python server owns job creation, status persistence, and preview source selection. The Node browser runner performs the ordered portal actions and writes named files. The browser UI polls a small JSON status endpoint and renders one row per export step.

**Tech Stack:** Python `http.server`, Node.js, Playwright over Chrome CDP, vanilla browser JavaScript, existing Node/Python test runners.

## Global Constraints

- Reuse the existing logged-in Chrome CDP endpoint at `http://127.0.0.1:9222`.
- Use Beijing time (`Asia/Shanghai`) and exclude the current Beijing day from custom sales ranges.
- Preserve successful files when a later export fails.
- Do not write back to Enterprise WeChat or modify the source Stock_Order document in this task.

---

### Task 1: Define the batch job state and source manifest

**Files:**
- Create: `automation/batch_job.js`
- Modify: `automation/export_plan.js`
- Test: `automation/batch_job.test.js`

**Interfaces:**
- `createBatchState(plan)` returns `{ status, currentIndex, steps }`.
- `markStep(state, index, patch)` returns a new state with the step updated.
- `portalExportManifest(exportDir)` returns the seven named paths in order.

- [ ] Write tests for initial seven-step state, step transitions, and exact manifest order.
- [ ] Run `node --test automation/batch_job.test.js` and verify it fails because the module is absent.
- [ ] Implement the pure state helpers and manifest builder without browser side effects.
- [ ] Run the focused test and then all Node tests.

### Task 2: Add the server-side asynchronous batch job

**Files:**
- Modify: `server.py`
- Test: `tests/test_server.py`

**Interfaces:**
- `POST /api/export-all` returns `{ "ok": true, "job_id": "..." }`.
- `GET /api/export-status?id=...` returns the current state JSON.
- `run_portal_batch(job_id)` launches `automation/batch_export.js` and updates the state.

- [ ] Add failing tests for job creation, unknown job lookup, and preview source selection from `runtime/portal-downloads`.
- [ ] Run the focused Python tests and verify the new routes fail.
- [ ] Implement in-memory job registry plus JSON status files under `runtime/jobs`; run the Node batch process in a background thread.
- [ ] Change the local preview manifest to prefer all seven named files in `runtime/portal-downloads` when present.
- [ ] Run all Python tests and confirm existing upload preview behavior remains unchanged.

### Task 3: Connect the workbench UI to the batch job

**Files:**
- Modify: `web/app.js`
- Modify: `web/index.html`
- Modify: `web/styles.css`

**Interfaces:**
- `startRun()` calls `/api/export-all`, polls `/api/export-status?id=...`, renders seven rows, and only calls `/api/inventory-preview` after success.
- Existing cancel and preview actions remain available.

- [ ] Add the seven named export rows and a status class in the HTML/CSS.
- [ ] Replace the two-source export calls in `startRun()` with job creation and polling.
- [ ] Display the current step, percentage, completed files, and failure reason.
- [ ] Verify the JavaScript syntax and serve the page through the existing local server.

### Task 4: Verify the complete flow

**Files:**
- Modify: `README.md`
- Create: `tests/fixtures/export-status-example.json`

- [ ] Run Node tests and Python tests.
- [ ] Run the local server and call `/api/export-all` with the existing logged-in Chrome.
- [ ] Confirm all seven exact files exist, are readable, and have non-empty headers.
- [ ] Confirm the workbench preview endpoint reports the newly exported rows.
- [ ] Document the exact run command and the expected download directory.
