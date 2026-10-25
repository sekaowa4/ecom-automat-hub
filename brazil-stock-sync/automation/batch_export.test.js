const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const JSZip = require('jszip');
const {
  calendarDateTitle,
  calendarRangeCellSelectors,
  clickSalesPreset,
  clickSalesVariantMode,
  clickVisibleDropdownText,
  createProgressReporter,
  exportThirdPartyInventory,
  findThirdPartyInventoryDownloadPages,
  getDedicatedUpSellerPage,
  handleBatchFailure,
  openSalesDatePicker,
  salesRangeValue,
  setSalesPreset,
  validateSalesVariantWorkbook,
} = require('./batch_export');

const PRODUCT_SALES = '\u4ea7\u54c1\u9500\u91cf';
const PAST_7_DAYS = '\u8fc7\u53bb7\u5929';
const VARIANT_MODE = '\u6309\u53d8\u79cd';
const THIRD_PARTY = '\u4e09\u65b9\u4ed3';
const IMPORT_EXPORT = '\u5bfc\u5165 & \u5bfc\u51fa';
const EXPORT_PAGES = '\u5bfc\u51fa\u9875\u6570';
const EXPORT = '\u5bfc\u51fa';
const STOCK_FILE = '\u5e93\u5b58\u6570\u636e.xlsx';
const STORE = '\u5e97\u94fa';
const PRODUCT = '\u4ea7\u54c1';
const MULTI_VARIANT = '\u591a\u53d8\u79cd';

async function writeTinyXlsx(filePath, headers) {
  const zip = new JSZip();
  zip.file('[Content_Types].xml', '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/></Types>');
  zip.file('_rels/.rels', '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>');
  zip.file('xl/workbook.xml', '<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>');
  zip.file('xl/_rels/workbook.xml.rels', '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>');
  const shared = headers.map((header) => `<si><t>${header}</t></si>`).join('');
  zip.file('xl/sharedStrings.xml', `<?xml version="1.0" encoding="UTF-8"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="${headers.length}" uniqueCount="${headers.length}">${shared}</sst>`);
  const cells = headers.map((_, index) => {
    const col = String.fromCharCode('A'.charCodeAt(0) + index);
    return `<c r="${col}1" t="s"><v>${index}</v></c>`;
  }).join('');
  zip.file('xl/worksheets/sheet1.xml', `<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1">${cells}</row></sheetData></worksheet>`);
  fs.writeFileSync(filePath, await zip.generateAsync({ type: 'nodebuffer' }));
}

test('emits a machine-readable completion event immediately after each batch step', () => {
  const messages = [];
  const report = createProgressReporter((line) => messages.push(line));
  report(2, { filename: 'file.xlsx', path: 'runtime/portal-downloads/file.xlsx' });
  assert.deepEqual(messages, [JSON.stringify({ event: 'step-complete', index: 2, filename: 'file.xlsx', path: 'runtime/portal-downloads/file.xlsx' })]);
});

test('exits immediately when the batch fails instead of waiting for Chrome sockets to close', () => {
  const writes = [];
  let exitCode = null;
  handleBatchFailure(new Error('download failed'), (text) => writes.push(text), (code) => { exitCode = code; });
  assert.equal(exitCode, 1);
  assert.match(writes.join(''), /download failed/);
});

test('opens third-party inventory export through its own Chrome context', async () => {
  const page = { url: () => 'https://app.upseller.com/zh-CN/inventory/list?warehouseId=' };
  const context = { newPage: async () => page };
  const result = await getDedicatedUpSellerPage({}, async () => context);
  assert.equal(result.context, context);
  assert.equal(result.page, page);
});

test('clicks only the visible dropdown item when hidden containers contain the same label', async () => {
  const visibleItem = { click: async () => { visibleItem.clicked = true; } };
  const page = {
    getByRole: () => ({ count: async () => 0 }),
    locator: (selector) => {
      if (selector === `.ant-dropdown:not(.ant-dropdown-hidden) [role="menuitem"]:text-is("${EXPORT_PAGES}")`) {
        return { count: async () => 0 };
      }
      if (selector === `.ant-dropdown:not(.ant-dropdown-hidden) :text-is("${EXPORT_PAGES}")`) {
        return {
          count: async () => 2,
          nth: (index) => index === 0
            ? { isVisible: async () => false }
            : { isVisible: async () => true, click: visibleItem.click },
        };
      }
      if (selector === '.ant-dropdown:not(.ant-dropdown-hidden)') return { evaluateAll: async () => '' };
      throw new Error(`unexpected locator: ${selector}`);
    },
    waitForTimeout: async () => {},
  };
  await clickVisibleDropdownText(page, EXPORT_PAGES);
  assert.equal(visibleItem.clicked, true);
});

test('downloads third-party inventory from the export task instead of waiting for 100 percent on the source page', async () => {
  const clicked = [];
  const waitableText = (label) => ({
    first: () => ({
      waitFor: async () => {},
      locator: () => ({ count: async () => 0 }),
    }),
    click: async () => clicked.push(label),
  });
  const page = {
    goto: async () => {},
    getByText: (label) => {
      if (label === EXPORT) return { count: async () => 1, nth: () => ({ click: async () => clicked.push(label) }) };
      return waitableText(label);
    },
    getByRole: (role, options) => ({
      count: async () => 0,
      isVisible: async () => false,
    }),
    locator: (selector) => {
      if (selector === 'body') return { innerText: async () => '\u603b\u9875\u6570 3' };
      if (selector === `:text-is("${THIRD_PARTY}"):visible`) return { first: () => ({ waitFor: async () => {} }) };
      if (selector === `:text-is("${IMPORT_EXPORT}"):visible`) return { first: () => ({ waitFor: async () => {} }) };
      if (selector === '.ant-modal input:visible') return { count: async () => 2, nth: () => ({ fill: async () => {} }) };
      if (selector === '.ant-dropdown-trigger') return {
        filter: () => ({
          first: () => ({
            count: async () => 1,
            hover: async () => {},
            click: async () => clicked.push(IMPORT_EXPORT),
          }),
        }),
      };
      if (selector === `.ant-dropdown:not(.ant-dropdown-hidden) [role="menuitem"]:text-is("${EXPORT_PAGES}")`) return { count: async () => 0 };
      if (selector === `.ant-dropdown:not(.ant-dropdown-hidden) :text-is("${EXPORT_PAGES}")`) return {
        count: async () => 1,
        nth: () => ({ isVisible: async () => true, click: async () => clicked.push(EXPORT_PAGES) }),
      };
      if (selector === '.ant-dropdown:not(.ant-dropdown-hidden)') return { evaluateAll: async () => '' };
      throw new Error(`unexpected locator: ${selector}`);
    },
    waitForTimeout: async () => {},
  };
  let downloaded = '';
  const result = await exportThirdPartyInventory(page, STOCK_FILE, async (name) => {
    downloaded = name;
    return { path: name };
  });
  assert.deepEqual(clicked, [IMPORT_EXPORT, EXPORT_PAGES, EXPORT]);
  assert.equal(downloaded, STOCK_FILE);
  assert.deepEqual(result, { path: STOCK_FILE });
});

test('finds third-party inventory downloads on the current inventory list page', () => {
  const inventoryList = { url: () => 'https://app.upseller.com/zh-CN/inventory/list?warehouseId=' };
  const threePl = { url: () => 'https://app.upseller.com/zh-CN/inventory/3pl-list?warehouseId=' };
  const fullInventory = { url: () => 'https://app.upseller.com/zh-CN/inventory/third-inventory/mercado-store?warehouseId=' };
  const sales = { url: () => 'https://app.upseller.com/zh-CN/analytics/product-sales?warehouseId=' };
  assert.deepEqual(findThirdPartyInventoryDownloadPages([sales, fullInventory, inventoryList, threePl]), [inventoryList, threePl]);
});

test('waits for the selected sales date range to change before exporting a preset', async () => {
  const values = ['04/07/2026 ~ 02/08/2026', '04/07/2026 ~ 02/08/2026', '27/07/2026 ~ 02/08/2026'];
  let selected = false;
  const page = {
    goto: async () => {},
    getByText: (label) => label === PRODUCT_SALES ? { waitFor: async () => {} } : { first: () => ({ locator: () => ({ count: async () => 0 }), click: async () => { selected = true; } }) },
    locator: (selector) => {
      assert.match(selector, /^input\[placeholder=/);
      return { count: async () => 1, first: () => ({ inputValue: async () => values.shift() }) };
    },
    waitForTimeout: async () => {},
  };
  await setSalesPreset(page, PAST_7_DAYS, { requireRangeChange: true });
  assert.equal(selected, true);
});

test('clicks the control that owns a sales preset label', async () => {
  const parentControl = {
    getAttribute: async () => 'ant-radio-button-wrapper',
    click: async () => { parentControl.clicked = true; },
  };
  const page = {
    getByText: (label, options) => {
      assert.equal(label, PAST_7_DAYS);
      assert.deepEqual(options, { exact: true });
      return { first: () => ({ locator: (selector) => {
        assert.equal(selector, 'xpath=ancestor::*[self::button or contains(@class,"ant-radio-button-wrapper") or contains(@class,"ant-btn")][1]');
        return { count: async () => 1, first: () => parentControl };
      } }) };
    },
  };
  await clickSalesPreset(page, PAST_7_DAYS);
  assert.equal(parentControl.clicked, true);
});

test('does not click an already selected sales preset', async () => {
  const selectedControl = {
    getAttribute: async (name) => {
      assert.equal(name, 'class');
      return 'ant-radio-button-wrapper ant-radio-button-wrapper-checked';
    },
    click: async () => { throw new Error('already selected preset must not be clicked'); },
  };
  const page = {
    getByText: () => ({ first: () => ({ locator: () => ({ count: async () => 1, first: () => selectedControl }) }) }),
  };

  await clickSalesPreset(page, PAST_7_DAYS);
});

test('clicks variant mode before exporting sales data', async () => {
  let clicked = false;
  const page = {
    getByText: (label, options) => {
      assert.equal(label, VARIANT_MODE);
      assert.deepEqual(options, { exact: true });
      return { first: () => ({ locator: () => ({ count: async () => 1, first: () => ({ click: async () => { clicked = true; } }) }) }) };
    },
  };
  await clickSalesVariantMode(page);
  assert.equal(clicked, true);
});

test('accepts exported sales workbooks only when B1 is store and C1 is SKU', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sales-header-'));
  const filePath = path.join(directory, 'variant.xlsx');
  await writeTinyXlsx(filePath, [PRODUCT, STORE, 'SKU', MULTI_VARIANT]);
  await validateSalesVariantWorkbook(filePath);
  fs.rmSync(directory, { recursive: true, force: true });
});

test('accepts sales headers encoded as numeric XML entities', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sales-header-'));
  const filePath = path.join(directory, 'variant.xlsx');
  await writeTinyXlsx(filePath, [PRODUCT, '&#24215;&#38138;', 'SKU', MULTI_VARIANT]);
  await validateSalesVariantWorkbook(filePath);
  fs.rmSync(directory, { recursive: true, force: true });
});

test('rejects product-mode sales workbooks whose C1 is parent SKU', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sales-header-'));
  const filePath = path.join(directory, 'product.xlsx');
  await writeTinyXlsx(filePath, [PRODUCT, 'SKU/\u591a\u53d8\u79cd', '\u7236SKU/\u4ea7\u54c1ID', '\u6709\u6548\u8ba2\u5355\u91cf']);
  await assert.rejects(() => validateSalesVariantWorkbook(filePath), /\u9500\u552e\u6570\u636e\u5bfc\u51fa\u6a21\u5f0f\u9519\u8bef/);
  fs.rmSync(directory, { recursive: true, force: true });
});

test('opens the sales date picker through the outer calendar control', async () => {
  let clicked = false;
  const page = { locator: (selector) => {
    assert.equal(selector, '.ant-calendar-picker:visible');
    return { count: async () => 1, first: () => ({ click: async () => { clicked = true; } }) };
  } };
  await openSalesDatePicker(page);
  assert.equal(clicked, true);
});

test('uses the date-picker title format exposed by the current UpSeller calendar', () => {
  assert.equal(calendarDateTitle('2026-07-27'), '2026\u5e747\u670827\u65e5');
  assert.equal(calendarDateTitle('2026-08-02'), '2026\u5e748\u67082\u65e5');
});

test('selects range boundaries from the calendar panels that own start and end dates', () => {
  assert.deepEqual(calendarRangeCellSelectors({ start: '2026-07-27', end: '2026-08-02' }), {
    start: 'td[title="2026\u5e747\u670827\u65e5"]:not(.ant-calendar-last-month-cell):not(.ant-calendar-next-month-btn-day)',
    end: 'td[title="2026\u5e748\u67082\u65e5"]:not(.ant-calendar-last-month-cell):not(.ant-calendar-next-month-btn-day)',
  });
});

test('finds same-month boundaries even when the month is currently on the right calendar', () => {
  assert.deepEqual(calendarRangeCellSelectors({ start: '2026-08-10', end: '2026-08-12' }), {
    start: 'td[title="2026\u5e748\u670810\u65e5"]:not(.ant-calendar-last-month-cell):not(.ant-calendar-next-month-btn-day)',
    end: 'td[title="2026\u5e748\u670812\u65e5"]:not(.ant-calendar-last-month-cell):not(.ant-calendar-next-month-btn-day)',
  });
});

test('reads the first current date control when UpSeller temporarily renders duplicates', async () => {
  const page = { locator: () => ({ count: async () => 2, first: () => ({ inputValue: async () => '27/07/2026' }) }) };
  assert.equal(await salesRangeValue(page), '27/07/2026');
});
