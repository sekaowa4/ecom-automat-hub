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
    dialogs = [
        item
        for item in driver.find_elements(By.CSS_SELECTOR, '[role="dialog"], dialog')
        if item.is_displayed()
    ]
    if len(dialogs) != 1:
        raise RuntimeError(f"Expected one visible dialog, found {len(dialogs)}")

    rows = driver.execute_script(
        r"""
        const dialog = arguments[0];
        return Array.from(dialog.querySelectorAll('*')).map((element) => {
          const direct = Array.from(element.childNodes)
            .filter((node) => node.nodeType === Node.TEXT_NODE)
            .map((node) => node.textContent.trim())
            .filter(Boolean)
            .join(' ');
          return {
            tag: element.tagName.toLowerCase(),
            className: typeof element.className === 'string' ? element.className : '',
            id: element.id || '',
            directText: direct,
            ariaLabel: element.getAttribute('aria-label') || '',
            dataTestId: element.getAttribute('data-testid') || ''
          };
        }).filter((row) => row.directText || row.ariaLabel || row.dataTestId);
        """,
        dialogs[0],
    )
    for row in rows:
        row["directText"] = re.sub(r"\b[A-Z0-9]{8}\b", "[CODE REDACTED]", row["directText"])
    print(json.dumps(rows, ensure_ascii=False))
finally:
    driver.service.stop()
