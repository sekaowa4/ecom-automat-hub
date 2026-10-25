from __future__ import annotations

import json
from pathlib import Path

from .core import DuplicateBatchError


class BatchLedger:
    def __init__(self, path: str | Path):
        self.path = Path(path)

    def _read(self) -> dict:
        if not self.path.exists():
            return {"batches": {}}
        return json.loads(self.path.read_text(encoding="utf-8"))

    def ensure_not_sent(self, business_date: str, fingerprint: str) -> None:
        key = f"{business_date}:{fingerprint}"
        record = self._read().get("batches", {}).get(key)
        if record and record.get("status") in {
            "sent_verified",
            "sent_verified_after_smtp_error",
        }:
            raise DuplicateBatchError(f"Batch already recorded for {business_date}")

    def mark_sent(
        self,
        business_date: str,
        fingerprint: str,
        message_id: str,
        status: str = "sent_verified",
    ) -> None:
        data = self._read()
        data.setdefault("batches", {})[f"{business_date}:{fingerprint}"] = {
            "business_date": business_date,
            "fingerprint": fingerprint,
            "message_id": message_id,
            "status": status,
        }
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temporary = self.path.with_suffix(self.path.suffix + ".tmp")
        temporary.write_text(json.dumps(data, indent=2), encoding="utf-8")
        temporary.replace(self.path)
