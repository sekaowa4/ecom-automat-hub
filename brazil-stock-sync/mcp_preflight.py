from __future__ import annotations

import sys

from daily_update import run_mcp_authorization_preflight


if __name__ == "__main__":
    try:
        run_mcp_authorization_preflight()
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        sys.exit(1)
    sys.exit(0)
