import json
import sys
from pathlib import Path

from selenium import webdriver
from selenium.webdriver.chrome.service import Service
from selenium.webdriver.common.by import By
from selenium.webdriver.support.ui import WebDriverWait


sys.stdout.reconfigure(encoding="utf-8")
options = webdriver.ChromeOptions()
options.add_experimental_option("debuggerAddress", "127.0.0.1:13641")
driver = webdriver.Chrome(
    service=Service(str(Path("runtime/chromedriver/chromedriver146.exe"))),
    options=options,
)

try:
    dialogs = [
        element
        for element in driver.find_elements(By.CSS_SELECTOR, '[role="dialog"], dialog')
        if element.is_displayed() and "Código de autorização" in element.text
    ]
    if len(dialogs) != 1:
        raise RuntimeError(f"Expected one visible authorization dialog, found {len(dialogs)}")
    dialog = dialogs[0]
    dialog.find_element(By.CSS_SELECTOR, 'button[aria-label="Fechar"]').click()
    WebDriverWait(driver, 10).until(
        lambda current: not any(
            element.is_displayed()
            for element in current.find_elements(By.CSS_SELECTOR, '[role="dialog"], dialog')
        )
    )
    print(json.dumps({"closed": True, "url": driver.current_url}, ensure_ascii=False))
finally:
    driver.service.stop()
