# Brazil Stock Sync

This is the active API-based Brazil Stock_Order project (local folder: 副本2).

## GitHub synchronization

- Requested repository: `sekaowa4/ecom-automat-hub`.
- Project directory in that repository: `brazil-stock-sync`.
- After a user-requested project change, test the affected behavior, inspect the exact diff for secrets/business data, then commit and synchronize this project's approved source files to GitHub.
- Fetch and inspect the remote before updating it. Preserve unrelated repository content. Never force-push or overwrite another project's files.
- If synchronization fails, report that the change is local only; never claim GitHub is current without checking the remote commit.
- The user approved public publication of sanitized source on 2026-10-25. Never include local credentials or business data in that approval.
- Keep credentials, runtime files, exports, Stock templates, customer data, screenshots, logs, bundled dependencies, and local task state out of GitHub. Follow `.gitignore` and scan source contents too.
- Synchronization means source-code backup, not cloud deployment or a continuously running cloud service. Do not create a background watcher or run inventory updates just to synchronize source.
- Export through `tools/prepare_github_snapshot.py` before publishing. The sanitized snapshot is under `runtime/github-public/brazil-stock-sync`; its local manifest records source hashes and the published commit. Never publish the working folder directly.

## Inventory safety

- Use the official Stock_Order, not a test copy, unless explicitly requested otherwise.
- Export six fresh UpSeller sources via API; no OMS inventory is needed.
- Check source headers, online column meanings, SKU identity and physical row placement before writing.
- Current user template (2026-10-29) has E=产品类别 and adds Meli Full店铺7库存 at W: write only S:AA, AD:AG, AM:BD. S:X are Meli Full stores 1/2/3/6/7/8; Y/Z/AA are occupied/unavailable/available; AB is total-stock formula; AC is transit; AD:AG are 3/7/15/30-day sales; AH is weighted daily sales. Use the shared pipeline/MCP column maps; stop on any further schema change. Raw UpSeller export columns did not shift.
- Never treat local/online equality alone as proof of source correctness.
- Preserve existing formulas, business formatting, and unrelated sheets. FULL values without a matching SKU/store remain blank, not zero.
- Update A1 to the Beijing-date label `最近更新时间：M.D`, 18-point standard red.
- Read back and verify before reporting completion. Send a group success notification only after successful verification; never send failure notifications.
- Record live occupied/available movements separately from export-snapshot consistency; do not describe snapshot checks as real-time equality.
