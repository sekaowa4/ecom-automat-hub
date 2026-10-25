const assert = require('node:assert/strict');
const test = require('node:test');
const {
  classifyUpSellerPage,
  classifyOmsPage,
  isAuthenticated,
  chooseExportButton,
} = require('./portal_adapter');

test('classifies the UpSeller product sales page', () => {
  assert.equal(
    classifyUpSellerPage('https://app.upseller.com/zh-CN/analytics/product-sales?warehouseId='),
    'product_sales',
  );
});

test('classifies the OMS inventory page', () => {
  assert.equal(
    classifyOmsPage('https://aplichic.jfwms.net/web/inventory/index'),
    'inventory',
  );
});

test('detects login pages without relying on a fixed button position', () => {
  assert.equal(isAuthenticated({ url: 'https://app.upseller.com/zh-CN/login', bodyText: '登录' }), false);
  assert.equal(isAuthenticated({ url: 'https://app.upseller.com/zh-CN/analytics/product-sales', bodyText: '导出 产品销量' }), true);
});

test('prefers a visible export button by accessible label', () => {
  const buttons = [
    { text: '刷新', visible: true },
    { text: '导出', visible: true },
    { text: '导出', visible: false },
  ];
  assert.deepEqual(chooseExportButton(buttons), { text: '导出', visible: true });
});
