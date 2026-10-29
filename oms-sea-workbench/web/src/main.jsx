import React, { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  Package, FileText, History as HistoryIcon, Settings, CircleAlert, X, Plus,
  CheckCircle2, ChevronDown, ChevronUp, Search, ShieldCheck,
} from "lucide-react";
import { api, count, money, statusNames } from "./api";
import {
  Upload, Timeline, Items, Summary, Issues, Confirmation, Logs, Results,
  ReferenceForm, History, Connections,
} from "./components";
import "./style.css";

const runningStatuses = ["running", "waiting_confirmation", "pausing", "matching"];

function BatchDetails({ job, wecom, busy, uploadBusy, onUpload, onAction, onConnect, configured }) {
  const [detailTab, setDetailTab] = useState("goods");
  const [priceSheet, setPriceSheet] = useState(job.price_sheet || '');
  useEffect(() => setPriceSheet(job.price_sheet || ''), [job.price_sheet]);
  const panelId = `batch-panel-${job.id}`;
  return (
    <div className="batch-detail" id={`batch-detail-${job.id}`}>
      <Upload job={job} busy={uploadBusy} onUpload={onUpload} />
      <Summary job={job} />
      {job.status !== 'completed' && !runningStatuses.includes(job.status) && !job.inflight && !Object.keys(job.records).length && !job.steps.some(s => s.status === 'done') && (
        <div className="panel">
          <label>正清表对应批次单价子表
            <select className="wecom-url" aria-label="正清表对应批次单价子表" value={priceSheet} onChange={e => setPriceSheet(e.target.value)}>
              <option value="">SKU 集合完全一致时自动匹配；否则请选择对应子表</option>
              {(wecom?.price_sheets || []).map(title => <option key={title} value={title}>{title}</option>)}
            </select>
          </label>
          {priceSheet !== (job.price_sheet || '') && <p>已选择新的单价子表，请先点击匹配；匹配成功前不能开始。</p>}
          <button className="button" disabled={busy} onClick={() => onAction('match-wecom', {price_sheet: priceSheet})}>匹配企微资料与批次单价</button>
        </div>
      )}
      <Timeline job={job} />
      <Issues job={job} />
      <Confirmation job={job} busy={busy} onAction={onAction} />
      <ReferenceForm job={job} busy={busy} onAction={onAction} />
      <div className="detail-tabs" role="tablist" aria-label="批次详情">
        {[["goods", "商品明细"], ["logs", "运行记录"], ["files", "文件与单号"]].map(([key, label]) => (
          <button key={key} role="tab" id={`${panelId}-${key}`}
            aria-selected={detailTab === key} aria-controls={panelId}
            onClick={() => setDetailTab(key)}>
            {label}{key === "goods" && <small>{job.manifest.totals.skus}</small>}
          </button>
        ))}
      </div>
      <div role="tabpanel" id={panelId} aria-labelledby={`${panelId}-${detailTab}`}>
        {detailTab === "goods" && <Items job={job} />}
        {detailTab === "files" && (
          <>
            <Results job={job} />
            {!job.files.length && !Object.keys(job.records).some((k) => k.endsWith("_no")) && (
              <div className="empty-results">尚未生成单据或附件。</div>
            )}
          </>
        )}
        <Logs job={job} busy={busy || priceSheet !== (job.price_sheet || '')} onAction={onAction} onConnect={onConnect}
          configured={configured} showEntries={detailTab === "logs"} />
      </div>
      {job.status === "completed" && (
        <div className="complete-banner">
          <CheckCircle2 size={18} />
          {job.files.length
            ? "本批次已完成，文件与单号可在“文件与单号”中查看。"
            : "本批次已有完成记录，不会重复建单；历史文件保留在原下载目录。"}
        </div>
      )}
    </div>
  );
}

function App() {
  const [state, setState] = useState({
    jobs: [], settings: {}, browser: { ok: false, pages: [] },
  });
  const [selected, setSelected] = useState(() => localStorage.getItem("oms-current-batch"));
  const [tab, setTab] = useState("current");
  const [showUpload, setShowUpload] = useState(false);
  const [batchFilter, setBatchFilter] = useState("all");
  const [search, setSearch] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [online, setOnline] = useState(true);
  const activeJob = state.jobs.find((job) => runningStatuses.includes(job.status));
  const configured = state.browser.ok &&
    ["common_page", "invoice_page", "oms_page"].every((key) =>
      state.settings[key] != null && state.browser.pages.some((page) => page.id === state.settings[key]));

  async function refresh() {
    try {
      const next = await api("/state");
      setState(next);
      setOnline(true);
      return next;
    } catch {
      setOnline(false);
    }
  }
  useEffect(() => {
    refresh();
    const timer = setInterval(refresh, 1600);
    return () => clearInterval(timer);
  }, []);
  useEffect(() => {
    if (selected) localStorage.setItem("oms-current-batch", selected);
    else localStorage.removeItem("oms-current-batch");
  }, [selected]);

  async function upload(file) {
    if (!/\.(xlsx|csv)$/i.test(file.name)) return setError("请上传 .xlsx 或 .csv 格式的清单");
    if (file.size > 50_000_000) return setError("清单超过 50 MB，请调整文件后重试");
    if (activeJob) return setError("请先暂停当前批次，再更换清单");
    setError("");
    setBusy(true);
    try {
      const job = await api(`/jobs?filename=${encodeURIComponent(file.name)}`, file, true);
      await refresh();
      setSelected(job.id);
      setShowUpload(false);
      setBatchFilter("all");
      setSearch("");
      setTab("current");
    } catch (e) {
      setError(e.message);
    } finally {
      setBusy(false);
    }
  }
  async function action(jobId, name, data) {
    setError("");
    setBusy(true);
    try {
      await api(`/jobs/${jobId}/${name}`, data);
      await refresh();
    } catch (e) {
      setError(e.message);
    } finally {
      setBusy(false);
    }
  }
  function openBatch(id) {
    setSelected(id);
    setTab("current");
    setSearch("");
    setBatchFilter("all");
  }
  const visibleJobs = state.jobs.filter((job) =>
    (batchFilter === "all" || (batchFilter === "completed" ? job.status === "completed" : job.status !== "completed")) &&
    `${job.manifest.batch_name} ${job.source_name} ${Object.values(job.records).filter((v) => typeof v === "string").join(" ")}`
      .toLowerCase().includes(search.toLowerCase()));
  const pending = state.jobs.filter((job) => job.status !== "completed").length;
  const titles = { current: "批次任务", history: "历史记录", settings: "连接设置" };

  return (
    <div className="app-shell">
      <nav className="sidebar" aria-label="主导航">
        <div className="brand" title="海运工作台"><Package size={26} strokeWidth={1.7} /></div>
        <div className="nav-items">
          {[["current", FileText, "当前批次"], ["history", HistoryIcon, "历史记录"], ["settings", Settings, "连接设置"]].map(([key, Icon, label]) => (
            <button key={key} className={tab === key ? "selected" : ""}
              aria-label={label} aria-current={tab === key ? "page" : undefined}
              title={label} onClick={() => setTab(key)}>
              <Icon size={21} strokeWidth={1.7} /><span>{label}</span>
            </button>
          ))}
        </div>
        <div className="sidebar-footer" title="进度保存在本机，关键操作需单独确认"><ShieldCheck size={19} /></div>
      </nav>
      <main>
        <div className="topline">
          <div className="breadcrumbs"><span>海运工作台</span><span>/</span><span>{titles[tab]}</span></div>
          <span className={`local-status ${online ? "" : "offline"}`}><i />{online ? "本机运行" : "服务已断开"}</span>
        </div>
        <div className="workspace">
          <header className="workspace-header">
            <div><h1>{titles[tab]}</h1><p>{tab === "current"
              ? "先选一批货，再展开处理。商品、进度与单据都留在本条任务里。"
              : tab === "history" ? "查看已处理的批次，或从原进度继续。" : "指定当前已经登录的业务账号标签页。"}</p></div>
            {tab === "current" && (
              <button className="button primary" disabled={busy || !!activeJob}
                onClick={() => { setShowUpload(true); setError(""); }}>
                <Plus size={16} />新建批次
              </button>
            )}
          </header>
          {!online && <div className="notice error"><CircleAlert size={19} /><span>本地服务已断开，请重新运行“启动海运工作台.cmd”。进度已保存在本机。</span></div>}
          {error && <div className="notice error" role="alert"><CircleAlert size={19} /><span>{error}</span><button className="dismiss" aria-label="关闭提示" onClick={() => setError("")}><X size={17} /></button></div>}
          {tab === "current" && (
            <>
              {(showUpload || !state.jobs.length) && (
                <section className="new-batch-panel">
                  <div className="section-head"><h2>新建海运批次</h2>{state.jobs.length > 0 && <button className="text-button" disabled={busy} onClick={() => setShowUpload(false)}>收起上传</button>}</div>
                  <Upload busy={busy || !!activeJob} onUpload={upload} />
                </section>
              )}
              <div className="batch-toolbar">
                <div className="batch-filters" aria-label="筛选批次">
                  {[["all", "全部批次", state.jobs.length], ["pending", "待处理", pending], ["completed", "已完成", state.jobs.length - pending]].map(([key, label, total]) => (
                    <button key={key} aria-pressed={batchFilter === key} onClick={() => setBatchFilter(key)}>
                      {label}<small>{total}</small>
                    </button>
                  ))}
                </div>
                <label className="batch-search"><Search size={15} /><input aria-label="搜索批次或单号" placeholder="搜索批次、文件或单号" value={search} onChange={(e) => setSearch(e.target.value)} /></label>
              </div>
              {activeJob && (selected !== activeJob.id || !visibleJobs.some((j) => j.id === activeJob.id)) && (
                <div className="active-batch-note">
                  <span>{activeJob.manifest.batch_name} · {statusNames[activeJob.status]}</span>
                  <button className="text-button" onClick={() => openBatch(activeJob.id)}>查看进行中批次</button>
                </div>
              )}
              <div className="batch-list">
                {[["待处理", visibleJobs.filter((job) => job.status !== "completed")], ["已完成", visibleJobs.filter((job) => job.status === "completed")]].map(([label, jobs]) =>
                  jobs.length > 0 && (
                    <section className="batch-group" key={label} aria-label={label + "批次"}>
                      <div className="batch-group-label"><ChevronDown size={14} />{label}<small>{jobs.length}</small></div>
                      {jobs.map((job) => {
                        const open = selected === job.id;
                        return (
                          <article className={`batch-card ${open ? "expanded" : ""}`} key={job.id}>
                            <button className="batch-toggle" aria-expanded={open}
                              aria-controls={`batch-detail-${job.id}`}
                              onClick={() => setSelected(open ? null : job.id)}>
                              <span className="batch-icon"><Package size={21} strokeWidth={1.6} /></span>
                              <span className="batch-title"><strong>{job.manifest.batch_name}</strong><small>{job.source_name}</small></span>
                              <span className="batch-count">{count(job.manifest.totals.skus)} SKU · {count(job.manifest.totals.quantity)} 件</span>
                              <span className={`batch-status ${job.status}`}>{statusNames[job.status] || job.status}</span>
                              {open ? <ChevronUp size={16} /> : <ChevronDown size={16} />}
                            </button>
                            {open && <BatchDetails key={job.id} job={job} wecom={state.wecom} busy={busy}
                              uploadBusy={busy || !!activeJob} onUpload={upload}
                              onAction={(name, data) => action(job.id, name, data)}
                              onConnect={() => setTab("settings")} configured={configured} />}
                          </article>
                        );
                      })}
                    </section>
                  ))}
                {!visibleJobs.length && state.jobs.length > 0 && <div className="empty-batches">未找到匹配的批次。</div>}
              </div>
              <div className="workspace-footnote"><span><ShieldCheck size={13} />进度保存在本机 · 关键操作逐次确认</span><span>OMS / UpSeller</span></div>
            </>
          )}
          {tab === "history" && <History jobs={state.jobs} onSelect={openBatch} />}
          {tab === "settings" && <Connections wecom={state.wecom} key={state.browser.pages.map((page) => page.id).join(",")}
            browser={state.browser} settings={state.settings} refresh={refresh} setError={setError} />}
        </div>
      </main>
    </div>
  );
}
createRoot(document.getElementById("root")).render(<App />);
