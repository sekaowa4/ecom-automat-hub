const test = require('node:test');
const assert = require('node:assert/strict');

const { NATIVE_SELECTION_SETTLE_MS, buildBlocks, buildNativePasteCommand } = require('./online_sheet_writeback');

test('buildBlocks creates the three ordinary-sheet ranges in SKU order', () => {
  const blocks = buildBlocks(
    ['SKU-1', 'SKU-2'],
    {
      'SKU-1': { P: 1, Q: 2, Z: 3, AI: 4 },
      'SKU-2': { P: 5, Q: 6, Z: 7, AI: 8 },
    },
  );

  assert.deepEqual(blocks, [
    { range: 'P6:W7', text: '1\t2\t\t\t\t\t\t\n5\t6\t\t\t\t\t\t' },
    { range: 'Z6:AC7', text: '3\t\t\t\n7\t\t\t' },
    { range: 'AI6:AW7', text: '4\t\t\t\t\t\t\t\t\t\t\t\t\t\t\n8\t\t\t\t\t\t\t\t\t\t\t\t\t\t' },
  ]);
});

test('buildBlocks rejects an update missing from the expected SKU order', () => {
  assert.throws(
    () => buildBlocks(['SKU-1'], { 'SKU-1': { P: 1 }, 'SKU-2': { P: 2 } }),
    /unexpected SKU/,
  );
});

test('buildNativePasteCommand activates the Chrome window and uses a real grid click before Ctrl+V', () => {
  const command = buildNativePasteCommand('Stock_Order_测试', '最近更新时间：8.7');

  assert.match(command, /SetForegroundWindow/);
  assert.match(command, /SetCursorPos/);
  assert.match(command, /mouse_event/);
  assert.match(command, /SendWait\('\^v'\)/);
  assert.match(command, /MainWindowTitle -like/);
  assert.doesNotMatch(command, /ClipboardEvent/);
});

test('native paste waits for WeCom to finish applying a named range selection', () => {
  assert.ok(NATIVE_SELECTION_SETTLE_MS >= 1500);
});

test('buildNativePasteCommand keeps large TSV payloads out of the Windows command line', () => {
  const command = buildNativePasteCommand('Stock_Order_测试', 'C:\\Temp\\brazil-stock-paste.tsv');

  assert.ok(command.length < 2_000);
  assert.match(command, /Get-Content -Raw -Encoding utf8/);
});
