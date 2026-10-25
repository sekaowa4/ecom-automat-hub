import json
import sys
from pathlib import Path

from selenium import webdriver
from selenium.webdriver.chrome.service import Service
from selenium.webdriver.support.ui import WebDriverWait


sys.stdout.reconfigure(encoding="utf-8")
options = webdriver.ChromeOptions()
options.add_experimental_option("debuggerAddress", "127.0.0.1:13641")
driver = webdriver.Chrome(
    service=Service(str(Path("runtime/chromedriver/chromedriver146.exe"))),
    options=options,
)

try:
    target = "https://vendedores.mercadolivre.com.br/preferencias-de-venda"
    driver.get(target)
    WebDriverWait(driver, 20).until(lambda current: "preferencias-de-venda" in current.current_url)
    print(json.dumps({"title": driver.title, "url": driver.current_url}, ensure_ascii=False))
finally:
    driver.service.stop()
