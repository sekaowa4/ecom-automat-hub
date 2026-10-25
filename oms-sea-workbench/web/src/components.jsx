import React, { useRef, useState } from "react";
import {
  UploadCloud,
  FileSpreadsheet,
  Check,
  CircleAlert,
  Download,
  ExternalLink,
  Play,
  Pause,
  ChevronRight,
  LoaderCircle,
  Paperclip,
  ArrowUpRight,
} from "lucide-react";
import { count, money, date, statusNames, api } from "./api";

export function Upload({ job, busy, onUpload }) {
  const input = useRef(null);
  const [drag, setDrag] = useState(false);
  function select(file) {
    if (file) onUpload(file);
  }
  return (
    <section
      className={`upload-panel ${job ? "compact" : ""}`}
      aria-label="上传清单"
    >
      <input
        ref={input}
        type="file"
        accept=".xlsx,.csv"
        onChange={(e) => {
          select(e.target.files[0]);
          e.target.value = "";
        }}
        aria-label="装箱清单文件"
        className="visually-hidden"
        disabled={busy}
      />
      {job ? (
        <div className="filebar">
          <div className="file-symbol">
            <FileSpreadsheet size={25} />
          </div>
          <div>
            <strong>{job.source_name}</strong>
            <p>
              {job.manifest.sheet} · 已读取{" "}
              {count(job.manifest.totals.source_rows)} 行 · 重复 SKU 已合并{" "}
              {job.manifest.totals.duplicates} 行
            </p>
          </div>
          <button
            className="button secondary small"
            disabled={busy}
            onClick={() => input.current.click()}
          >
            更换清单
          </button>
        </div>
      ) : (
        <div
          className={`dropzone ${drag ? "drag" : ""}`}
          onDragOver={(e) => {
            e.preventDefault();
            setDrag(true);
          }}
          onDragLeave={() => setDrag(false)}
          onDrop={(e) => {
            e.preventDefault();
            setDrag(false);
            if (!busy) select(e.dataTransfer.files[0]);
          }}
        >
          {busy ? (
            <LoaderCircle className="spin upload-icon" />
          ) : (
            <UploadCloud className="upload-icon" strokeWidth={1.6} />
          )}
          <h2>{busy ? "正在读取清单…" : "拖入装箱清单，或点击选择"}</h2>
          <p>.xlsx / .csv · 最大 50 MB</p>
          <button
            className="button primary"
            disabled={busy}
            onClick={() => input.current.click()}
          >
            选择清单
          </button>
        </div>
      )}
    </section>
  );
}

const groups = [
  ["核对商品与新品录入", ["catalog", "products"]],
  ["更新 OMS 尺寸重量", ["measurements"]],
  ["创建入库单", ["inbound"]],
  ["提交入库并发货", ["submit", "ship"]],
  ["创建手工订单", ["invoice_order"]],
  ["填写税务信息并开票", ["tax", "issue"]],
  ["下载发票与回传附件", ["documents", "attachments"]],
];
export function Timeline({ job }) {
  return (
    <aside className="timeline-panel panel">
      <h2>执行流程</h2>
      <ol>
        {groups.map(([label, ids], index) => {
          const steps = job?.steps.filter((s) => ids.includes(s.id)) || [];
          const done = steps.length && steps.every((s) => s.status === "done");
          const active = steps.find((s) =>
            ["running", "blocked"].includes(s.status),
          );
          const gate = job?.gate && ids.includes(job.gate.step);
          const name = done
            ? "已完成"
            : gate
              ? "等待你的确认"
              : active?.status === "blocked"
                ? "需要处理"
                : active
                  ? job?.status === "paused"
                    ? "已暂停"
                    : job?.status === "pausing"
                      ? "正在暂停"
                      : "正在处理"
                  : "等待开始";
          return (
            <li
              key={label}
              className={
                done ? "done" : gate ? "waiting" : active ? "active" : ""
              }
            >
              <span className="step-number">
                {done ? <Check size={18} /> : index + 1}
              </span>
              <div>
                <strong>{label}</strong>
                <p>{name}</p>
                {active?.message && <small>{active.message}</small>}
              </div>
            </li>
          );
        })}
      </ol>
    </aside>
  );
}

export function Items({ job }) {
  const [filter, setFilter] = useState("");
  const items = job?.manifest.items || [];
  const visible = items.filter((i) =>
    `${i.sku} ${i.name_pt} ${i.name_zh}`
      .toLowerCase()
      .includes(filter.toLowerCase()),
  );
  return (
    <section className="panel products-panel">
      <div className="section-head">
        <h2>商品清单</h2>
        {job && (
          <div className="table-actions">
            <input
              aria-label="搜索 SKU 或名称"
              placeholder="搜索 SKU 或名称"
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
            />
            <a
              className="icon-link"
              title="下载核对清单"
              href={`/api/jobs/${job.id}/preview.csv`}
            >
              <Download size={17} />
            </a>
          </div>
        )}
      </div>
      <div className="table-scroll">
        <table>
          <thead>
            <tr>
              <th>SKU</th>
              <th>商品名称</th>
              <th className="numeric">件数</th>
              <th className="numeric">开票单价</th>
              <th>单套尺寸</th>
              <th className="numeric">重量</th>
            </tr>
          </thead>
          <tbody>
            {visible.map((item) => (
              <tr key={item.sku}>
                <td>
                  <div className="sku-cell">
                    {item.image && (
                      <img
                        alt=""
                        src={`/api/jobs/${job.id}/images/${item.image}`}
                      />
                    )}
                    <span>
                      {item.sku}
                      <small>
                        {item.source_rows.length > 1
                          ? `合并第 ${item.source_rows.join("、")} 行`
                          : `第 ${item.source_rows[0]} 行`}
                      </small>
                    </span>
                  </div>
                </td>
                <td>
                  <div
                    className="product-name"
                    title={item.name_pt || item.name_zh}
                  >
                    {item.name_pt || item.name_zh || "名称待补充"}
                  </div>
                  {item.name_pt && <small>{item.name_zh}</small>}
                </td>
                <td className="numeric">{count(item.quantity)}</td>
                <td className="numeric">{money(item.unit_price)}</td>
                <td className="measure">
                  {item.length_cm} × {item.width_cm} × {item.height_cm}
                  <small>cm</small>
                </td>
                <td className="numeric">
                  {count(item.weight_g)}
                  <small>g</small>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {!items.length ? (
          <div className="empty-table">
            <FileSpreadsheet size={44} strokeWidth={1.5} />
            <p>上传清单后显示商品明细</p>
          </div>
        ) : (
          !visible.length && (
            <div className="empty-table">
              <p>未找到匹配的商品</p>
            </div>
          )
        )}
      </div>
      {job && (
        <div className="column-note">
          数量 {job.manifest.columns.quantity} 列 · 单价{" "}
          {job.manifest.columns.unit_price || "未识别"} 列 · 尺寸{" "}
          {job.manifest.columns.length_cm}:{job.manifest.columns.height_cm} 列 ·
          重量统一为 g
        </div>
      )}
    </section>
  );
}

export function Summary({ job }) {
  const { totals } = job.manifest;
  return (
    <div className="summary">
      <div>
        <span>本批 SKU</span>
        <strong>
          {count(totals.skus)}
          <small>款</small>
        </strong>
      </div>
      <div>
        <span>总件数</span>
        <strong>
          {count(totals.quantity)}
          <small>件</small>
        </strong>
      </div>
      <div>
        <span>开票金额</span>
        <strong className="amount">{money(totals.amount)}</strong>
      </div>
      <div>
        <span>当前状态</span>
        <strong className={`job-status ${job.status}`}>
          {statusNames[job.status] || job.status}
        </strong>
      </div>
    </div>
  );
}

export function Issues({ job }) {
  if (!job) return null;
  return (
    <>
      {job.manifest.errors.length > 0 && (
        <div className="notice error">
          <CircleAlert size={19} />
          <div>
            <strong>清单需要修正后重新上传</strong>
            <ul>
              {job.manifest.errors.map((e, i) => (
                <li key={i}>{e}</li>
              ))}
            </ul>
          </div>
        </div>
      )}
      {job.manifest.warnings.length > 0 && (
        <details className="notice warning">
          <summary>有 {job.manifest.warnings.length} 项资料提示</summary>
          <ul>
            {job.manifest.warnings.map((w, i) => (
              <li key={i}>{w}</li>
            ))}
          </ul>
        </details>
      )}
    </>
  );
}

export function Confirmation({ job, busy, onAction }) {
  if (!job?.gate) return null;
  return (
    <section className="confirmation" aria-live="polite">
      <div className="confirmation-icon">
        <CircleAlert size={24} />
      </div>
      <div className="confirmation-copy">
        <h2>{job.gate.title}</h2>
        <p>核对本批信息后继续执行。</p>
        <ul>
          {job.gate.details.map((detail, index) => (
            <li key={index}>{detail}</li>
          ))}
        </ul>
      </div>
      <button
        className="button primary"
        disabled={busy}
        onClick={() => onAction("approve", { id: job.gate.id })}
      >
        确认并继续
        <ChevronRight size={17} />
      </button>
    </section>
  );
}

export function Logs({ job, busy, onAction, onConnect, configured, showEntries = true }) {
  const active = ["running", "waiting_confirmation", "pausing"].includes(
    job?.status,
  );
  const ready =
    job && !["completed", "invalid"].includes(job.status) && !active;
  return (
    <section className={`panel logs-panel ${showEntries ? "" : "actions-only"}`}>
      {showEntries && <div className="section-head">
        <h2>运行记录</h2>
        {job && <span className="muted">进度自动保存在本机</span>}
      </div>}
      <div className="log-and-action">
        {showEntries && <div className="log-list" aria-live="polite">
          {job?.logs.length ? (
            job.logs.slice(-12).map((l, i) => (
              <div className={`log-row ${l.level}`} key={`${i}-${l.time}`}>
                <time>
                  {new Date(l.time).toLocaleTimeString("zh-CN", {
                    hour12: false,
                  })}
                </time>
                <span>{l.message}</span>
              </div>
            ))
          ) : (
            <p>准备好后，点击开始。</p>
          )}
        </div>}
        <div className="run-action">
          {active ? (
            <button
              className="button secondary"
              disabled={busy || job.status === "pausing"}
              onClick={() => onAction("pause", {})}
            >
              <Pause size={18} />
              {job.status === "pausing" ? "正在安全暂停…" : "暂停处理"}
            </button>
          ) : (
            <button
              className="button primary"
              disabled={!ready || busy}
              onClick={() => (configured ? onAction("start", {}) : onConnect())}
            >
              <Play size={18} />
              {["blocked", "paused"].includes(job?.status)
                ? "继续处理"
                : "开始处理"}
            </button>
          )}
          {ready && !configured && (
            <button className="text-button" onClick={onConnect}>
              先连接已登录的浏览器
              <ArrowUpRight size={13} />
            </button>
          )}
        </div>
      </div>
    </section>
  );
}

export function Results({ job }) {
  if (
    !job ||
    (!job.files.length &&
      !Object.keys(job.records).some((k) => k.endsWith("_no")))
  )
    return null;
  return (
    <section className="panel results-panel">
      <h2>本批结果</h2>
      <div className="references">
        {[
          ["inbound_no", "OMS 入库单"],
          ["manual_order_no", "UpSeller 订单"],
          ["invoice_no", "发票号"],
        ].map(
          ([key, title]) =>
            job.records[key] && (
              <div key={key}>
                <span>{title}</span>
                <strong>{job.records[key]}</strong>
              </div>
            ),
        )}
      </div>
      <div className="downloads">
        {job.files.map((file) => (
          <a
            key={file}
            href={`/api/jobs/${job.id}/artifacts/${encodeURIComponent(file)}`}
          >
            <Paperclip size={17} />
            {file}
            <Download size={15} />
          </a>
        ))}
      </div>
    </section>
  );
}

export function ReferenceForm({ job, onAction, busy }) {
  const [values, setValues] = useState({});
  if (!job || !["blocked", "paused"].includes(job.status) || !job.inflight)
    return null;
  return (
    <details className="panel reference-form">
      <summary>上一步结果待核对：补充已生成的单号</summary>
      <p>
        在原平台核对后填写。程序继续时会复核单据，已完成的记录不会重新创建。
      </p>
      <div>
        {[
          ["inbound_no", "OMS 入库单号"],
          ["manual_order_no", "UpSeller 手工订单号"],
          ["invoice_no", "发票号"],
        ].map(([key, label]) => (
          <label key={key}>
            {label}
            <input
              value={values[key] ?? job.records[key] ?? ""}
              onChange={(e) =>
                setValues((v) => ({ ...v, [key]: e.target.value }))
              }
            />
          </label>
        ))}
        <button
          className="button secondary"
          disabled={busy}
          onClick={() => onAction("references", values)}
        >
          保存核对结果
        </button>
      </div>
    </details>
  );
}

export function History({ jobs, onSelect }) {
  return (
    <section className="panel history-panel">
      <div className="section-head">
        <h2>批次记录</h2>
        <span className="muted">共 {jobs.length} 批</span>
      </div>
      {!jobs.length ? (
        <div className="empty-table">
          <FileSpreadsheet size={40} />
          <p>上传第一份清单后，执行记录会保存在这里。</p>
        </div>
      ) : (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>批次</th>
                <th>创建时间</th>
                <th className="numeric">SKU / 件数</th>
                <th>入库单号</th>
                <th>状态</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {jobs.map((j) => (
                <tr key={j.id}>
                  <td>
                    <strong>{j.manifest.batch_name}</strong>
                    <small>{j.source_name}</small>
                  </td>
                  <td>{date(j.created_at)}</td>
                  <td className="numeric">
                    {j.manifest.totals.skus} /{" "}
                    {count(j.manifest.totals.quantity)}
                  </td>
                  <td>{j.records.inbound_no || "—"}</td>
                  <td>
                    <span className={`state-dot ${j.status}`} />
                    {statusNames[j.status]}
                  </td>
                  <td>
                    <button
                      className="text-button"
                      onClick={() => onSelect(j.id)}
                    >
                      查看
                      <ChevronRight size={16} />
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

export function Connections({ browser, settings, refresh, setError }) {
  const [loading, setLoading] = useState(false);
  const [selection, setSelection] = useState(settings);
  const [saved, setSaved] = useState(false);
  async function connect() {
    setLoading(true);
    setSaved(false);
    setError("");
    try {
      await api("/browser/connect", {});
      await refresh();
    } catch (e) {
      setError(e.message);
    } finally {
      setLoading(false);
    }
  }
  async function save() {
    setLoading(true);
    try {
      await api("/settings", selection);
      setSaved(true);
      await refresh();
    } catch (e) {
      setError(e.message);
    } finally {
      setLoading(false);
    }
  }
  return (
    <section className="connection-content">
      <div className="panel connection-panel">
        <div className="section-head">
          <div>
            <h2>复用专用 Chrome</h2>
            <p>
              打开并登录常用 UpSeller、开票 UpSeller 和
              OMS，再连接已打开的标签页。
            </p>
          </div>
          <button
            className="button primary"
            disabled={loading}
            onClick={connect}
          >
            {loading ? (
              <LoaderCircle className="spin" size={17} />
            ) : (
              <ExternalLink size={17} />
            )}{" "}
            {loading ? "正在连接…" : "连接浏览器"}
          </button>
        </div>
        <div className={`connection-state ${browser.ok ? "connected" : ""}`}>
          <span className={`state-dot ${browser.ok ? "completed" : ""}`} />
          {browser.ok
            ? `已读取 ${browser.pages.length} 个业务标签页`
            : browser.error || "等待连接专用 Chrome"}
        </div>
        <div className="account-selectors">
          {[
            ["common_page", "常用 UpSeller 账号", "upseller"],
            ["invoice_page", "开票专用 UpSeller 账号", "upseller"],
            ["oms_page", "OMS 账号", "oms"],
          ].map(([key, label, site], i) => (
            <label key={key}>
              <span>
                <b>0{i + 1}</b>
                {label}
              </span>
              <select
                value={selection[key] ?? ""}
                onChange={(e) => {
                  setSelection((s) => ({
                    ...s,
                    [key]:
                      e.target.value === "" ? null : Number(e.target.value),
                  }));
                  setSaved(false);
                }}
              >
                <option value="">选择已登录的标签页</option>
                {browser.pages
                  .filter((p) => p.site === site)
                  .map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.account || "账号待核对"} · {p.title} · 标签页{" "}
                      {p.page_id ?? p.id}
                    </option>
                  ))}
              </select>
              <small>请确认该标签页使用的是正确账号。</small>
            </label>
          ))}
        </div>
        <div className="connection-footer">
          <span>首次连接或 Chrome 重启后需重新读取标签页。</span>
          <button
            className="button primary"
            disabled={loading || !browser.ok}
            onClick={save}
          >
            {saved ? (
              <>
                <Check size={18} />
                已保存
              </>
            ) : (
              "保存连接设置"
            )}
          </button>
        </div>
      </div>
      <div className="connection-help">
        <h3>连接需要什么</h3>
        <p>
          沿用当前专用 Chrome 调试通道。若提示未找到调试连接，请在专用 Chrome
          打开 <code>chrome://inspect/#remote-debugging</code>
          ，启用远程调试后点击“连接浏览器”。
        </p>
        <p>
          也可以双击项目里的“启动OMS专用Chrome.cmd”使用持久专用窗口。登录资料保存在浏览器自己的资料目录中。
        </p>
        <p>
          工作台连接失败会显示原因，等待你处理后再重试。页面发生变化时，执行会停在对应步骤并保留进度。
        </p>
      </div>
    </section>
  );
}
