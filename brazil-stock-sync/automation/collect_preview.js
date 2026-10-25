const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const { isAuthenticated, classifyUpSellerPage, classifyOmsPage } = require('./portal_adapter');
const { buildPreviewSummary } = require('./preview_schema');
const { connectToExistingChrome } = require('./chrome_connection');

const ROOT = path.resolve(__dirname, '..');
const DEFAULT_TIMEOUT = 20_000;

const TARGETS = [
  {
    name: 'UpSeller',
    url: 'https://app.upseller.com/zh-CN/analytics/product-sales?warehouseId=',
    classify: classifyUpSellerPage,
  },
  {
    name: 'OMS',
    url: 'https://aplichic.jfwms.net/?redirect=/web/inventory/index',
    classify: classifyOmsPage,
  },
];

async function inspectPage(page, target) {
  const result = { name: target.name, requestedUrl: target.url };
  try {
    const currentUrl = page.url();
    if (!currentUrl || !currentUrl.includes(new URL(target.url).hostname)) {
      await page.goto(target.url, { waitUntil: 'domcontentloaded', timeout: DEFAULT_TIMEOUT });
    }
    result.url = page.url();
    result.title = await page.title();
    result.pageType = target.classify(result.url);
    const bodyText = await page.locator('body').innerText({ timeout: 5_000 });
    result.authenticated = isAuthenticated({ url: result.url, bodyText }) || result.pageType !== 'unknown';
    result.status = result.authenticated ? 'ready_for_export' : 'login_required';
  } catch (error) {
    result.status = 'unreachable_or_timeout';
    result.error = String(error.message || error).slice(0, 500);
  }
  return result;
}

async function main() {
  const context = await connectToExistingChrome(chromium);
  const results = [];
  const openPages = context.pages();
  for (const target of TARGETS) {
    const page = openPages.find((candidate) => candidate.url().includes(new URL(target.url).hostname)) || await context.newPage();
    try {
      results.push(await inspectPage(page, target));
    } finally {
      if (!openPages.includes(page)) await page.close();
    }
  }
  process.stdout.write(`${JSON.stringify(buildPreviewSummary(results), null, 2)}\n`);
  process.exit(0);
}

main().catch((error) => {
  process.stderr.write(`${error.stack || error}\n`);
  process.exitCode = 1;
});
