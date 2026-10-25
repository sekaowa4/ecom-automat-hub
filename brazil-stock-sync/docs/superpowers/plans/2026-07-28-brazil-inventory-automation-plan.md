# 巴西库存更新自动化 MVP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or superpowers:subagent-driven-development to implement this plan task-by-task.

**Goal:** Build a local browser-operated MVP that transforms the supplied inventory and sales workbooks into an updated `Stock_Order.xlsx` plus a validation report.

**Architecture:** A small Python standard-library HTTP service serves a local HTML interface and processes XLSX files through OOXML. The browser uploads files, the server validates headers, aggregates by SKU/store, patches target cells while preserving the source workbook package, and returns downloadable artifacts.

**Tech Stack:** Python 3 standard library, HTML/CSS/JavaScript, ZIP/XML OOXML processing, unittest.

## Global Constraints

- Do not modify user-provided source files.
- Use Beijing time and include the current day.
- Missing SKU matches remain blank.
- Physical stores contribute to 30-day total only.
- Severe validation failures block final workbook generation.
- No live login or enterprise-WeChat writes in MVP.

### Task 1: Define test fixtures and parser contracts

**Files:**
- Create: `tests/test_inventory_pipeline.py`
- Create: `inventory_pipeline.py`

- [ ] Write failing tests for header detection, SKU aggregation, store normalization, missing-match blanks, and target-column mapping.
- [ ] Run `python -m unittest tests.test_inventory_pipeline -v` and verify failures are caused by missing implementation.

### Task 2: Implement XLSX reader and aggregation engine

**Files:**
- Modify: `inventory_pipeline.py`

- [ ] Implement OOXML workbook reader supporting shared strings, inline strings, numeric values, and formulas without evaluating formulas.
- [ ] Implement source adapters for OMS, FULL, pending invoice, and 3/7/15/30-day sales files.
- [ ] Implement aggregate maps keyed by normalized SKU and optional normalized store.
- [ ] Run the focused tests and verify they pass.

### Task 3: Implement target workbook patching and validation

**Files:**
- Modify: `inventory_pipeline.py`
- Modify: `tests/test_inventory_pipeline.py`

- [ ] Add tests proving target cells P-S, T-V, W, Z-AC, and AI-AS are populated according to the confirmed rules.
- [ ] Add tests proving missing SKU matches are blank and physical stores are excluded from individual store columns.
- [ ] Implement safe OOXML cell replacement while preserving styles, formulas outside updated cells, and all other workbook parts.
- [ ] Implement structured validation output with blocking errors and non-blocking warnings.
- [ ] Run all tests.

### Task 4: Build local web interface and HTTP endpoints

**Files:**
- Create: `server.py`
- Create: `web/index.html`
- Create: `web/app.js`
- Create: `web/styles.css`

- [ ] Add a form for the eight workbook inputs and a run button.
- [ ] Add `/api/process` multipart upload handling with temporary files and safe cleanup.
- [ ] Return result workbook, report JSON, and summary counts.
- [ ] Render progress, blocking errors, warnings, and download links.

### Task 5: End-to-end verification with supplied files

**Files:**
- Create: `README.md`

- [ ] Run the test suite.
- [ ] Process the supplied files from `D:\Mic` and `D:\桌面` without modifying them.
- [ ] Inspect output workbook package for expected updated cells and preserved sheets.
- [ ] Start the local server and verify the page loads and the process endpoint returns artifacts.
