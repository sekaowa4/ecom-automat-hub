const assert = require('node:assert/strict');
const test = require('node:test');
const { getExportConfig } = require('./export_config');

test('returns UpSeller export configuration', () => {
  const config = getExportConfig('upseller');
  assert.equal(config.name, 'UpSeller');
  assert.match(config.url, /product-sales/);
  assert.equal(config.requiredHeaders.includes('SKU'), true);
});

test('returns OMS export configuration', () => {
  const config = getExportConfig('oms');
  assert.equal(config.name, 'OMS');
  assert.equal(config.url, 'https://aplichic.jfwms.net/web/inventory/index');
  assert.equal(config.requiredHeaders.includes('商品SKU'), true);
});

test('rejects unknown export source', () => {
  assert.throws(() => getExportConfig('unknown'), /Unknown export source/);
});
