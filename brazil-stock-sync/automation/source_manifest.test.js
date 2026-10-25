const assert = require('node:assert/strict');
const test = require('node:test');
const { validateSourceManifest } = require('./source_manifest');

test('accepts a complete source manifest', () => {
  const manifest = validateSourceManifest({
    inventory: 'inventory.xlsx',
    full: 'full.xlsx',
    '3天': 'sales-3.xlsx',
    '7天': 'sales-7.xlsx',
    '15天': 'sales-15.xlsx',
    '30天': 'sales-30.xlsx',
    target: 'Stock_Order.xlsx',
  });
  assert.equal(manifest.ok, true);
  assert.deepEqual(manifest.missing, []);
});

test('blocks a manifest when a sales period is missing', () => {
  const manifest = validateSourceManifest({ inventory: 'inventory.xlsx', target: 'Stock_Order.xlsx' });
  assert.equal(manifest.ok, false);
  assert.deepEqual(manifest.missing, ['full', '3天', '7天', '15天', '30天']);
});
