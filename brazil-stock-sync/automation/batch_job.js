const path = require('node:path');

function createBatchState(plan) {
  return {
    status: 'queued',
    currentIndex: 0,
    startedAt: null,
    finishedAt: null,
    error: null,
    steps: plan.map((item) => ({
      id: item.id,
      filename: item.filename,
      source: item.source,
      status: 'pending',
      path: null,
      error: null,
    })),
  };
}

function markStep(state, index, patch) {
  const steps = state.steps.map((step, stepIndex) => stepIndex === index ? { ...step, ...patch } : step);
  const currentIndex = patch.status === 'completed' || patch.status === 'failed' ? Math.min(index + 1, steps.length) : state.currentIndex;
  const status = steps.some((step) => step.status === 'failed') ? 'failed'
    : steps.every((step) => step.status === 'completed') ? 'completed'
      : steps.some((step) => step.status === 'running') ? 'running' : state.status;
  return { ...state, steps, currentIndex, status };
}

function portalExportManifest(exportDir) {
  const names = ['近30天销售数据.xlsx', '近7天销售数据.xlsx', '近15天销售数据.xlsx', '近3天销售数据.xlsx', 'FULL库存数据.xlsx', '库存数据.xlsx'];
  return names.map((name) => path.join(exportDir, name));
}

module.exports = { createBatchState, markStep, portalExportManifest };
