import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

from selenium import webdriver
from selenium.webdriver.chrome.service import Service
from selenium.webdriver.common.by import By
from selenium.webdriver.support.ui import WebDriverWait


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--store", required=True)
    args = parser.parse_args()

    sys.stdout.reconfigure(encoding="utf-8")
    options = webdriver.ChromeOptions()
    options.add_experimental_option("debuggerAddress", f"127.0.0.1:{args.port}")
    driver = webdriver.Chrome(
        service=Service(str(Path("runtime/chromedriver/chromedriver146.exe"))),
        options=options,
    )

    try:
        driver.get("https://vendedores.mercadolivre.com.br/preferencias-de-venda")
        wait = WebDriverWait(driver, 30)
        wait.until(lambda current: "preferencias-de-venda" in current.current_url)

        visible_dialogs = [
            item
            for item in driver.find_elements(By.CSS_SELECTOR, '[role="dialog"], dialog')
            if item.is_displayed() and "Código de autorização" in item.text
        ]
        if not visible_dialogs:
            buttons = [
                item
                for item in driver.find_elements(By.TAG_NAME, "button")
                if item.is_displayed() and item.text.strip() == "Obter código"
            ]
            if len(buttons) != 1:
                raise RuntimeError(f"Expected one visible Obter código button, found {len(buttons)}")
            buttons[0].click()

        code_element = wait.until(
            lambda current: next(
                (
                    item
                    for item in current.find_elements(
                        By.CSS_SELECTOR, ".sc-settings-copy-textfield__text"
                    )
                    if item.is_displayed()
                ),
                None,
            )
        )
        helper_element = next(
            item
            for item in driver.find_elements(
                By.CSS_SELECTOR, ".sc-settings-copy-textfield__helper-text"
            )
            if item.is_displayed()
        )
        value = code_element.text.strip()
        date_match = re.search(r"(\d{2}/\d{2}/\d{4})", helper_element.text)
        if not re.fullmatch(r"[A-Z0-9]{8}", value):
            raise RuntimeError("Authorization code does not match the expected 8-character format")

        print(
            json.dumps(
                {
                    "store": args.store,
                    "valid_format": True,
                    "mask": f"{value[:2]}****{value[-2:]}",
                    "sha256_prefix": hashlib.sha256(value.encode()).hexdigest()[:12],
                    "valid_date": date_match.group(1) if date_match else None,
                    "dialog_left_open": True,
                },
                ensure_ascii=False,
            )
        )
    finally:
        driver.service.stop()


if __name__ == "__main__":
    main()
