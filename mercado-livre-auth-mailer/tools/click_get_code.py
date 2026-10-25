import hashlib
import json
import re
import sys
import time
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
    buttons = [
        element
        for element in driver.find_elements(By.TAG_NAME, "button")
        if element.is_displayed() and element.text.strip() == "Obter código"
    ]
    if len(buttons) != 1:
        raise RuntimeError(f"Expected one visible Obter código button, found {len(buttons)}")

    before = driver.find_element(By.TAG_NAME, "body").text
    buttons[0].click()
    time.sleep(3)
    after = driver.find_element(By.TAG_NAME, "body").text

    candidates = sorted(set(re.findall(r"\b[A-Z0-9]{8}\b", after)))
    masked = [
        {
            "mask": f"{value[:2]}****{value[-2:]}",
            "sha256_prefix": hashlib.sha256(value.encode()).hexdigest()[:12],
        }
        for value in candidates
    ]
    dialogs = []
    for element in driver.find_elements(By.CSS_SELECTOR, '[role="dialog"], dialog'):
        if element.is_displayed():
            text = re.sub(r"\b[A-Z0-9]{8}\b", "[CODE REDACTED]", element.text.strip())
            dialogs.append(text[:800])

    print(
        json.dumps(
            {
                "clicked": True,
                "url": driver.current_url,
                "page_changed": before != after,
                "candidate_count": len(candidates),
                "candidates": masked,
                "dialogs": dialogs,
            },
            ensure_ascii=False,
        )
    )
finally:
    driver.service.stop()
