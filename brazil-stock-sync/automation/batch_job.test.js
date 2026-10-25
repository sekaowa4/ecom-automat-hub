const assert = require('node:assert/strict');
const test = require('node:test');
const path = require('node:path');
const { buildExportPlan } = require('./export_plan');
const { createBatchState, markStep, portalExportManifest } = require('./batch_job');

test('creates six pending steps in export order', () => {
  const state = createBatchState(buildExportPlan(new Date('2026-08-28T04:00:00Z')));
  assert.equal(state.status, 'queued');
  assert.equal(state.currentIndex, 0);
  assert.deepEqual(state.steps.map((step) => step.filename), [
    '近30天销售数据.xlsx', '近7天销售数据.xlsx', '近15天销售数据.xlsx', '近3天销售数据.xlsx',
    'FULL库存数据.xlsx', '库存数据.xlsx',
  ]);
  assert.ok(state.steps.every((step) => step.status === 'pending'));
});

test('marks a step and advances the current index', () => {
  const initial = createBatchState(buildExportPlan());
  const running = markStep(initial, 0, { status: 'running' });
  const complete = markStep(running, 0, { status: 'completed', path: 'x.xlsx' });
  assert.equal(complete.steps[0].status, 'completed');
  assert.equal(complete.currentIndex, 1);
});

test('builds the exact six-file manifest from an export directory', () => {
  assert.deepEqual(portalExportManifest('runtime/portal-downloads'), [
    path.join('runtime/portal-downloads', '近30天销售数据.xlsx'),
    path.join('runtime/portal-downloads', '近7天销售数据.xlsx'),
    path.join('runtime/portal-downloads', '近15天销售数据.xlsx'),
    path.join('runtime/portal-downloads', '近3天销售数据.xlsx'),
    path.join('runtime/portal-downloads', 'FULL库存数据.xlsx'),
    path.join('runtime/portal-downloads', '库存数据.xlsx'),
  ]);
});
