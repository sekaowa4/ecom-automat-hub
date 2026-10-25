# UpSeller API export replacement

1. Capture the six existing UI export request shapes once, redacting credentials.
2. Add a small HTTP export client that reads `us_u` only from local runtime configuration.
3. Preserve the existing stock generation, MCP writeback and webhook flow.
4. Verify generated workbooks retain the required sales-by-variant headers before replacing the browser path.
