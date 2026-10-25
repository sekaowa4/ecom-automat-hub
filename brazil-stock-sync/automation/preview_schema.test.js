const assert = require('node:assert/strict');
const test = require('node:test');
const { buildPreviewSummary } = require('./preview_schema');

test('builds a preview summary with ready and blocked portal counts', () => {
  const summary = buildPreviewSummary([
    { name: 'UpSeller', status: 'ready_for_export' },
    { name: 'OMS', status: 'login_required' },
  ]);

  assert.deepEqual(summary, {
    mode: 'preview_only',
    overallStatus: 'blocked',
    readyCount: 1,
    blockedCount: 1,
    portals: [
      { name: 'UpSeller', status: 'ready_for_export' },
      { name: 'OMS', status: 'login_required' },
    ],
  });
});

test('returns ready when every portal is ready for export', () => {
  assert.equal(
    buildPreviewSummary([{ name: 'UpSeller', status: 'ready_for_export' }]).overallStatus,
    'ready',
  );
});
