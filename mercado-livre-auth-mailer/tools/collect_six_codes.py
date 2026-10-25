"""Backward-compatible entry point for the official ZiNiao agent collector."""

from .collect_agent_six_codes import main


if __name__ == "__main__":
    raise SystemExit(main())
