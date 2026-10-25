const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { Readable } = require('node:stream');
const { choosePortalPage, closeExistingReportDialog, closeReportDialogAfterDownload, completeUpSellerExport, clickForDirectDownload, clickReportDownload, resolveVisibleButtonByText, resolveReportDownloadButton, saveDownloadFile, saveVisibleDownloadButton, selectPortalPage, uniqueExportButton, upSellerReportDialogTitlePattern } = require('./export_portal');

test('finds an UpSeller export control rendered as a link', async () => {
  const exportLink = { count: async () => 1, first: () => ({ kind: 'link' }) };
  const page = {
    getByRole: (role) => role === 'link'
      ? exportLink
      : { count: async () => 0 },
    locator: () => ({ count: async () => 0 }),
    getByText: () => ({ count: async () => 0 }),
  };

  assert.equal(await uniqueExportButton(page), exportLink);
});

test('replaces an older same-name runtime export before saving the new download', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-export-'));
  const destination = path.join(directory, '近30天销售数据.xlsx');
  fs.writeFileSync(destination, 'old');
  const download = {
    suggestedFilename: () => 'source.xlsx',
    saveAs: async (target) => {
      assert.equal(fs.existsSync(target), false);
      fs.writeFileSync(target, 'new');
    },
  };

  const result = await saveDownloadFile(download, 'upseller', '近30天销售数据.xlsx', directory);

  assert.equal(result.path, destination);
  assert.equal(fs.readFileSync(destination, 'utf8'), 'new');
  fs.rmSync(directory, { recursive: true, force: true });
});

test('falls back to the Chrome download folder when Playwright cannot save a CDP download', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-export-dest-'));
  const chromeDownloads = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-chrome-downloads-'));
  fs.writeFileSync(path.join(chromeDownloads, 'source.xlsx'), 'downloaded-by-chrome');
  const download = {
    suggestedFilename: () => 'source.xlsx',
    saveAs: async () => {
      const error = new Error('missing Playwright artifact');
      error.code = 'ENOENT';
      throw error;
    },
  };

  const result = await saveDownloadFile(download, 'upseller', '近30天销售数据.xlsx', directory, chromeDownloads);

  assert.equal(result.path, path.join(directory, '近30天销售数据.xlsx'));
  assert.equal(fs.readFileSync(result.path, 'utf8'), 'downloaded-by-chrome');
  fs.rmSync(directory, { recursive: true, force: true });
  fs.rmSync(chromeDownloads, { recursive: true, force: true });
});

test('falls back to the Chrome download folder when Playwright cannot write the target file', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-export-dest-'));
  const chromeDownloads = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-chrome-downloads-'));
  fs.writeFileSync(path.join(chromeDownloads, 'source.xlsx'), 'downloaded-by-chrome');
  const download = {
    suggestedFilename: () => 'source.xlsx',
    saveAs: async () => {
      const error = new Error('target denied');
      error.code = 'EPERM';
      throw error;
    },
  };

  const result = await saveDownloadFile(download, 'upseller', '近30天销售数据.xlsx', directory, chromeDownloads);

  assert.equal(fs.readFileSync(result.path, 'utf8'), 'downloaded-by-chrome');
  fs.rmSync(directory, { recursive: true, force: true });
  fs.rmSync(chromeDownloads, { recursive: true, force: true });
});

test('streams the Playwright download when saveAs cannot write the target file', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-export-dest-'));
  const chromeDownloads = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-chrome-downloads-'));
  const download = {
    suggestedFilename: () => 'source.xlsx',
    saveAs: async () => {
      const error = new Error('target denied');
      error.code = 'EPERM';
      throw error;
    },
    createReadStream: async () => Readable.from(['download-stream']),
  };

  const result = await saveDownloadFile(download, 'upseller', '近30天销售数据.xlsx', directory, chromeDownloads);

  assert.equal(fs.readFileSync(result.path, 'utf8'), 'download-stream');
  fs.rmSync(directory, { recursive: true, force: true });
  fs.rmSync(chromeDownloads, { recursive: true, force: true });
});

test('does not accept a zero byte Playwright save when Chrome saved a non-empty file', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-export-dest-'));
  const chromeDownloads = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-chrome-downloads-'));
  fs.writeFileSync(path.join(chromeDownloads, 'source.xlsx'), 'real-download');
  const download = {
    suggestedFilename: () => 'source.xlsx',
    saveAs: async (target) => fs.writeFileSync(target, ''),
  };

  const result = await saveDownloadFile(download, 'upseller', '近30天销售数据.xlsx', directory, chromeDownloads);

  assert.equal(fs.readFileSync(result.path, 'utf8'), 'real-download');
  fs.rmSync(directory, { recursive: true, force: true });
  fs.rmSync(chromeDownloads, { recursive: true, force: true });
});

test('does not accept a zero byte streamed download when Chrome saved a non-empty file', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-export-dest-'));
  const chromeDownloads = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-chrome-downloads-'));
  fs.writeFileSync(path.join(chromeDownloads, 'source.xlsx'), 'real-download');
  const download = {
    suggestedFilename: () => 'source.xlsx',
    saveAs: async () => {
      const error = new Error('target denied');
      error.code = 'EPERM';
      throw error;
    },
    createReadStream: async () => Readable.from(['']),
  };

  const result = await saveDownloadFile(download, 'upseller', '近30天销售数据.xlsx', directory, chromeDownloads);

  assert.equal(fs.readFileSync(result.path, 'utf8'), 'real-download');
  fs.rmSync(directory, { recursive: true, force: true });
  fs.rmSync(chromeDownloads, { recursive: true, force: true });
});

test('falls back to the newest Chrome workbook when a visible download button does not emit a Playwright download event', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-visible-dest-'));
  const chromeDownloads = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-visible-chrome-'));
  fs.writeFileSync(path.join(chromeDownloads, 'inventory.xlsx'), 'eventless-download');
  let clicked = false;
  const page = {
    getByText: (label, options) => {
      assert.deepEqual(options, { exact: true });
      if (label === '\u5173\u95ed') return { last: () => ({ isVisible: async () => false }) };
      assert.equal(label, '\u4e0b\u8f7d');
      return {
        count: async () => 1,
        first: () => ({ locator: () => ({ count: async () => 0 }), click: async () => { clicked = true; } }),
      };
    },
    context: () => ({ newCDPSession: async () => ({ send: async () => true }) }),
    waitForEvent: async () => { throw new Error('Timeout 30000ms exceeded while waiting for event "download"'); },
  };

  const result = await saveVisibleDownloadButton(page, 'upseller', '\u5e93\u5b58\u6570\u636e.xlsx', directory, chromeDownloads, Date.now() - 1000, 100);

  assert.equal(clicked, true);
  assert.equal(result.path, path.join(directory, '\u5e93\u5b58\u6570\u636e.xlsx'));
  assert.equal(fs.readFileSync(result.path, 'utf8'), 'eventless-download');
  fs.rmSync(directory, { recursive: true, force: true });
  fs.rmSync(chromeDownloads, { recursive: true, force: true });
});
test('does not hide unexpected download save failures', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-export-dest-'));
  const chromeDownloads = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-chrome-downloads-'));
  const download = {
    suggestedFilename: () => 'source.xlsx',
    saveAs: async () => {
      const error = new Error('bad download');
      error.code = 'EIO';
      throw error;
    },
  };

  await assert.rejects(
    () => saveDownloadFile(download, 'upseller', '近30天销售数据.xlsx', directory, chromeDownloads),
    /bad download/,
  );
  fs.rmSync(directory, { recursive: true, force: true });
  fs.rmSync(chromeDownloads, { recursive: true, force: true });
});

test('uses the newest matching file from the Chrome download folder', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-export-dest-'));
  const chromeDownloads = fs.mkdtempSync(path.join(os.tmpdir(), 'upseller-chrome-downloads-'));
  const older = path.join(chromeDownloads, 'source.xlsx');
  const newer = path.join(chromeDownloads, 'source (1).xlsx');
  fs.writeFileSync(older, 'old');
  fs.writeFileSync(newer, 'new');
  fs.utimesSync(older, new Date('2026-01-01T00:00:00Z'), new Date('2026-01-01T00:00:00Z'));
  fs.utimesSync(newer, new Date('2026-01-02T00:00:00Z'), new Date('2026-01-02T00:00:00Z'));
  const download = {
    suggestedFilename: () => 'source.xlsx',
    saveAs: async () => {
      const error = new Error('missing Playwright artifact');
      error.code = 'ENOENT';
      throw error;
    },
  };

  const result = await saveDownloadFile(download, 'upseller', '近30天销售数据.xlsx', directory, chromeDownloads);

  assert.equal(fs.readFileSync(result.path, 'utf8'), 'new');
  fs.rmSync(directory, { recursive: true, force: true });
  fs.rmSync(chromeDownloads, { recursive: true, force: true });
});

test('closes an already-open UpSeller report dialog before starting a new export', async () => {
  let clicked = false;
  let waitedForHidden = false;
  const modal = {
    locator: () => ({ count: async () => 1, click: async () => { clicked = true; } }),
    waitFor: async ({ state }) => { waitedForHidden = state === 'hidden'; },
  };
  const page = {
    locator: () => ({ filter: () => ({ count: async () => 1, first: () => modal }) }),
    getByText: () => ({}),
  };

  await closeExistingReportDialog(page);

  assert.equal(clicked, true);
  assert.equal(waitedForHidden, true);
});

test('closes the UpSeller report dialog after saving its download', async () => {
  let clicked = false;
  const modal = {
    locator: (selector) => {
      assert.equal(selector, 'button.ant-modal-close');
      return { count: async () => 1, click: async () => { clicked = true; } };
    },
    waitFor: async () => {},
  };

  await closeReportDialogAfterDownload(modal);

  assert.equal(clicked, true);
});

test('accepts an UpSeller report dialog that disappeared with the download navigation', async () => {
  const modal = {
    isVisible: async () => false,
    locator: () => ({ count: async () => 0 }),
  };

  await closeReportDialogAfterDownload(modal);
});

test('recognizes both UpSeller sales and inventory export dialog titles', () => {
  const pattern = upSellerReportDialogTitlePattern();

  assert.equal(pattern.test('导出报表'), true);
  assert.equal(pattern.test('导出'), true);
});

test('prefers the page matching the requested URL when multiple pages share a hostname', () => {
  const salesPage = { url: () => 'https://app.upseller.com/zh-CN/analytics/product-sales' };
  const fullPage = { url: () => 'https://app.upseller.com/zh-CN/inventory/third-inventory/mercado-store' };
  const context = { pages: () => [salesPage, fullPage] };

  assert.equal(selectPortalPage(context, 'app.upseller.com', fullPage.url()), fullPage);
});

test('falls back to the first matching page when no preferred page is supplied', () => {
  const firstPage = { url: () => 'https://app.upseller.com/first' };
  const secondPage = { url: () => 'https://app.upseller.com/second' };
  const context = { pages: () => [firstPage, secondPage] };

  assert.equal(selectPortalPage(context, 'app.upseller.com'), firstPage);
});

test('uses the download action as the completion condition for a sales export', async () => {
  let downloadArgs;
  const result = await completeUpSellerExport('近15天销售数据.xlsx', 'sales-url', async (...args) => {
      downloadArgs = args;
      return { path: 'runtime/portal-downloads/近15天销售数据.xlsx' };
  });

  assert.deepEqual(downloadArgs, ['upseller', '近15天销售数据.xlsx', 'sales-url']);
  assert.equal(result.path, 'runtime/portal-downloads/近15天销售数据.xlsx');
});

test('captures a browser download triggered directly by the export button', async () => {
  let clicked = false;
  let clickOptions;
  const download = { suggestedFilename: () => 'sales.xlsx' };
  const page = { waitForEvent: async () => download };
  const button = { click: async (options) => { clicked = true; clickOptions = options; } };
  assert.equal(await clickForDirectDownload(page, button), download);
  assert.equal(clicked, true);
  assert.equal(clickOptions.force, undefined);
});

test('uses a normal click for the UpSeller report download action', async () => {
  let options;
  await clickReportDownload({ click: async (received) => { options = received; } });
  assert.equal(options.force, undefined);
});

test('clicks the real button that owns the UpSeller report download text', async () => {
  const parentButton = { tag: 'button' };
  const textNode = {
    count: async () => 1,
    first: () => ({
      locator: (selector) => {
        assert.equal(selector, 'xpath=ancestor::button[1]');
        return { count: async () => 1, first: () => parentButton };
      },
    }),
  };
  const modal = {
    getByText: () => textNode,
  };

  assert.equal(await resolveReportDownloadButton(modal), parentButton);
});

test('resolves a visible portal download text to its owning button', async () => {
  const parentButton = { tag: 'button' };
  const textNode = {
    first: () => ({
      locator: (selector) => {
        assert.equal(selector, 'xpath=ancestor::button[1]');
        return { count: async () => 1, first: () => parentButton };
      },
    }),
  };
  const page = {
    getByText: (label, options) => {
      assert.equal(label, '下载');
      assert.deepEqual(options, { exact: true });
      return { count: async () => 1, first: textNode.first };
    },
  };

  assert.equal(await resolveVisibleButtonByText(page, '下载'), parentButton);
});

test('keeps exporting from the same page that received the sales date filter', async () => {
  const filteredPage = { url: () => 'https://app.upseller.com/zh-CN/analytics/product-sales?warehouseId=' };
  const stalePage = { url: () => 'https://app.upseller.com/zh-CN/analytics/product-sales?warehouseId=' };
  const context = { pages: () => [stalePage, filteredPage], newPage: async () => ({}) };

  assert.equal(await choosePortalPage(context, 'app.upseller.com', filteredPage.url(), filteredPage), filteredPage);
});
