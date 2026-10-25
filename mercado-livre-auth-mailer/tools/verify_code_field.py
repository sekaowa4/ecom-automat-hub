import hashlib
import json
import re
import sys
from pathlib import Path

from selenium import webdriver
from selenium.webdriver.chrome.service import Service
from selenium.webdriver.common.by import By


sys.stdout.reconfigure(encoding="utf-8")
options = webdriver.ChromeOptions()
options.add_experimental_option("debuggerAddress", "127.0.0.1:13641")
driver = webdriver.Chrome(
    service=Service(str(Path("runtime/chromedriver/chromedriver146.exe"))),
    options=options,
)

try:
    value = driver.find_element(By.CSS_SELECTOR, ".sc-settings-copy-textfield__text").text.strip()
    helper = driver.find_element(By.CSS_SELECTOR, ".sc-settings-copy-textfield__helper-text").text.strip()
    date_match = re.search(r"(\d{2}/\d{2}/\d{4})", helper)
    result = {
        "valid_format": bool(re.fullmatch(r"[A-Z0-9]{8}", value)),
        "length": len(value),
        "mask": f"{value[:2]}****{value[-2:]}" if len(value) >= 4 else "[INVALID]",
        "sha256_prefix": hashlib.sha256(value.encode()).hexdigest()[:12],
        "valid_date": date_match.group(1) if date_match else None,
    }
    print(json.dumps(result, ensure_ascii=False))
finally:
    driver.service.stop()
