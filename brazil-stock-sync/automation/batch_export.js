const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const JSZip = require('jszip');
const { connectToExistingChrome } = require('./chrome_connection');
const { exportPortal, saveVisibleDownloadButton } = require('./export_portal');
const { buildExportPlan } = require('./export_plan');

const PRODUCT_SALES = 'https://app.upseller.com/zh-CN/analytics/product-sales?warehouseId=';
const INVENTORY_LIST = 'https://app.upseller.com/zh-CN/inventory/3pl-list?warehouseId=';
const FULL_INVENTORY = 'https://app.upseller.com/zh-CN/inventory/third-inventory/mercado-store?warehouseId=';
const PORTAL_DOWNLOAD_DIR = path.resolve(__dirname, '..', 'runtime', 'portal-downloads');

function displayDate(iso) {
  const [year, month, day] = iso.split('-');
  return `${day}/${month}/${year}`;
}

function calendarDateTitle(iso) {
  const [year, month, day] = iso.split('-');
  return `${year}年${Number(month)}月${Number(day)}日`;
}

function calendarRangeCellSelectors(range) {
  const cell = (iso) => `td[title="${calendarDateTitle(iso)}"]:not(.ant-calendar-last-month-cell):not(.ant-calendar-next-month-btn-day)`;
  return {
    start: cell(range.start),
    end: cell(range.end),
  };
}

function createProgressReporter(write = (line) => process.stdout.write(`${line}\n`)) {
  return (index, result) => {
    write(JSON.stringify({
      event: 'step-complete',
      index,
      filename: result.filename,
      path: result.path,
    }));
  };
}

async function existingStableWorkbook(filePath, startedAtMs, stableMs = 750) {
  if (!fs.existsSync(filePath)) return false;
  const first = fs.statSync(filePath);
  if (!first.isFile() || first.size <= 0 || first.mtimeMs < startedAtMs - 1_000) return false;
  await new Promise((resolve) => setTimeout(resolve, stableMs));
  if (!fs.existsSync(filePath)) return false;
  const second = fs.statSync(filePath);
  return second.isFile() && second.size === first.size && second.size > 0;
}

async function waitForStableWorkbook(filePath, startedAtMs, timeoutMs = 45_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await existingStableWorkbook(filePath, startedAtMs).catch(() => false)) return true;
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  return false;
}

function textBetweenTags(xml, tagName) {
  const pattern = new RegExp(`<${tagName}[^>]*>([\\s\\S]*?)<\\/${tagName}>`, 'g');
  const values = [];
  for (const match of xml.matchAll(pattern)) values.push(match[1].replace(/<[^>]+>/g, ''));
  return values;
}

function decodeXmlText(text) {
  return String(text || '')
    .replace(/&#x([0-9a-fA-F]+);/g, (_, code) => String.fromCodePoint(Number.parseInt(code, 16)))
    .replace(/&#(\d+);/g, (_, code) => String.fromCodePoint(Number.parseInt(code, 10)))
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&amp;/g, '&')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'");
}

function cellColumn(cellRef) {
  return String(cellRef || '').replace(/\d+/g, '');
}

function columnIndex(column) {
  let value = 0;
  for (const char of column) value = value * 26 + char.charCodeAt(0) - 64;
  return value - 1;
}

async function readFirstWorksheetHeaders(workbookPath) {
  const zip = await JSZip.loadAsync(fs.readFileSync(workbookPath));
  const sharedXml = await zip.file('xl/sharedStrings.xml')?.async('string');
  const sharedStrings = sharedXml ? textBetweenTags(sharedXml, 't').map(decodeXmlText) : [];
  const sheetXml = await zip.file('xl/worksheets/sheet1.xml')?.async('string');
  if (!sheetXml) throw new Error(`Excel 文件缺少 Sheet1: ${workbookPath}`);
  const rowMatch = sheetXml.match(/<row[^>]*r="1"[^>]*>([\s\S]*?)<\/row>/) || sheetXml.match(/<row[^>]*>([\s\S]*?)<\/row>/);
  if (!rowMatch) return [];
  const headers = [];
  for (const cellMatch of rowMatch[1].matchAll(/<c\b([^>]*)>([\s\S]*?)<\/c>/g)) {
    const attrs = cellMatch[1];
    const body = cellMatch[2];
    const ref = attrs.match(/\br="([^"]+)"/)?.[1] || '';
    const type = attrs.match(/\bt="([^"]+)"/)?.[1] || '';
    const value = body.match(/<v>([\s\S]*?)<\/v>/)?.[1] || '';
    const inline = body.match(/<t[^>]*>([\s\S]*?)<\/t>/)?.[1] || '';
    const index = columnIndex(cellColumn(ref));
    headers[index] = decodeXmlText(type === 's' ? sharedStrings[Number(value)] : inline || value);
  }
  return headers;
}

async function validateSalesVariantWorkbook(workbookPath) {
  const headers = await readFirstWorksheetHeaders(workbookPath);
  if (headers[1] !== '店铺' || headers[2] !== 'SKU') {
    throw new Error(`销售数据导出模式错误：需要“按变种”导出，期望 B1=店铺、C1=SKU，实际 B1=${headers[1] || ''}、C1=${headers[2] || ''}`);
  }
  return true;
}

async function getUpSellerPage(context) {
  return context.pages().find((page) => page.url().includes('upseller.com')) || await context.newPage();
}

async function getDedicatedUpSellerPage(chromiumImpl = chromium, connect = connectToExistingChrome) {
  const context = await connect(chromiumImpl);
  const page = await context.newPage();
  return { context, page };
}

async function openSalesPage(page) {
  await page.goto(PRODUCT_SALES, { waitUntil: 'domcontentloaded', timeout: 45_000 });
  await page.getByText('\u4ea7\u54c1\u9500\u91cf', { exact: true }).waitFor({ state: 'visible', timeout: 45_000 });
}

async function salesRangeValue(page) {
  const input = page.locator('input[placeholder="按日期筛选"]:visible');
  if (await input.count() < 1) throw new Error('UpSeller sales date control is unavailable');
  return input.first().inputValue();
}

async function waitForSalesRangeChange(page, previousValue, timeoutMs = 45_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await salesRangeValue(page) !== previousValue) return;
    await page.waitForTimeout(500);
  }
  throw new Error('UpSeller sales date did not change; export was blocked to prevent stale data');
}

async function clickSalesPreset(page, label) {
  const text = page.getByText(label, { exact: true }).first();
  const control = text.locator('xpath=ancestor::*[self::button or contains(@class,"ant-radio-button-wrapper") or contains(@class,"ant-btn")][1]');
  if (await control.count() === 1) {
    const firstControl = control.first();
    const className = typeof firstControl.getAttribute === 'function'
      ? await firstControl.getAttribute('class')
      : '';
    if (className?.includes('ant-radio-button-wrapper-checked')) return;
    await firstControl.click({ timeout: 8_000, noWaitAfter: true });
    return;
  }
  await text.click({ timeout: 8_000, noWaitAfter: true });
}

async function clickSalesVariantMode(page) {
  await clickSalesPreset(page, '按变种');
}

async function openSalesDatePicker(page) {
  const picker = page.locator('.ant-calendar-picker:visible');
  if (await picker.count() < 1) throw new Error('UpSeller sales date picker is unavailable');
  await picker.first().click({ timeout: 8_000, noWaitAfter: true });
}

async function setSalesPreset(page, label, { requireRangeChange = false } = {}) {
  await openSalesPage(page);
  const previousRange = requireRangeChange ? await salesRangeValue(page) : '';
  await clickSalesPreset(page, label);
  if (requireRangeChange) await waitForSalesRangeChange(page, previousRange);
  await page.waitForTimeout(1_000);
}

async function setSalesRange(page, range) {
  await openSalesPage(page);
  const previousRange = await salesRangeValue(page);
  await openSalesDatePicker(page);
  const calendar = page.locator('.ant-calendar-picker-container:visible');
  await calendar.waitFor({ state: 'visible', timeout: 10_000 });
  const selectors = calendarRangeCellSelectors(range);
  const startDate = calendar.locator(selectors.start);
  const endDate = calendar.locator(selectors.end);
  if (await startDate.count() !== 1 || await endDate.count() !== 1) {
    throw new Error(`UpSeller 日期不在当前日历面板中：${range.start} ~ ${range.end}`);
  }
  await startDate.click({ force: true });
  await endDate.click({ force: true });
  await waitForSalesRangeChange(page, previousRange);
  await page.waitForTimeout(1_000);
}

async function clickVisibleDropdownText(page, label, timeoutMs = 8_000) {
  const escapedLabel = String(label).replace(/\\/g, '\\\\').replace(/"/g, '\\"');
  const deadline = Date.now() + timeoutMs;
  const locators = [
    page.getByRole?.('menuitem', { name: label, exact: true }),
    page.locator(`.ant-dropdown:not(.ant-dropdown-hidden) [role="menuitem"]:text-is("${escapedLabel}")`),
    page.locator(`.ant-dropdown:not(.ant-dropdown-hidden) :text-is("${escapedLabel}")`),
  ].filter(Boolean);

  while (Date.now() < deadline) {
    for (const locator of locators) {
      const count = await locator.count().catch(() => 0);
      for (let index = 0; index < count; index += 1) {
        const item = locator.nth(index);
        if (await item.isVisible().catch(() => false)) {
          await item.click({ timeout: Math.max(1_000, deadline - Date.now()), force: true });
          return;
        }
      }
    }
    await page.waitForTimeout(250);
  }

  const dropdownText = await page.locator('.ant-dropdown:not(.ant-dropdown-hidden)').evaluateAll((nodes) => nodes
    .map((node) => (node.innerText || node.textContent || '').trim().replace(/\s+/g, ' '))
    .filter(Boolean)
    .join(' | '))
    .catch(() => '');
  throw new Error(`未找到可见下拉菜单项：${label}${dropdownText ? `；当前可见菜单：${dropdownText}` : ''}`);
}

async function openThirdPartyExportMenu(page) {
  const trigger = page.locator('.ant-dropdown-trigger').filter({ hasText: '\u5bfc\u5165 & \u5bfc\u51fa' }).first();
  if (await trigger.count().catch(() => 0) > 0) {
    await trigger.hover({ timeout: 8_000 });
    await page.waitForTimeout(300);
    if (await page.getByRole('menuitem', { name: '\u5bfc\u51fa\u9875\u6570', exact: true }).isVisible().catch(() => false)) return;
    await trigger.click({ timeout: 8_000, force: true, noWaitAfter: true });
    return;
  }

  const text = page.getByText('\u5bfc\u5165 & \u5bfc\u51fa', { exact: true }).first();
  const control = text.locator('xpath=ancestor::*[self::button or contains(@class,"ant-dropdown-trigger") or contains(@class,"ant-btn")][1]');
  if (await control.count().catch(() => 0) > 0) {
    await control.first().hover({ timeout: 8_000 });
    await page.waitForTimeout(300);
    if (await page.getByRole('menuitem', { name: '\u5bfc\u51fa\u9875\u6570', exact: true }).isVisible().catch(() => false)) return;
    await control.first().click({ timeout: 8_000, force: true, noWaitAfter: true });
    return;
  }

  await text.hover({ timeout: 8_000 });
  await page.waitForTimeout(300);
}

async function waitForVisibleExactText(page, label, timeoutMs = 20_000) {
  const escapedLabel = String(label).replace(/\\/g, '\\\\').replace(/"/g, '\\"');
  await page.locator(`:text-is("${escapedLabel}"):visible`).first().waitFor({ state: 'visible', timeout: timeoutMs });
}

async function exportThirdPartyInventory(page, outputName, downloadCompleted = downloadCompletedThirdPartyInventory) {
  await page.goto(INVENTORY_LIST, { waitUntil: 'domcontentloaded', timeout: 20_000 });
  await waitForVisibleExactText(page, '\u4e09\u65b9\u4ed3');
  await waitForVisibleExactText(page, '\u5bfc\u5165 & \u5bfc\u51fa');
  await openThirdPartyExportMenu(page);
  await clickVisibleDropdownText(page, '\u5bfc\u51fa\u9875\u6570');
  const body = await page.locator('body').innerText();
  const match = body.match(/\u603b\u9875\u6570\s+(\d+)/);
  if (!match) throw new Error('\u672a\u8bfb\u53d6\u5230\u4e09\u65b9\u4ed3\u5b9e\u9645\u603b\u9875\u6570');
  const totalPages = Number(match[1]);
  const modalInputs = page.locator('.ant-modal input:visible');
  const inputCount = await modalInputs.count();
  if (inputCount >= 2) {
    await modalInputs.nth(inputCount - 2).fill('1');
    await modalInputs.nth(inputCount - 1).fill(String(totalPages));
  }
  const exportLabels = page.getByText('\u5bfc\u51fa', { exact: true });
  const exportCount = await exportLabels.count();
  await exportLabels.nth(exportCount - 1).click({ force: true });
  // UpSeller sends this export to a separate task page. The original inventory
  // page never receives the task's 100% indicator, so wait for that task's
  // Download action instead of waiting on the wrong page.
  return downloadCompleted(outputName);
}

function findThirdPartyInventoryDownloadPages(pages) {
  return pages.filter((candidate) => {
    const url = candidate.url();
    return url.includes('/inventory/list') || url.includes('/inventory/3pl-list');
  });
}

async function downloadCompletedThirdPartyInventory(outputName) {
  const context = await connectToExistingChrome(chromium);
  const startedAtMs = Date.now();
  const destination = path.join(PORTAL_DOWNLOAD_DIR, outputName);
  const deadline = Date.now() + 90_000;
  while (Date.now() < deadline) {
    if (await existingStableWorkbook(destination, startedAtMs).catch(() => false)) {
      return { source: 'upseller', path: destination, suggestedName: outputName };
    }
    const candidates = findThirdPartyInventoryDownloadPages(context.pages());
    for (const page of candidates.reverse()) {
      if (await existingStableWorkbook(destination, startedAtMs).catch(() => false)) {
        return { source: 'upseller', path: destination, suggestedName: outputName };
      }
      const downloadButton = page.getByText('\u4e0b\u8f7d', { exact: true }).last();
      if (await downloadButton.isVisible().catch(() => false)) {
        let buttonError = null;
        const buttonDownload = saveVisibleDownloadButton(page, 'upseller', outputName).catch((error) => {
          buttonError = error;
          return null;
        });
        const diskDownload = waitForStableWorkbook(destination, startedAtMs).then((ok) => (
          ok ? { source: 'upseller', path: destination, suggestedName: outputName } : null
        ));
        const result = await Promise.race([buttonDownload, diskDownload]);
        if (result) return result;
        if (buttonError) throw buttonError;
      }
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error('\u4e09\u65b9\u4ed3\u5bfc\u51fa\u5b8c\u6210\u540e\u672a\u627e\u5230\u4e0b\u8f7d\u6309\u94ae');
}

async function runBatch({ onRequest } = {}) {
  const context = await connectToExistingChrome(chromium);
  const page = await getUpSellerPage(context);
  if (onRequest) page.on('request', onRequest);
  const plan = buildExportPlan();
  const results = [];
  const reportProgress = createProgressReporter();

  const complete = async (index, operation) => {
    const result = await operation();
    if (index < 4) await validateSalesVariantWorkbook(result.path);
    results.push(result);
    reportProgress(index, { filename: plan[index].filename, path: result.path });
    return result;
  };

  await setSalesPreset(page, '过去30天');
  await clickSalesVariantMode(page);
  await complete(0, () => exportPortal('upseller', plan[0].filename, PRODUCT_SALES, page));
  await setSalesPreset(page, '过去7天', { requireRangeChange: true });
  await clickSalesVariantMode(page);
  await complete(1, () => exportPortal('upseller', plan[1].filename, PRODUCT_SALES, page));
  for (const [index, salesExport] of plan.slice(2, 4).entries()) {
    await setSalesRange(page, salesExport.range);
    await clickSalesVariantMode(page);
    await complete(index + 2, () => exportPortal('upseller', salesExport.filename, PRODUCT_SALES, page));
  }

  await page.goto(FULL_INVENTORY, { waitUntil: 'domcontentloaded', timeout: 20_000 });
  await page.getByText('Mercado Envios Full', { exact: true }).waitFor({ state: 'visible', timeout: 90_000 });
  await page.getByText('\u5e73\u53f0\u4ed3\u5e93', { exact: true }).waitFor({ state: 'visible', timeout: 90_000 });
  await complete(4, () => exportPortal('upseller', plan[4].filename, FULL_INVENTORY, page));
  const thirdParty = await getDedicatedUpSellerPage();
  if (onRequest) thirdParty.page.on('request', onRequest);
  await complete(5, () => exportThirdPartyInventory(thirdParty.page, plan[5].filename));
  return { plan, results };
}

function handleBatchFailure(error, write = (text) => process.stderr.write(text), exit = (code) => process.exit(code)) {
  write(`${error.stack || error}\n`);
  exit(1);
}

if (require.main === module) {
  runBatch().then((result) => {
    process.stdout.write(`${JSON.stringify(result)}\n`);
    process.exit(0);
  }).catch((error) => {
    handleBatchFailure(error);
  });
}

module.exports = { displayDate, calendarDateTitle, calendarRangeCellSelectors, clickSalesPreset, clickSalesVariantMode, clickVisibleDropdownText, createProgressReporter, findThirdPartyInventoryDownloadPages, handleBatchFailure, openSalesDatePicker, readFirstWorksheetHeaders, salesRangeValue, validateSalesVariantWorkbook, waitForSalesRangeChange, setSalesPreset, setSalesRange, exportThirdPartyInventory, downloadCompletedThirdPartyInventory, getDedicatedUpSellerPage, runBatch };
