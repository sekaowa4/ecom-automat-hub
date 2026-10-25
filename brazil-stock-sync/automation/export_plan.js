function formatDate(date) {
  return date.toISOString().slice(0, 10);
}

function beijingDate(date = new Date()) {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Shanghai',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(date);
  const values = Object.fromEntries(parts.filter((part) => part.type !== 'literal').map((part) => [part.type, part.value]));
  return new Date(Date.UTC(Number(values.year), Number(values.month) - 1, Number(values.day)));
}

function rangeEndingYesterday(days, now = new Date()) {
  const end = beijingDate(now);
  end.setDate(end.getDate() - 1);
  const start = new Date(end);
  start.setDate(start.getDate() - days + 1);
  return { start: formatDate(start), end: formatDate(end) };
}

function buildExportPlan(now = new Date()) {
  return [
    { id: 'sales_30d', source: 'upseller', filename: '近30天销售数据.xlsx', preset: '30d', range: rangeEndingYesterday(30, now) },
    { id: 'sales_7d', source: 'upseller', filename: '近7天销售数据.xlsx', preset: '7d', range: rangeEndingYesterday(7, now) },
    { id: 'sales_15d', source: 'upseller', filename: '近15天销售数据.xlsx', preset: 'custom', range: rangeEndingYesterday(15, now) },
    { id: 'sales_3d', source: 'upseller', filename: '近3天销售数据.xlsx', preset: 'custom', range: rangeEndingYesterday(3, now) },
    { id: 'full_inventory', source: 'upseller', filename: 'FULL库存数据.xlsx', inventory: 'full' },
    { id: 'inventory', source: 'upseller', filename: '库存数据.xlsx', inventory: 'third_party' },
  ];
}

module.exports = { beijingDate, rangeEndingYesterday, buildExportPlan };
