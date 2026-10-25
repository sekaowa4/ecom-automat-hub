# 库存自动化工作台预览界面 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Upgrade the existing local web page into a clickable HTML prototype for automatic data fetching, preview review, and confirmed writeback.

**Architecture:** Keep the existing plain HTML/CSS/JavaScript app. Add a focused workbench layout and local state machine in `web/app.js`; keep simulated data isolated from future API replacement points. No real OMS or WeCom calls and no real writeback in this prototype.

**Tech Stack:** Existing HTML, CSS, vanilla JavaScript, Python static server.

## Global Constraints

- The main flow must not ask the user to manually upload files.
- The primary action is automatic fetch, followed by preview and explicit confirmation.
- The prototype must never write to OMS or WeCom.
- Use Chinese labels and Beijing-time status copy.
- Preserve responsive behavior for desktop and narrow screens.

---

### Task 1: Build the workbench shell and visual hierarchy

**Files:**
- Modify: `web/index.html`
- Modify: `web/styles.css`

**Interfaces:**
- Produces DOM targets used by `web/app.js`: `#startRun`, `#previewPanel`, `#runProgress`, `#previewTable`, `#confirmWriteback`, `#cancelPreview`, `#runLog`.

- [ ] Replace the upload-first layout with a sidebar, top status bar, overview section, data-source status rows, and preview section.
- [ ] Add accessible labels, button states, status classes, and responsive layout rules.
- [ ] Keep backup/log actions visible but secondary.

### Task 2: Add the simulated fetch-preview-confirm interaction

**Files:**
- Modify: `web/app.js`

**Interfaces:**
- `startRun()` changes local state from `idle` to `fetching` and then to `preview`.
- `loadPreview()` renders `previewRows` into `#previewTable`.
- `confirmWriteback()` changes the local state to `written` and shows a success result.

- [ ] Implement sample source statuses for OMS库存、企业微信表格、销售数据 and a progress indicator.
- [ ] Implement preview rows containing SKU, current stock, days of supply, status, and recommendation.
- [ ] Implement cancel, retry, backup, and log interactions without external writes.
- [ ] Add the future API seam comments around `startRun`, `loadPreview`, and `confirmWriteback`.

### Task 3: Run browser-facing verification

**Files:**
- Test: `web/index.html` through the existing local server.

- [ ] Start the existing Python server.
- [ ] Verify the initial overview, automatic fetch animation, preview table, cancel path, and confirm path.
- [ ] Verify a narrow viewport does not create horizontal overflow.
- [ ] Capture a screenshot for visual review and fix any obvious clipping or unreadable controls.

