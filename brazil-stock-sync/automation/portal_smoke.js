const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const {
  classifyUpSellerPage,
  classifyOmsPage,
  isAuthenticated,
} = require('./portal_adapter');

const ROOT = path.resolve(__dirname, '..');
const PROFILE = path.join(ROOT, 'runtime', 'playwright-profile');
const TARGETS = [
  {
    name: 'upseller',
    url: 'https://app.upseller.com/zh-CN/analytics/product-sales?warehouseId=',
    classify: classifyUpSellerPage,
  },
  {
    name: 'oms',
    url: 'https://aplichic.jfwms.net/?redirect=/web/inventory/index',
    classify: classifyOmsPage,
  },
];

async function inspectTarget(page, target) {
  await page.goto(target.url, { waitUntil: 'domcontentloaded', timeout: 45_000 });
  await page.waitForTimeout(1_000);
  const snapshot = {
    name: target.name,
    url: page.url(),
    title: await page.title(),
    bodyText: (await page.locator('body').innerText()).slice(0, 2_000),
  };
  return {
    ...snapshot,
    pageType: target.classify(snapshot.url),
    authenticated: isAuthenticated(snapshot),
  };
}

async function main() {
  fs.mkdirSync(PROFILE, { recursive: true });
  const context = await chromium.launchPersistentContext(PROFILE, {
    headless: false,
    viewport: { width: 1440, height: 900 },
  });
  try {
    const results = [];
    for (const target of TARGETS) {
      const page = await context.newPage();
      try {
        results.push(await inspectTarget(page, target));
      } catch (error) {
        results.push({ name: target.name, error: String(error.message || error) });
      } finally {
        await page.close();
      }
    }
    console.log(JSON.stringify(results, null, 2));
  } finally {
    await context.close();
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
