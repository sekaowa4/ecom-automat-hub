const state = { phase: "idle", logs: [] };
const $ = (selector) => document.querySelector(selector);

function updateClock() {
  const now = new Date();
  const date = new Intl.DateTimeFormat("zh-CN", { timeZone: "Asia/Shanghai", year: "numeric", month: "2-digit", day: "2-digit" }).format(now).replaceAll("/", "-");
  const time = new Intl.DateTimeFormat("zh-CN", { timeZone: "Asia/Shanghai", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false }).format(now);
  $("#clock").textContent = time;
  $("#brazilClock").textContent = new Intl.DateTimeFormat("pt-BR", { timeZone: "America/Sao_Paulo", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false }).format(now);
  $("#dataDate").textContent = date;
}

function addLog(message, tone = "") {
  state.logs.unshift({ time: $("#clock").textContent, message, tone });
  $("#runLog").innerHTML = state.logs.map((log) => `<div class="log-item"><strong>${log.message}</strong><span>${log.time}</span></div>`).join("");
}

function showView(view) {
  document.querySelectorAll(".view").forEach((node) => node.classList.add("hidden"));
  $(`#${view}View`).classList.remove("hidden");
  document.querySelectorAll(".nav-item").forEach((item) => item.classList.toggle("active", item.dataset.view === view));
  $("#pageTitle").textContent = view === "preview" ? "更新预览" : view === "logs" ? "运行日志" : "今日库存更新";
}

function renderSources(job) {
  const steps = job?.steps || [];
  $("#sourceRows").innerHTML = steps.map((step) => {
    const statusText = step.status === "completed" ? "导出完成" : step.status === "running" ? "正在导出" : step.status === "failed" ? `失败：${step.error || "未知错误"}` : "等待处理";
    const icon = step.status === "completed" ? "✓" : step.status === "running" ? "…" : step.status === "failed" ? "!" : "—";
    return `<div class="source-row"><div><strong>${step.filename}</strong><small>${statusText}</small></div><span class="state">${icon}</span></div>`;
  }).join("");
}

function renderInventoryPreview(preview) {
  if (!preview) return;
  const stats = document.querySelectorAll(".preview-stats strong");
  if (stats[0]) stats[0].textContent = preview.targetRows ?? 0;
  if (stats[1]) stats[1].textContent = preview.priorityReplenishment ?? 0;
  if (stats[2]) stats[2].textContent = preview.unmatchedCount ?? 0;
  if (stats[3]) stats[3].textContent = preview.dataTime || "—";
  const confirmButton = $("#confirmWriteback");
  if (confirmButton) {
    confirmButton.disabled = preview.canConfirm !== true;
    confirmButton.title = preview.canConfirm === true ? "可以确认回写" : "存在异常，处理异常后才能回写";
  }
  const warningBox = $("#previewWarnings");
  if (warningBox) {
    const items = preview.unmatched || [];
    warningBox.classList.toggle("hidden", items.length === 0);
    warningBox.innerHTML = items.length === 0
      ? ""
      : `<strong>异常产品（${items.length}）</strong><ul>${items.map((item) => `<li>第 ${item.row || ""} 行：${item.product || "未命名产品"}</li>`).join("")}</ul>`;
  }
}

function loadPreview(preview = null, workbookUrl = "") {
  const rows = preview?.rows || [];
  const toneFor = (status) => status === "紧急" ? "urgent" : status === "重要" ? "important" : "normal";
  $("#previewTable").innerHTML = rows.map((row) => `<tr><td>${row.sku}</td><td>${row.name}</td><td>${row.stock}</td><td>${typeof row.days === "number" ? `${row.days} 天` : row.days}</td><td><span class="status ${toneFor(row.status)}">${row.status || "—"}</span></td><td class="recommendation">${row.advice}${row.purchaseQty > 0 ? `（${row.purchaseQty} 件）` : ""}</td></tr>`).join("");
  $("#previewSkuCount").textContent = preview?.targetRows ?? 0;
  $("#previewState").textContent = "待确认回写";
  $("#previewState").className = "run-state warning-state";
  $("#previewDescription").textContent = "以下是本次运行计算出的变化，确认后才会写入企业微信。";
  $("#previewEmpty").classList.add("hidden");
  $("#previewTableWrap").classList.remove("hidden");
  $("#cancelPreview").disabled = false;
  const downloadLink = $("#previewWorkbookDownload");
  downloadLink.classList.toggle("hidden", !workbookUrl);
  if (workbookUrl) downloadLink.href = workbookUrl;
  renderInventoryPreview(preview);
  showView("preview");
}

function renderPortalSummary(summary) {
  const rows = document.querySelectorAll(".connection-row .row-status");
  const portals = Object.fromEntries((summary?.portals || []).map((portal) => [portal.name, portal]));
  const upsellerReady = portals.UpSeller?.status === "ready_for_export";
  if (rows[0]) {
    rows[0].textContent = upsellerReady ? "可抓取" : "待登录";
    rows[0].className = `row-status ${upsellerReady ? "ready" : "pending"}`;
  }
  if (rows[2]) {
    rows[2].textContent = "已配置";
    rows[2].className = "row-status ready";
  }
}

async function exportSource(source) {
  const response = await fetch("/api/export", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ source }),
  });
  const payload = await response.json();
  if (!response.ok || !payload.ok) throw new Error(payload.error || `${source} 导出失败`);
  return payload.export;
}

async function pollExportJob(jobId) {
  while (true) {
    const response = await fetch(`/api/export-status?id=${encodeURIComponent(jobId)}`);
    const payload = await response.json();
    if (!response.ok || !payload.ok) throw new Error(payload.error || "批量导出状态读取失败");
    const job = payload.job;
    renderSources(job);
    const completed = job.steps.filter((step) => step.status === "completed").length;
    const percent = Math.round((completed / job.steps.length) * 100);
    $("#progressPercent").textContent = `${percent}%`;
    $("#progressBar").style.width = `${Math.max(percent, 5)}%`;
    const active = job.steps[job.currentIndex];
    $("#progressHint").textContent = active ? `${active.filename}：${active.status === "running" ? "正在处理" : "等待处理"}` : "正在收尾";
    if (job.status === "completed") return job;
    if (job.status === "failed") throw new Error(job.error || job.steps.find((step) => step.status === "failed")?.error || "批量导出失败");
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
}

async function startRun() {
  if (state.phase === "fetching") return;
  state.phase = "fetching";
  $("#startRun").disabled = true;
  $("#startRun").innerHTML = "正在自动抓取…";
  $("#runState").textContent = "正在运行";
  $("#runState").className = "run-state warning-state";
  $("#progressPanel").classList.remove("hidden");
  addLog("开始连接 UpSeller 接口");
  $("#progressTitle").textContent = "正在通过 UpSeller 接口导出 6 个文件";
  $("#progressHint").textContent = "使用本机已配置的 us_u，不启动专用 Chrome";
  $("#progressPercent").textContent = "0%";
  $("#progressBar").style.width = "0%";
  try {
    const createResponse = await fetch("/api/export-all", { method: "POST" });
    const createPayload = await createResponse.json();
    if (!createResponse.ok || !createPayload.ok) throw new Error(createPayload.error || "批量导出任务创建失败");
    const exportJob = await pollExportJob(createPayload.job.job_id);
    addLog(`6 个导出文件已完成：${exportJob.steps.map((step) => step.filename).join("、")}`);
    $("#progressTitle").textContent = "正在生成库存预览";
    $("#progressHint").textContent = "校验 SKU、库存、销售周期和异常数据";
    $("#progressPercent").textContent = "100%";
    $("#progressBar").style.width = "100%";
    const inventoryResponse = await fetch("/api/inventory-preview-copy", { method: "POST" });
    const inventoryPayload = await inventoryResponse.json();
    if (!inventoryResponse.ok || !inventoryPayload.ok) {
      throw new Error(inventoryPayload.error || "库存预览生成失败");
    }
    state.phase = "preview";
    loadPreview(inventoryPayload.preview, inventoryPayload.workbook_url);
  } catch (error) {
    state.phase = "idle";
    $("#runState").textContent = "连接阻断";
    $("#runState").className = "run-state warning-state";
    $("#progressTitle").textContent = "浏览器预览未完成";
    $("#progressHint").textContent = error.message;
    $("#progressPercent").textContent = "阻断";
    $("#progressBar").style.width = "100%";
    addLog(`浏览器预览失败：${error.message}`);
    $("#startRun").disabled = false;
    $("#startRun").innerHTML = '<span class="play">▶</span>重新检查连接';
  }
}

function cancelPreview() {
  state.phase = "idle";
  $("#progressPanel").classList.add("hidden");
  $("#startRun").disabled = false;
  $("#startRun").innerHTML = '<span class="play">▶</span>开始今日库存更新';
  $("#runState").textContent = "准备就绪";
  $("#runState").className = "run-state";
  addLog("用户取消本次更新");
  showView("overview");
}

function confirmWriteback() {
  state.phase = "written";
  addLog("预览已确认，模拟回写成功");
  $("#runState").textContent = "回写完成";
  $("#runState").className = "run-state";
  showView("overview");
  $("#progressPanel").classList.add("hidden");
  $("#startRun").disabled = false;
  $("#startRun").innerHTML = '<span class="play">▶</span>开始下一次更新';
  window.alert("预览已确认。当前为 HTML 原型，未修改真实企业微信数据。");
}

async function loadWecomStatus() {
  const response = await fetch("/api/wecom-status");
  const payload = await response.json();
  const connected = response.ok && payload.ok && payload.wecom?.configured;
  $("#wecomSourceStatus").textContent = connected ? "已接通" : "待配置";
  $("#wecomSourceStatus").className = `row-status ${connected ? "ready" : "pending"}`;
  $("#wecomSourceDetail").textContent = connected ? "在线表格 · 已授权读取与写回" : "在线表格 · 等待本机授权";
  $("#wecomConnectionState").textContent = connected ? "已接通" : "待配置";
  $("#wecomConnectionState").className = `row-status ${connected ? "ready" : "pending"}`;
  $("#wecomDescription").textContent = connected
    ? `已接通：${payload.wecom.endpointHint}。确认回写前仍会先生成预览。`
    : "请粘贴机器人 MCP 地址和 Stock 在线表格链接，地址只保存于本机。";
  if (connected) $("#wecomSheetUrl").value = payload.wecom.sheetUrl || "";
}

async function saveWecomConfig() {
  const mcpUrl = $("#wecomMcpUrl").value.trim();
  const sheetUrl = $("#wecomSheetUrl").value.trim();
  if (!mcpUrl || !sheetUrl) {
    window.alert("请填写机器人 MCP 地址和 Stock 在线表格链接。");
    return;
  }
  const button = $("#saveWecomConfig");
  button.disabled = true;
  button.textContent = "正在校验…";
  try {
    const response = await fetch("/api/wecom-config", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ mcp_url: mcpUrl, sheet_url: sheetUrl }) });
    const payload = await response.json();
    if (!response.ok || !payload.ok) throw new Error(payload.error || "企业微信配置失败");
    $("#wecomMcpUrl").value = "";
    await loadWecomStatus();
    addLog("企业微信在线表格连接已保存并通过本机校验");
  } catch (error) {
    window.alert(error.message);
  } finally {
    button.disabled = false;
    button.textContent = "保存并校验连接";
  }
}

async function createDownload(buttonId, endpoint, linkId, label) {
  const button = $(buttonId);
  button.disabled = true;
  button.textContent = "正在生成…";
  try {
    const response = await fetch(endpoint, { method: "POST" });
    const payload = await response.json();
    if (!response.ok || !payload.ok) throw new Error(payload.error || "文件生成失败");
    const link = $(linkId);
    const details = payload.export || payload;
    link.href = details.workbook_url;
    link.classList.remove("hidden");
    link.textContent = `${label}（${details.rows || ""} 行）`;
    addLog(`${label}已生成`);
  } catch (error) {
    window.alert(error.message);
  } finally {
    button.disabled = false;
    button.textContent = buttonId === "#exportWecomSnapshot" ? "导出在线表格快照" : "生成更新预览 XLSX";
  }
}

$("#startRun").addEventListener("click", startRun);
$("#cancelPreview").addEventListener("click", cancelPreview);
$("#confirmWriteback").addEventListener("click", confirmWriteback);
$("#clearLog").addEventListener("click", () => { state.logs = []; $("#runLog").innerHTML = '<div class="empty-log">暂无新的运行记录</div>'; });
$("#saveWecomConfig").addEventListener("click", saveWecomConfig);
$("#exportWecomSnapshot").addEventListener("click", () => createDownload("#exportWecomSnapshot", "/api/wecom-export", "#wecomSnapshotDownload", "下载在线表格导出文件"));
document.querySelectorAll(".nav-item").forEach((item) => item.addEventListener("click", () => showView(item.dataset.view)));
updateClock();
loadWecomStatus();
setInterval(updateClock, 1000);
