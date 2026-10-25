const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');

const ROOT = path.resolve(__dirname, '..');
const PROFILE_DIR = path.join(ROOT, 'runtime', 'portal-profile-chrome');
const CHROME_PATH = process.env.BRAZIL_STOCK_CHROME || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';

const targets = [
  ['UpSeller', 'https://app.upseller.com/zh-CN/analytics/product-sales?warehouseId='],
  ['OMS', 'https://aplichic.jfwms.net/?redirect=/web/inventory/index'],
];

async function main() {
  fs.mkdirSync(PROFILE_DIR, { recursive: true });
  const context = await chromium.launchPersistentContext(PROFILE_DIR, {
    executablePath: fs.existsSync(CHROME_PATH) ? CHROME_PATH : undefined,
    headless: false,
    viewport: { width: 1440, height: 900 },
  });
  for (const [, url] of targets) {
    const page = await context.newPage();
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 30_000 }).catch(() => {});
  }
  process.stdout.write('LOGIN_WINDOWS_OPEN\n');
  await new Promise(() => {});
}

main().catch((error) => {
  process.stderr.write(`${error.stack || error}\n`);
  process.exitCode = 1;
});
