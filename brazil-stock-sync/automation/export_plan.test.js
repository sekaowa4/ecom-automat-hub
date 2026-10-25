const assert = require('node:assert/strict');
const test = require('node:test');
const { buildExportPlan, rangeEndingYesterday } = require('./export_plan');

const testNow = new Date('2026-08-28T04:00:00.000Z');

test('calculates Beijing ranges without including today', () => {
  assert.deepEqual(rangeEndingYesterday(15, testNow), { start: '2026-08-13', end: '2026-08-27' });
  assert.deepEqual(rangeEndingYesterday(3, testNow), { start: '2026-08-25', end: '2026-08-27' });
});

test('builds the six exports with user-facing filenames', () => {
  const plan = buildExportPlan(testNow);
  assert.deepEqual(plan.map((item) => item.filename), [
    '近30天销售数据.xlsx', '近7天销售数据.xlsx', '近15天销售数据.xlsx', '近3天销售数据.xlsx',
    'FULL库存数据.xlsx', '库存数据.xlsx',
  ]);
  assert.equal(plan[2].range.start, '2026-08-13');
  assert.equal(plan[3].range.end, '2026-08-27');
  assert.deepEqual(plan.slice(0, 4).map((item) => item.preset), ['30d', '7d', 'custom', 'custom']);
  assert.equal(plan[5].source, 'upseller');
});
