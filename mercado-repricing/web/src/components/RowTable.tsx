import { useState } from 'react';
import { CircleCheck, CircleAlert, CircleMinus, CircleDashed, Info, ChevronLeft, ChevronRight } from 'lucide-react';
import { rowLabels, type ValidatedRow, type JobRow } from '../api/types';
import { buildExecutionBindings, executionBindingKey } from './executionBindings';

interface TableRow { source: ValidatedRow; job?: JobRow }

export default function RowTable({ rows, jobRows, weekend = false }: { rows: ValidatedRow[]; jobRows?: JobRow[]; weekend?: boolean }) {
  const [query, setQuery] = useState('');
  const [result, setResult] = useState('all');
  const [action, setAction] = useState('all');
  const [filter, setFilter] = useState('all');
  const [page, setPage] = useState(0);
  const table: TableRow[] = jobRows ? jobRows.map(job => ({ source: job.source, job })) : rows.map(source => ({ source }));
  const executionBindings = jobRows ? buildExecutionBindings(jobRows) : null;
  const showObservation = jobRows?.some(row => row.checkpoint.startsWith('readonly') || row.observation);
  const valid = table.filter(row => !!row.source.input).length;
  const actions = [...new Set(table.map(row => row.source.input?.action || row.source.mapped?.action || (row.source.input ? '自动判断' : '—')))];
  const filtered = table.filter(row =>
    (filter === 'all' || (filter === 'valid' ? !!row.source.input : !row.source.input)) &&
    (row.source.input?.sku ?? row.source.mapped?.sku ?? row.source.original.SKU ?? row.source.original.sku ?? '').toLowerCase().includes(query.trim().toLowerCase()) &&
    (result === 'all' || (result === 'network-retry' ? row.job?.status === 'RetryPending' && !!row.job.retryReason : result === 'subsidy-income-kept' ? row.job?.checkpoint === result : row.job?.status === result)) &&
    (action === 'all' || (row.source.input?.action || row.source.mapped?.action || (row.source.input ? '自动判断' : '—')) === action));
  const last = Math.max(0, Math.ceil(filtered.length / 50) - 1);
  const current = Math.min(page, last);

  function raw(row: ValidatedRow, keys: string[]) {
    const field = keys[0] === '店铺' ? 'store' : keys[0] === '站点' ? 'site' : keys[0] === 'SKU' ? 'sku' : 'targetPrice';
    return row.mapped?.[field] ?? keys.map(key => row.original[key]).find(value => value != null) ?? '—';
  }

  return <section className="table-panel">
    <div className="table-toolbar">
      <h2>{jobRows ? '执行明细' : '数据预览'}</h2>
      <div className="filter-tabs" role="group" aria-label="数据筛选">
        {(['all', 'valid', 'invalid'] as const).map(key => {
          const label = key === 'all' ? `全部 (${table.length})` : key === 'valid' ? `有效 (${valid})` : `异常 (${table.length - valid})`;
          return <button key={key} className={filter === key ? 'selected' : ''} onClick={() => { setFilter(key); setPage(0); }}>{label}</button>;
        })}
      </div>
    </div>
    <div className="detail-filters">
      <input aria-label="搜索 SKU" placeholder="搜索 SKU" value={query} onChange={event => { setQuery(event.target.value); setPage(0); }} />
      {jobRows && <select aria-label="筛选执行结果" value={result} onChange={event => { setResult(event.target.value); setPage(0); }}>
        <option value="all">全部执行结果</option>
        {Object.entries(rowLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
        <option value="network-retry">网络问题 · 持续重试</option><option value="subsidy-income-kept">已保留高收益补贴</option>
      </select>}
      {!weekend && <select aria-label="筛选表内动作" value={action} onChange={event => { setAction(event.target.value); setPage(0); }}>
        <option value="all">全部表内动作</option>
        {actions.map(value => <option key={value} value={value}>{value}</option>)}
      </select>}
    </div>
    <div className="table-scroll">
      <table>
        <thead><tr>
          <th className="row-number">源行</th><th>店铺</th><th>站点</th><th>SKU</th>
          {jobRows && <th>活动 / 商品链接</th>}
          {!weekend && <th>表内动作</th>}
          <th className="numeric">{weekend ? '报名价格' : '目标售价'}</th>
          {showObservation && <><th className="numeric">当前售价</th><th className="numeric">目标差额</th></>}
          {jobRows && <th>处理路径</th>}
          <th>{jobRows ? '执行结果' : '检查结果'}</th>
        </tr></thead>
        <tbody>{filtered.slice(current * 50, current * 50 + 50).map(({ source, job }, index) => {
          const input = source.input;
          const ok = !!input;
          const warning = !ok || ['NeedsReview', 'Failed', 'RetryPending', 'AwaitingVerification'].includes(job?.status ?? '');
          const sku = input?.sku ?? source.mapped?.sku ?? source.original.SKU ?? source.original.sku ?? '';
          const store = input?.storeName || input?.store || '';
          const binding = executionBindings && sku ? executionBindings.get(executionBindingKey(store, sku)) : undefined;
          const itemId = job?.observation?.itemId || job?.priceCut?.product?.itemId || input?.itemId;
          const activityIds = binding?.activities.map(activity => activity.id) ?? [];
          return <tr key={`${source.sheet}-${source.sourceRow}-${index}`}>
            <td className="muted">{source.sourceRow}</td>
            <td>{input?.storeName ?? input?.store ?? raw(source, ['店铺', '店铺名称', 'store'])}</td>
            <td><span className="site-code">{input?.site ?? raw(source, ['站点', 'site'])}</span></td>
            <td className="sku">{sku || '—'}</td>
            {jobRows && <td className="execution-binding">
              <strong className={binding?.itemIds.length && binding.itemIds.length > 1 ? 'multiple-links' : ''}>
                {binding?.itemIds.length ? binding.itemIds.length > 1 ? `多个链接 · ${binding.itemIds.length}` : '商品链接' : '链接待读取'}
              </strong>
              {binding?.itemIds.length ? <small>商品 ID：{binding.itemIds.map(id => id === itemId ? `${id}（本条）` : id).join('、')}</small> : itemId && <small>商品 ID：{itemId}</small>}
              {activityIds.length > 0 ? <small>本批活动：{binding!.activities.map(activity => `${activity.name} · ${activity.id}（${activity.status}）`).join('；')}</small> : <small>活动待读取</small>}
            </td>}
            {!weekend && <td>{input?.action || source.mapped?.action || (input ? '自动判断' : '—')}</td>}
            <td className="numeric price">{input ? `${input.currency} ${input.targetPrice.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}` : raw(source, ['目标售价', '目标价格', 'target_price'])}</td>
            {showObservation && <>
              <td className="numeric">{job?.observation?.currentPrice?.toFixed(2) ?? '—'}{job?.observation && <small>商品 {job.observation.itemId}<br />原价 {job.observation.basePriceText}</small>}</td>
              <td className="numeric">{job?.observation?.difference?.toFixed(2) ?? '—'}</td>
            </>}
            {jobRows && <td>{job?.route || '—'}</td>}
            <td className="result-cell">
              <span className={`result-label ${job?.status === 'Failed' ? 'danger' : warning ? 'warning' : job?.checkpoint !== 'subsidy-income-kept' && ['Pending', 'Skipped'].includes(job?.status ?? '') ? 'muted' : 'success'}`}>
                {warning ? <CircleAlert size={16} /> : job?.status === 'Running' ? <span className="spinner" /> : job?.status === 'Pending' ? <CircleDashed size={16} aria-hidden="true" /> : job?.status === 'Skipped' && job.checkpoint !== 'subsidy-income-kept' ? <CircleMinus size={16} aria-hidden="true" /> : <CircleCheck size={16} aria-hidden="true" />}
                {' '}{job ? job.status === 'RetryPending' && job.retryReason ? '网络问题 · 持续重试' : job.checkpoint === 'subsidy-income-kept' ? '已保留高收益补贴' : rowLabels[job.status] : ok ? '有效' : source.errors.join('；')}
              </span>
              {job?.message && <small>{job.message}</small>}{job?.status === 'RetryPending' && job.retryReason && <small>{job.retryReason} · 已尝试 {job.attempts ?? 0} 次{job.retryAfter && <> · 下次重试：{new Date(job.retryAfter).toLocaleString('zh-CN')}</>}</small>}
            </td>
          </tr>;
        })}</tbody>
      </table>
      {!filtered.length && <div className="empty-table">没有符合条件的数据</div>}
    </div>
    <div className="table-foot">
      <span><Info size={15} />目标售价指商品售价，不是净收款额</span>
      <div className="pagination"><span>{filtered.length ? current * 50 + 1 : 0}–{Math.min((current + 1) * 50, filtered.length)} / {filtered.length}</span><button aria-label="上一页" disabled={!current} onClick={() => setPage(current - 1)}><ChevronLeft size={17} /></button><button aria-label="下一页" disabled={current >= last} onClick={() => setPage(current + 1)}><ChevronRight size={17} /></button></div>
    </div>
  </section>;
}
