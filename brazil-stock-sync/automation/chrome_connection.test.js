const test = require('node:test');
const assert = require('node:assert/strict');
const { getChromeConnectionConfig, buildChromeLaunchArgs, connectToExistingChrome } = require('./chrome_connection');

test('defaults to the existing Chrome CDP endpoint', () => {
  assert.deepEqual(getChromeConnectionConfig({}), {
    mode: 'cdp',
    endpoint: 'http://127.0.0.1:9222',
  });
});

test('allows an explicit CDP endpoint override', () => {
  assert.deepEqual(getChromeConnectionConfig({ BRAZIL_STOCK_CDP_ENDPOINT: 'http://127.0.0.1:9333' }), {
    mode: 'cdp',
    endpoint: 'http://127.0.0.1:9333',
  });
});

test('builds a dedicated Chrome launch command that opens the workbench and UpSeller login page', () => {
  const args = buildChromeLaunchArgs('D:/runtime/portal-profile-chrome');

  assert.deepEqual(args, [
    '--remote-debugging-port=9222',
    '--user-data-dir=D:/runtime/portal-profile-chrome',
    'http://127.0.0.1:8765/',
    'https://app.upseller.com/zh-CN/analytics/product-sales?warehouseId=',
  ]);
});

test('retries CDP connection while dedicated Chrome is still starting', async () => {
  let calls = 0;
  const expectedContext = {};
  const chromium = {
    connectOverCDP: async () => {
      calls += 1;
      if (calls === 1) throw new Error('starting');
      return { contexts: () => [expectedContext] };
    },
  };

  const context = await connectToExistingChrome(chromium, {}, { attempts: 2, delay: 0 });

  assert.equal(context, expectedContext);
  assert.equal(calls, 2);
});

test('allows the dedicated Chrome handshake up to thirty seconds by default', async () => {
  let timeout;
  const chromium = {
    connectOverCDP: async (_endpoint, options) => {
      timeout = options.timeout;
      return { contexts: () => [{}] };
    },
  };

  await connectToExistingChrome(chromium, {}, { attempts: 1 });

  assert.equal(timeout, 30_000);
});
