const fs = require('node:fs/promises');
const fsSync = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { chromium } = require('playwright');
const { connectToExistingChrome } = require('./chrome_connection');

const WRITE_RANGES = [
  { start: 'P', end: 'W' },
  { start: 'Z', end: 'AC' },
  { start: 'AI', end: 'AW' },
];
const NATIVE_SELECTION_SETTLE_MS = 1500;

function columnNumber(column) {
  return [...column].reduce((value, letter) => value * 26 + letter.charCodeAt(0) - 64, 0);
}

function columnsBetween(start, end) {
  const names = [];
  for (let value = columnNumber(start); value <= columnNumber(end); value += 1) {
    let current = value;
    let name = '';
    while (current > 0) {
      const remainder = (current - 1) % 26;
      name = String.fromCharCode(65 + remainder) + name;
      current = Math.floor((current - 1) / 26);
    }
    names.push(name);
  }
  return names;
}

function buildBlocks(skus, updatesBySku, updateLabel = '') {
  const expected = new Set(skus);
  for (const sku of Object.keys(updatesBySku)) {
    if (!expected.has(sku)) throw new Error(`unexpected SKU in writeback data: ${sku}`);
  }
  if (!skus.length) throw new Error('no SKU rows available for browser writeback');

  const blocks = WRITE_RANGES.map(({ start, end }) => {
    const columns = columnsBetween(start, end);
    return {
      range: `${start}6:${end}${skus.length + 5}`,
      text: skus.map((sku) => columns.map((column) => updatesBySku[sku]?.[column] ?? '').join('\t')).join('\n'),
    };
  });
  return updateLabel ? [{ range: 'A1', text: updateLabel }, ...blocks] : blocks;
}

async function getSheetPage(context, sheetUrl) {
  let page = context.pages().find((item) => item.url().startsWith(sheetUrl.split('?')[0]));
  if (!page) {
    page = await context.newPage();
    await page.goto(sheetUrl, { waitUntil: 'domcontentloaded', timeout: 30_000 });
  }
  await page.locator('input.bar-label').first().waitFor({ state: 'visible', timeout: 30_000 });
  return page;
}

function buildNativePasteCommand(windowTitle, clipboardPath) {
  const title = Buffer.from(windowTitle, 'utf8').toString('base64');
  const clipboardFile = Buffer.from(clipboardPath, 'utf8').toString('base64');
  return `Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class StockNativeInput {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
'@
$title = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${title}'))
$clipboardFile = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${clipboardFile}'))
$window = Get-Process chrome | Where-Object { $_.MainWindowTitle -like "$title*" } | Select-Object -First 1
if (-not $window) { throw "Chrome window not found: $title" }
if (-not [StockNativeInput]::SetForegroundWindow($window.MainWindowHandle)) { throw "Could not activate Chrome window: $title" }
[StockNativeInput+RECT]$rect = New-Object StockNativeInput+RECT
if (-not [StockNativeInput]::GetWindowRect($window.MainWindowHandle, [ref]$rect)) { throw "Could not read Chrome window bounds: $title" }
Set-Clipboard -Value (Get-Content -Raw -Encoding utf8 -LiteralPath $clipboardFile)
Start-Sleep -Milliseconds 250
[StockNativeInput]::SetCursorPos($rect.Left + 250, $rect.Top + 370) | Out-Null
[StockNativeInput]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
[StockNativeInput]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.SendKeys]::SendWait('^v')`;
}

async function pasteBlock(page, block) {
  const nameBox = page.locator('input.bar-label').first();
  await nameBox.fill(block.range);
  await nameBox.press('Enter');
  await page.waitForTimeout(NATIVE_SELECTION_SETTLE_MS);
  const clipboardPath = path.join(os.tmpdir(), `brazil-stock-paste-${process.pid}-${Date.now()}.tsv`);
  fsSync.writeFileSync(clipboardPath, block.text, 'utf8');
  try {
    execFileSync('powershell.exe', ['-NoProfile', '-Command', buildNativePasteCommand(await page.title(), clipboardPath)], { stdio: 'pipe' });
  } finally {
    fsSync.rmSync(clipboardPath, { force: true });
  }
  await page.waitForTimeout(1000);
}

async function writeBlocks(sheetUrl, blocks, { chromiumImpl = chromium, connect = connectToExistingChrome } = {}) {
  const context = await connect(chromiumImpl);
  try {
    const page = await getSheetPage(context, sheetUrl);
    for (const block of blocks) await pasteBlock(page, block);
    return { updated_rows: blocks.length ? blocks[0].text.split('\n').length : 0, ranges: blocks.length };
  } finally {
    await context.browser()?.close();
  }
}

async function main() {
  const [requestPath, resultPath] = process.argv.slice(2);
  if (!requestPath || !resultPath) throw new Error('request and result paths are required');
  const request = JSON.parse(await fs.readFile(requestPath, 'utf8'));
  const blocks = buildBlocks(request.skus, request.updates_by_sku, request.update_label);
  const result = await writeBlocks(request.sheet_url, blocks);
  await fs.writeFile(resultPath, JSON.stringify(result), 'utf8');
  process.exit(0);
}

if (require.main === module) {
  main().catch((error) => {
    process.stderr.write(String(error.stack || error));
    process.exitCode = 1;
  });
}

module.exports = { NATIVE_SELECTION_SETTLE_MS, buildBlocks, buildNativePasteCommand, columnsBetween, writeBlocks };
