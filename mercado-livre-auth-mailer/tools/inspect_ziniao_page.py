import json
import sys
from pathlib import Path

from selenium import webdriver
from selenium.webdriver.chrome.service import Service


options = webdriver.ChromeOptions()
sys.stdout.reconfigure(encoding="utf-8")
options.add_experimental_option("debuggerAddress", "127.0.0.1:13641")
driver_path = Path("runtime/chromedriver/chromedriver146.exe")
driver = webdriver.Chrome(service=Service(str(driver_path)), options=options)

try:
    items = driver.execute_script(
        r"""
        return Array.from(document.querySelectorAll('a,button'))
          .map((element) => ({
            tag: element.tagName,
            text: (element.innerText || element.getAttribute('aria-label') || element.title || '')
              .trim()
              .replace(/\s+/g, ' '),
            href: element.href || ''
          }))
          .filter((item) => item.text)
          .slice(0, 250);
        """
    )
    print(json.dumps({"title": driver.title, "url": driver.current_url, "items": items}, ensure_ascii=False))
finally:
    driver.service.stop()
