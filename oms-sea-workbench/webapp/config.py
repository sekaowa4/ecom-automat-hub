"""Private workstation settings are never shipped with the public source."""
import json
import re
from pathlib import Path

CONFIG_PATH = Path(__file__).resolve().parent.parent / 'local-config.json'


def load_config(path=CONFIG_PATH):
    return json.loads(Path(path).read_text(encoding='utf-8')) if Path(path).exists() else {}


def billing_config(config=None):
    billing = (load_config() if config is None else config).get('billing', {})
    if not all(isinstance(billing.get(key), str) and billing[key].strip() for key in ('shop', 'name', 'cnpj', 'company')) or len(re.sub(r'\D', '', billing.get('cnpj', ''))) != 14:
        raise ValueError('请先在本机 local-config.json 填写并核对开票收件资料')
    return billing
