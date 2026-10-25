const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pipeline } = require('node:stream/promises');
const { chromium } = require('playwright');
const { getExportConfig } = require('./export_config');
const { connectToExistingChrome } = require('./chrome_connection');
const { selectPortalPage } = require('./portal_page_selection');

const ROOT = path.resolve(__dirname, '..');
const DOWNLOAD_DIR = path.join(ROOT, 'runtime', 'portal-downloads');
const EXPORT_LABEL = '\u5bfc\u51fa';

async function uniqueExportButton(page) {
  const roleButton = page.getByRole('button', { name: EXPORT_LABEL, exact: true });
  const roleCount = await roleButton.count();
  if (roleCount === 1) return roleButton;
  const roleLink = page.getByRole('link', { name: EXPORT_LABEL, exact: true });
  const linkCount = await roleLink.count();
  if (linkCount === 1) return roleLink;
  const textButton = page.locator('button').filter({ hasText: EXPORT_LABEL });
  const textCount = await textButton.count();
  if (textCount === 1) return textButton;
  const textNode = page.getByText(EXPORT_LABEL, { exact: true });
  if (await textNode.count() === 1) {
    const ancestorButton = textNode.first().locator('xpath=ancestor::button[1]');
    if (await ancestorButton.count() === 1) return ancestorButton;
  }
  throw new Error(`\u5bfc\u51fa\u6309\u94ae\u4e0d\u552f\u4e00\u6216\u4e0d\u5b58\u5728\uff1a${roleCount}/${linkCount}/${textCount}`);
}

async function waitForUniqueExportButton(page, timeoutMs = 90_000) {
  const deadline = Date.now() + timeoutMs;
  let lastError;
  while (Date.now() < deadline) {
    try {
      return await uniqueExportButton(page);
    } catch (error) {
      lastError = error;
      await page.waitForTimeout(500);
    }
  }
  throw lastError || new Error('导出按钮等待超时');
}

async function clickForDirectDownload(page, button, timeoutMs = 15_000) {
  if (typeof page.locator === 'function') {
    await page.locator('.ant-spin-spinning').waitFor({ state: 'hidden', timeout: 90_000 });
  }
  const downloadPromise = page.waitForEvent('download', { timeout: timeoutMs }).catch(() => null);
  await button.click({ timeout: 8_000, noWaitAfter: true });
  return downloadPromise;
}

function chromeDownloadDir() {
  return path.join(os.homedir(), 'Downloads');
}

function escapedRegExp(text) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function newestChromeDownloadedFile(suggestedName, downloadDir = chromeDownloadDir()) {
  if (!fs.existsSync(downloadDir)) return null;
  const parsed = path.parse(suggestedName);
  const pattern = new RegExp(`^${escapedRegExp(parsed.name)}(?: \\(\\d+\\))?${escapedRegExp(parsed.ext)}$`, 'i');
  return fs.readdirSync(downloadDir, { withFileTypes: true })
    .filter((entry) => entry.isFile() && pattern.test(entry.name) && !entry.name.endsWith('.crdownload'))
    .map((entry) => {
      const fullPath = path.join(downloadDir, entry.name);
      const stat = fs.statSync(fullPath);
      return { path: fullPath, mtimeMs: stat.mtimeMs, size: stat.size };
    })
    .filter((entry) => entry.size > 0)
    .sort((left, right) => right.mtimeMs - left.mtimeMs)[0]?.path || null;
}

function wait(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function copyNewestChromeDownload(suggestedName, destination, downloadDir = chromeDownloadDir(), timeoutMs = 10_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const sourcePath = newestChromeDownloadedFile(suggestedName, downloadDir);
    if (sourcePath) {
      fs.copyFileSync(sourcePath, destination);
      return sourcePath;
    }
    await wait(250);
  }
  throw new Error(`Chrome download file was not found: ${suggestedName}`);
}

async function copyNewestWorkbookDownload(destination, downloadDir = chromeDownloadDir(), startedAtMs = Date.now() - 1_000, timeoutMs = 15_000) {
  fs.mkdirSync(path.dirname(destination), { recursive: true });
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (fs.existsSync(downloadDir)) {
      const sourcePath = fs.readdirSync(downloadDir, { withFileTypes: true })
        .filter((entry) => entry.isFile() && /\.(xlsx|xls|csv)$/i.test(entry.name) && !entry.name.endsWith('.crdownload'))
        .map((entry) => {
          const fullPath = path.join(downloadDir, entry.name);
          const stat = fs.statSync(fullPath);
          return { path: fullPath, mtimeMs: stat.mtimeMs, size: stat.size };
        })
        .filter((entry) => entry.size > 0 && entry.mtimeMs >= startedAtMs - 1_000)
        .sort((left, right) => right.mtimeMs - left.mtimeMs)[0]?.path || null;
      if (sourcePath) {
        fs.rmSync(destination, { force: true });
        fs.copyFileSync(sourcePath, destination);
        return sourcePath;
      }
    }
    await wait(250);
  }
  throw new Error('Chrome workbook download file was not found after clicking visible download button');
}
async function streamDownloadFile(download, destination) {
  if (typeof download.createReadStream !== 'function') return false;
  const stream = await download.createReadStream();
  if (!stream) return false;
  await pipeline(stream, fs.createWriteStream(destination));
  return fs.existsSync(destination) && fs.statSync(destination).size > 0;
}

async function saveDownloadFile(download, source, outputName = '', destinationDir = DOWNLOAD_DIR, fallbackDownloadDir = chromeDownloadDir()) {
  const suggestedName = download.suggestedFilename() || `${source}-export.xlsx`;
  const safeName = outputName || `${source}-${Date.now()}-${suggestedName}`;
  const destination = path.join(destinationDir, safeName);
  fs.rmSync(destination, { force: true });
  try {
    await download.saveAs(destination);
    if (fs.existsSync(destination) && fs.statSync(destination).size > 0) {
      return { source, path: destination, suggestedName };
    }
  } catch (error) {
    const recoverable = ['ENOENT', 'EPERM'];
    const message = String(error?.message || error);
    if (!recoverable.includes(error?.code) && !recoverable.some((code) => message.includes(code))) throw error;
    if (await streamDownloadFile(download, destination)) return { source, path: destination, suggestedName };
  }
  if (fallbackDownloadDir !== destinationDir) {
    try {
      await copyNewestChromeDownload(suggestedName, destination, destinationDir, 1_000);
      return { source, path: destination, suggestedName };
    } catch {}
  }
  await copyNewestChromeDownload(suggestedName, destination, fallbackDownloadDir);
  return { source, path: destination, suggestedName };
}

async function allowBrowserDownloadToDirectory(page, downloadDir = DOWNLOAD_DIR) {
  if (typeof page.context !== 'function') return false;
  const context = page.context();
  if (typeof context.newCDPSession !== 'function') return false;
  const client = await context.newCDPSession(page);
  try {
    await client.send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: downloadDir, eventsEnabled: true });
    return true;
  } catch {
    await client.send('Page.setDownloadBehavior', { behavior: 'allow', downloadPath: downloadDir });
    return true;
  }
}

async function choosePortalPage(context, hostname, preferredUrl, activePage = null) {
  return activePage || selectPortalPage(context, hostname, preferredUrl) || await context.newPage();
}

function upSellerReportDialogTitlePattern() {
  return /^\u5bfc\u51fa(?:\u62a5\u8868)?$/;
}

async function visibleReportModal(page) {
  const modal = page.locator('.ant-modal:visible').filter({ has: page.getByText(upSellerReportDialogTitlePattern()) });
  const count = await modal.count();
  if (count === 0) return null;
  if (count > 1) throw new Error(`UpSeller export report dialogs are ambiguous: ${count}`);
  return modal.first();
}

async function closeExistingReportDialog(page) {
  const modal = await visibleReportModal(page);
  if (!modal) return false;
  await closeReportDialogAfterDownload(modal);
  return true;
}

async function closeReportDialogAfterDownload(modal) {
  if (typeof modal.isVisible === 'function' && !await modal.isVisible().catch(() => false)) return;
  const closeButton = modal.locator('button.ant-modal-close');
  if (await closeButton.count() !== 1) throw new Error('UpSeller export report dialog has no unique close button');
  await closeButton.click({ force: true, timeout: 8_000 });
  await modal.waitFor({ state: 'hidden', timeout: 8_000 });
}

async function waitForReportModal(page, timeoutMs = 90_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const modal = await visibleReportModal(page);
    if (modal) return modal;
    await page.waitForTimeout(500);
  }
  throw new Error('UpSeller export report dialog did not appear');
}

async function clickReportDownload(downloadButton) {
  await downloadButton.click({ timeout: 8_000, noWaitAfter: true });
}

async function resolveReportDownloadButton(modal) {
  const downloadText = modal.getByText('\u4e0b\u8f7d', { exact: true });
  const downloadCount = await downloadText.count();
  if (downloadCount !== 1) throw new Error(`UpSeller export download buttons are ambiguous: ${downloadCount}`);
  const ancestorButton = downloadText.first().locator('xpath=ancestor::button[1]');
  if (await ancestorButton.count() === 1) return ancestorButton.first();
  return downloadText.first();
}

async function resolveVisibleButtonByText(scope, label) {
  const text = scope.getByText(label, { exact: true });
  const count = await text.count();
  if (count < 1) throw new Error(`Portal button text was not found: ${label}`);
  if (typeof text.nth !== 'function') {
    const first = text.first();
    const ancestorButton = first.locator('xpath=ancestor::button[1]');
    if (await ancestorButton.count() === 1) return ancestorButton.first();
    return first;
  }
  for (let index = 0; index < count; index += 1) {
    const candidate = text.nth(index);
    if (!await candidate.isVisible().catch(() => false)) continue;
    const ancestorButton = candidate.locator('xpath=ancestor::button[1]');
    if (await ancestorButton.count() === 1) return ancestorButton.first();
    return candidate;
  }
  throw new Error(`Portal button text was not visible: ${label}`);
}

async function resolveLastVisibleButtonByText(scope, label) {
  const text = scope.getByText(label, { exact: true });
  const count = await text.count();
  if (typeof text.nth !== 'function') {
    const first = text.first();
    const ancestorButton = first.locator('xpath=ancestor::button[1]');
    if (await ancestorButton.count() === 1) return ancestorButton.first();
    return first;
  }
  for (let index = count - 1; index >= 0; index -= 1) {
    const candidate = text.nth(index);
    if (!await candidate.isVisible().catch(() => false)) continue;
    const ancestorButton = candidate.locator('xpath=ancestor::button[1]');
    if (await ancestorButton.count() === 1) return ancestorButton.first();
    return candidate;
  }
  throw new Error(`Portal button text was not visible: ${label}`);
}

async function saveReportDownload(page, source, outputName = '') {
  const modal = await waitForReportModal(page);
  const downloadButton = await resolveReportDownloadButton(modal);
  await downloadButton.waitFor({ state: 'visible', timeout: 45_000 });
  await allowBrowserDownloadToDirectory(page);
  const downloadPromise = page.waitForEvent('download', { timeout: 30_000 });
  await clickReportDownload(downloadButton);
  const download = await downloadPromise;
  const result = await saveDownloadFile(download, source, outputName);
  await closeReportDialogAfterDownload(modal);
  return result;
}

async function saveVisibleDownloadButton(page, source, outputName = '', destinationDir = DOWNLOAD_DIR, fallbackDownloadDir = chromeDownloadDir(), startedAtMs = Date.now(), fallbackTimeoutMs = 15_000) {
  const downloadButton = await resolveLastVisibleButtonByText(page, '\u4e0b\u8f7d');
  await allowBrowserDownloadToDirectory(page);
  const downloadPromise = page.waitForEvent('download', { timeout: 30_000 });
  await downloadButton.click({ timeout: 8_000, noWaitAfter: true });
  let result;
  try {
    const download = await downloadPromise;
    result = await saveDownloadFile(download, source, outputName, destinationDir, fallbackDownloadDir);
  } catch (error) {
    const message = String(error?.message || error);
    if (!message.includes('Timeout') || !message.includes('download')) throw error;
    const safeName = outputName || `${source}-${Date.now()}-export.xlsx`;
    const destination = path.join(destinationDir, safeName);
    let sourcePath;
    try {
      sourcePath = await copyNewestWorkbookDownload(destination, destinationDir, startedAtMs, 1_000);
    } catch {
      sourcePath = await copyNewestWorkbookDownload(destination, fallbackDownloadDir, startedAtMs, fallbackTimeoutMs);
    }
    result = { source, path: destination, suggestedName: path.basename(sourcePath) };
  }
  const closeButton = page.getByText('\u5173\u95ed', { exact: true }).last();
  if (await closeButton.isVisible().catch(() => false)) await closeButton.click({ timeout: 8_000, noWaitAfter: true });
  return result;
}

async function saveReportDownloadFromFreshConnection(source, outputName, targetUrl, timeoutMs = 20_000) {
  const context = await connectToExistingChrome(chromium);
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const candidates = context.pages().filter((candidate) => candidate.url().includes(targetUrl));
    for (const page of candidates.reverse()) {
      const downloadButton = page.getByText('\u4e0b\u8f7d', { exact: true }).last();
      if (await downloadButton.isVisible().catch(() => false)) {
        return saveReportDownload(page, source, outputName);
      }
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error('\u5bfc\u51fa\u4efb\u52a1\u572820\u79d2\u5185\u672a\u51fa\u73b0\u201c\u4e0b\u8f7d\u201d\u6309\u94ae\uff0c\u5df2\u505c\u6b62\u9632\u6b62\u5361\u4f4f');
}

async function completeUpSellerExport(outputName, targetUrl, saveDownload = saveReportDownloadFromFreshConnection) {
  return saveDownload('upseller', outputName, targetUrl);
}

async function exportPortal(source, outputName = '', preferredUrl = '', activePage = null) {
  const config = getExportConfig(source);
  fs.mkdirSync(DOWNLOAD_DIR, { recursive: true });
  const hostname = new URL(config.url).hostname;
  const context = await connectToExistingChrome(chromium);
  const page = await choosePortalPage(context, hostname, preferredUrl, activePage);
  const targetUrl = preferredUrl || config.url;
  if (!page.url().includes(targetUrl)) await page.goto(targetUrl, { waitUntil: 'domcontentloaded', timeout: 20_000 });
  const bodyText = await page.locator('body').innerText({ timeout: 5_000 });
  if (/login|signin/i.test(`${page.url()} ${bodyText}`)) throw new Error(`${config.name} \u672a\u767b\u5f55`);

  if (source === 'upseller') await closeExistingReportDialog(page);
  const exportButton = await waitForUniqueExportButton(page);
  if (source === 'upseller') {
    const directDownload = await clickForDirectDownload(page, exportButton);
    if (directDownload) {
      const direct = await saveDownloadFile(directDownload, source, outputName);
      return { source, name: config.name, path: direct.path };
    }
  } else {
    await exportButton.click({ force: true, timeout: 8_000 });
  }
  if (source === 'oms') {
    const filteredExport = page.getByRole('menuitem', { name: '\u6309\u7b5b\u9009\u5bfc\u51fa', exact: true });
    await filteredExport.waitFor({ state: 'visible', timeout: 10_000 });
    await filteredExport.click({ force: true, timeout: 8_000 });
    await page.getByText('\u5bfc\u51fa\u5b8c\u6210', { exact: true }).waitFor({ state: 'visible', timeout: 90_000 });
    const result = await saveVisibleDownloadButton(page, source, outputName);
    return { source, name: config.name, path: result.path };
  }
  const result = source === 'upseller'
    ? await saveReportDownload(page, source, outputName)
    : await saveReportDownloadFromFreshConnection(source, outputName, targetUrl);
  return { source, name: config.name, path: result.path };
}

if (require.main === module) {
  const source = process.argv[2];
  const outputName = process.argv[3] || '';
  if (!source) {
    console.error('Usage: node export_portal.js <upseller|oms> [outputName]');
    process.exitCode = 2;
  } else {
    exportPortal(source, outputName).then((result) => {
      process.stdout.write(`${JSON.stringify(result)}\n`);
      process.exit(0);
    }).catch((error) => {
      process.stderr.write(`${error.stack || error}\n`);
      process.exit(1);
    });
  }
}

module.exports = { exportPortal, uniqueExportButton, waitForUniqueExportButton, clickForDirectDownload, clickReportDownload, resolveVisibleButtonByText, resolveReportDownloadButton, saveDownloadFile, choosePortalPage, visibleReportModal, closeExistingReportDialog, closeReportDialogAfterDownload, upSellerReportDialogTitlePattern, waitForReportModal, saveReportDownload, saveVisibleDownloadButton, saveReportDownloadFromFreshConnection, completeUpSellerExport, selectPortalPage };
