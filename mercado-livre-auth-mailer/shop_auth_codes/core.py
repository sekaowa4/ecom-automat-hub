from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from datetime import datetime, time


class BeforeCutoffError(ValueError):
    """Raised when a run starts before the daily code refresh time."""


class DuplicateBatchError(RuntimeError):
    """Raised when a batch was already sent."""


@dataclass(frozen=True)
class StoreCode:
    store_name: str
    code: str


def validate_cutoff(now: datetime, cutoff: str) -> None:
    try:
        cutoff_time = datetime.strptime(cutoff, "%H:%M").time()
    except ValueError as exc:
        raise ValueError(f"Invalid cutoff time: {cutoff}") from exc
    if now.time() < cutoff_time:
        raise BeforeCutoffError(f"Run blocked before daily cutoff {cutoff}")


def validate_batch(
    expected_store_names: list[str],
    collected: list[StoreCode],
    code_regex: str,
) -> list[StoreCode]:
    if len(expected_store_names) != len(set(expected_store_names)):
        raise ValueError("Configured store names must be unique")

    pattern = re.compile(code_regex)
    by_store: dict[str, StoreCode] = {}
    for item in collected:
        if item.store_name not in expected_store_names:
            raise ValueError(f"Unexpected store: {item.store_name}")
        if item.store_name in by_store:
            raise ValueError(f"Duplicate store: {item.store_name}")
        code = item.code.strip()
        if not code or pattern.fullmatch(code) is None:
            raise ValueError(f"Invalid authorization code for {item.store_name}")
        by_store[item.store_name] = StoreCode(item.store_name, code)

    missing = [name for name in expected_store_names if name not in by_store]
    if missing:
        raise ValueError(f"Missing stores: {', '.join(missing)}")

    codes = [by_store[name] for name in expected_store_names]
    if len({item.code for item in codes}) != len(codes):
        raise ValueError("Authorization codes must be unique within a batch")
    return codes


def render_message(template: str, date_text: str, codes: list[StoreCode]) -> str:
    if template.count("{{date}}") != 1 or template.count("{{store_lines}}") != 1:
        raise ValueError("Template must contain exactly one {{date}} and {{store_lines}}")
    lines = "\n".join(f"{item.store_name}: {item.code}" for item in codes)
    return template.replace("{{date}}", date_text).replace("{{store_lines}}", lines)


def build_batch_fingerprint(date_text: str, codes: list[StoreCode]) -> str:
    payload = {"date": date_text, "codes": [(item.store_name, item.code) for item in codes]}
    encoded = json.dumps(payload, ensure_ascii=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def mask_code(code: str) -> str:
    if len(code) <= 4:
        return "*" * len(code)
    return f"{code[:4]}{'*' * (len(code) - 4)}"


def format_business_date(now: datetime, date_format: str) -> str:
    if date_format == "YYYY/M/D":
        return f"{now.year}/{now.month}/{now.day}"
    return now.strftime(date_format)
