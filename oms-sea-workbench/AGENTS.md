# Project instructions

Think before coding: state assumptions and ask when unclear. Use the smallest complete solution. Make surgical changes, preserve unrelated work, verify observable results.

## Local business settings

Read README.md and, when present, AGENTS.local.md for the established workflow. Local instructions supplement these public rules; do not copy customer identifiers into tracked files. Real recipient, shop, OMS origin and special image-sharing mappings belong only in ignored local-config.json. Preserve local historical records and business files.

## Browser and workflow safety

Reuse the dedicated Chrome debugging channel (chrome-devtools-mcp --autoConnect) and existing tabs. Inspect read-only before reconnecting. Do not default to extensions, loop authorization requests, or copy/export cookies.

The workbench runs via 启动海运工作台.cmd at http://127.0.0.1:8765. Maintain browser connections across start/pause/resume. Inbound submission, shipping, invoicing and sending attachments require current per-action confirmation; stale confirmations cannot be reused after restart.

Keep duplicate protection in .workbench/ and ignored webapp/completed_batches.json. Never recreate historical orders for testing. Unknown saves require reconciliation first.

Quantities come from the manifest's total-pieces header (including bilingual headers); never assume a fixed column or use boxes/per-box units. Prices come only from the corresponding batch sub-sheet of the online 正清表: match SKU and read E=单价, not manifest RMB prices. Bind one sheet per batch; only auto-select when the entire SKU set matches exactly and uniquely. Otherwise require explicit selection. Preserve price sheet/column provenance and recompute exact-cent totals. Missing/conflicting prices block execution. Private filename-to-sheet bindings belong in local-config.json batch_price_sheets, never public code.

Prefer product images from read-only WeCom 图包作图需求 (B=SKU, N=产品图, first image). If a SKU has no row in that image sheet, use its embedded manifest picture only when the workbook's image anchor is in the configured image column and on that SKU's row; do not guess from visual overlap. A conflicting row or a row with no valid image remains an issue, with no fallback. Dimensions/weight always come from 箱规汇总表 (C=SKU, AA:AC=cm, AD=kg converted to g); never use manifest dimensions/weight. Never modify source manifests or WeCom sheets. Preserve image-source provenance and warn when using a manifest picture. Missing/conflicting sizes block start; missing usable images block creation of new products, not replenishment. Use the workstation's existing Codex MCP config without copying credentials. Only read tools are permitted. Do not rematch completed or already-executed batches.

OMS changes D:G only; preserve Portuguese names and all other fields. Manual orders use 添加商品, not 导入SKU. Tax NCM comes from common-account product export AG; origin code 2. Verify saved values before invoicing. Invoice PDF and XML must match the target order. Upload unmodified XML inside ZIP; compress only images in oversized XLSX copies, never table data.

Keep Windows launchers CRLF and test with cmd.exe. Local testing does not establish live platform integration success.

## Mandatory sync after project changes

Remote: https://github.com/sekaowa4/ecom-automat-hub.git (public). Project: oms-sea-workbench.

For future user-requested changes, run relevant tests/build, inspect the diff and staged list for private information, commit task changes and push. Report commit and sync result. Do not force push, overwrite remote work, stage unrelated edits or claim success if push fails. This is the assistant workflow, not a background watcher for manual file edits.

This workstation keeps the running project at its existing path. Its local Git tracks project sources only; the shared remote is a multi-project repository. Publish reviewed tracked files to the oms-sea-workbench/ subdirectory in the ignored .work/github-hub checkout, commit there and push its main branch. Fetch/pull fast-forward there first and reconcile any remote project changes before copying; never push this standalone source tree directly over the hub root. Verify file hashes and the remote commit afterward. Keep other projects unchanged.

CLI Git push may lack credentials although the GitHub connector is authenticated. In that case use the connected GitHub tools without extracting credentials: create a tree based on the latest remote tree with reviewed project changes, verify its hash against the local hub tree, create a commit with the latest remote parent and update main with expected_sha (no force). Read back the remote head. Fetch afterward; if an identical local-only publish commit remains, rebase onto origin/main so Git can skip the duplicate patch. Do not rebase unrelated user work or retry a changed remote without reviewing it.

Never track local-config.json, *.local.*, completed business records, source manifests, invoice PDF/XML/ZIP, catalogs, browser profiles, cookies, credentials, screenshots, logs or runtime progress. Never blindly git add . . Public examples/tests use fictitious or configurable data.
