const assert = require('node:assert/strict');
const test = require('node:test');

const { redactRequest } = require('./upseller_api_probe');

test('redacts credentials while retaining API request shape', () => {
  const result = redactRequest({
    url: 'https://api.upseller.com/export?warehouseId=1',
    method: 'POST',
    headers: {
      cookie: 'us_u=private-value',
      authorization: 'Bearer private-token',
      'content-type': 'application/json',
    },
    postData: '{"range":"30d"}',
  });

  assert.deepEqual(result, {
    url: 'https://api.upseller.com/export?warehouseId=1',
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    postData: '{"range":"30d"}',
  });
});
