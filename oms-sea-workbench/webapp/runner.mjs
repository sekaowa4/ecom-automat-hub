import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import readline from "node:readline";
import crypto from "node:crypto";
import { spawn } from "node:child_process";
import { pathToFileURL } from "node:url";
import {
  connect,
  Page,
  parsePages,
  BrowserError,
  root,
  sleep,
  findUid,
} from "./browser.mjs";
import {
  uploadLocalImage,
  advanceToNext,
} from "./product_helpers.mjs";
import { omsOrigin, billingConfig } from "./config.mjs";

const emit = (event) => process.stdout.write(JSON.stringify(event) + "\n");
export const cents = (value) => {
  const text = String(value).replace(/R\$|BRL|\s/g, "");
  const normalized = text.includes(",")
    ? text.replace(/\./g, "").replace(",", ".")
    : text;
  if (!/^\d+(?:\.\d{1,2})?$/.test(normalized))
    throw new BrowserError(`金额格式无效：${text}`);
  const [whole, fraction = ""] = normalized.split(".");
  const result = Number(whole) * 100 + Number(fraction.padEnd(2, "0"));
  if (!Number.isSafeInteger(result)) throw new BrowserError('金额超过允许的精确计算范围');
  return result;
};

export function checkQuantities(actual, expected, prices = false) {
  if (
    actual.length !== expected.length ||
    new Set(actual.map((i) => i.sku)).size !== expected.length
  )
    throw new BrowserError("页面商品数量或 SKU 集合不匹配");
  for (const item of expected) {
    const found = actual.find((i) => i.sku === item.sku);
    if (
      !found ||
      Number(found.values[0].replace(/[ ,]/g, "")) !== item.quantity
    )
      throw new BrowserError(`${item.sku} 页面件数与清单不一致`);
    if (prices && cents(found.values[1]) !== cents(item.unit_price))
      throw new BrowserError(`${item.sku} 页面单价与清单不一致`);
  }
}

export function checkSavedOrder(actual, expected) {
  if (actual.length !== expected.length)
    throw new BrowserError("订单详情的 SKU 集合不完整，不能确认原单据");
  for (const item of expected) {
    const row = actual.find((r) => r.sku === item.sku);
    const quantity = row?.text.match(/[×x]\s*(\d[\d,.]*)/)?.[1];
    const price = row?.text.match(/R\$\s*([\d.,]+)/)?.[1];
    if (
      !row ||
      Number(quantity?.replace(/[,.]/g, "")) !== item.quantity ||
      !price ||
      cents(price) !== cents(item.unit_price)
    )
      throw new BrowserError(
        `${item.sku} 的已保存订单数量或单价与本批清单不一致`,
      );
  }
}

export class ApprovalController {
  constructor(send = emit) {
    this.send = send;
    this.paused = false;
    this.pending = null;
  }
  receive(message) {
    if (message.type === "pause") {
      this.paused = true;
      this.pending?.resolve(false);
      this.pending = null;
    }
    if (message.type === "approve" && this.pending?.id === message.id) {
      this.pending.resolve(true);
      this.pending = null;
    }
  }
  async gate(step, title, details) {
    if (!["submit", "ship", "issue", "attachments"].includes(step))
      throw new Error("无效确认步骤");
    if (this.paused) throw new Paused();
    const id = crypto.randomUUID();
    const promise = new Promise((resolve) => {
      this.pending = { id, resolve };
    });
    this.send({ type: "confirmation", id, step, title, details });
    if (!(await promise)) throw new Paused();
  }
}
class Paused extends Error {}

export class ShippingWorkflow {
  constructor(input, send = emit, clients = null) {
    this.input = input;
    this.send = send;
    this.manifest = input.manifest;
    this.items = this.manifest.items;
    this.records = { ...input.records };
    this.done = new Set(input.completed_steps);
    this.clients = clients || new Map();
    this.ownsClients = !clients;
    this.approvals = new ApprovalController(send);
    this.inflight = input.inflight;
    this.artifacts = path.join(input.job_dir, "artifacts");
    this.downloads = path.join(os.homedir(), "Downloads");
  }
  log(message, level = "info") {
    this.send({ type: "log", message, level });
  }
  record(data) {
    Object.assign(this.records, data);
    this.send({ type: "records", data });
  }
  action(action) {
    if (action) this.checkPause();
    this.inflight = action;
    this.send({ type: "inflight", action });
  }
  checkPause() {
    if (this.approvals.paused) throw new Paused();
  }
  async python(action, args) {
    const params = path.join(this.artifacts, `.params-${action}.json`);
    await fs.writeFile(params, JSON.stringify(args), "utf8");
    try {
      return await new Promise((resolve, reject) => {
        const child = spawn(
          this.input.python,
          ["-m", "webapp.actions", action, params],
          { cwd: root, stdio: ["ignore", "pipe", "pipe"], windowsHide: true },
        );
        let output = "",
          errors = "";
        child.stdout.on("data", (chunk) => {
          output += chunk;
        });
        child.stderr.on("data", (chunk) => {
          errors += chunk;
        });
        child.on("error", reject);
        child.on("exit", (code) => {
          if (code)
            reject(
              new BrowserError(
                errors.trim().split("\n").at(-1) || "文件处理失败",
              ),
            );
          else {
            try {
              resolve(JSON.parse(output));
            } catch {
              reject(new BrowserError("无法读取文件处理结果"));
            }
          }
        });
      });
    } finally {
      await fs.unlink(params).catch(() => {});
    }
  }
  async initialize() {
    const bindings = this.input.settings.bindings;
    if (!bindings)
      throw new BrowserError("请先重新读取浏览器并保存三个账号的连接设置");
    for (const [key, site] of [
      ["common_page", "upseller"],
      ["invoice_page", "upseller"],
      ["oms_page", "oms"],
    ]) {
      const binding = bindings[key];
      if (!binding || binding.site !== site)
        throw new BrowserError("账号连接设置不完整");
      if (!this.clients.has(binding.source))
        this.clients.set(binding.source, await connect(binding.source));
      const client = this.clients.get(binding.source);
      const pages = parsePages(await client.tool("list_pages"));
      const found = pages.find(
        (p) => p.id === binding.page_id && p.site === site,
      );
      if (!found)
        throw new BrowserError(
          "原标签页已关闭或编号已变化，请在连接设置重新选择账号",
        );
      if (/\/(login|signin|sign-in|passport|verify)(?:\/|$)/i.test(new URL(found.url).pathname) || /登录|\blogin\b/i.test(found.title)) throw new BrowserError('所选账号尚未完成登录验证，请在原专用 Chrome 完成登录后继续');
      const page = new Page(client, found.id, new URL(found.url).origin);
      if (binding.account) {
        const current = await page.account();
        if (!current || current !== binding.account)
          throw new BrowserError("标签页的账号已变化，请重新核对连接设置");
      }
      this[key.replace("_page", "")] = page;
    }
    this.log("已连接并核对常用 UpSeller、开票 UpSeller 和 OMS 标签页。");
  }
  async step(id, label, fn) {
    this.checkPause();
    if (this.done.has(id)) {
      this.log(`沿用已核验的进度：${label}`);
      return;
    }
    this.current = id;
    this.send({ type: "step_started", step: id });
    this.log(label);
    const message = await fn.call(this);
    this.done.add(id);
    this.send({ type: "step_done", step: id, message: message || "已核验" });
    this.log(`${label}：已核验`, "success");
  }
  async clickAny(page, labels, role = "button") {
    const snapshot = await page.snapshot();
    for (const label of labels) {
      if (snapshot.includes(`${role} "${label}"`))
        return page.click(label, role);
    }
    throw new BrowserError(`找不到“${labels.join(" / ")}”操作入口`);
  }
  async clickWithin(page, anchor, text, selector) {
    const outcome = await page.evaluate(
      String.raw`() => { const anchor=${JSON.stringify(anchor)}, target=${JSON.stringify(text)}, selector=${JSON.stringify(selector || "")}; const visible=e=>e.getBoundingClientRect().width&&e.getBoundingClientRect().height; const leaves=[...document.querySelectorAll('span,a,div,td')].filter(e=>visible(e)&&!e.querySelector('input,button,table')&&e.innerText?.replace(/\s/g,'')===anchor.replace(/\s/g,'')); let matches=[]; for(const leaf of leaves){ let node=leaf; for(let depth=0;depth<12&&node;depth++,node=node.parentElement){ const options=selector?[...node.querySelectorAll(selector)]:[...node.querySelectorAll('a,button,span,[role="menuitem"]')].filter(e=>e.innerText?.trim()===target); const targets=options.filter(visible); if(targets.length===1){matches.push(targets[0]);break;} } } matches=[...new Set(matches)]; if(matches.length!==1) return {ok:false,count:matches.length}; matches[0].click(); return {ok:true}; }`,
    );
    if (!outcome.ok)
      throw new BrowserError(
        `无法在 ${anchor} 内唯一定位 ${text || selector}，请检查页面`,
      );
  }
  async selectLabel(page, label, value) {
    const result = await page.evaluate(
      String.raw`() => { const label=${JSON.stringify(label)}, value=${JSON.stringify(value)}; const candidates=[...document.querySelectorAll('label,span,div,th')].filter(e=>!e.querySelector('input,select,[role="combobox"]')&&e.innerText?.replace(/[：:*\s]/g,'')===label&&e.getBoundingClientRect().width); const leaves=candidates.filter(e=>!candidates.some(other=>other!==e&&e.contains(other)));if(leaves.length!==1) return {ok:false}; let n=leaves[0]; for(let k=0;k<7&&n;k++,n=n.parentElement){ const options=[...n.querySelectorAll('select,[role="combobox"],.el-select,.ant-select')].filter(e=>e.getBoundingClientRect().width); if(options.length===1){ const select=options[0]; if(select.tagName==='SELECT'){ const found=[...select.options].filter(o=>o.text.trim()===value||o.text.trim().startsWith(value)); if(found.length!==1)return {ok:false};select.value=found[0].value;select.dispatchEvent(new Event('change',{bubbles:true}));return {ok:true,native:true};}select.click();return {ok:true,native:false};} } return {ok:false}; }`,
    );
    if (!result.ok) throw new BrowserError(`无法唯一定位 ${label} 下拉框`);
    if (result.native) return;
    const snapshot = await page.snapshot();
    const options = [
      ...snapshot.matchAll(/uid=(\S+) option "([^"\n]+)"/g),
    ].filter((m) => m[2] === value || m[2].startsWith(value));
    if (options.length === 1) await page.tool("click", { uid: options[0][1] });
    else {
      const selected = await page.evaluate(
        `() => {const value=${JSON.stringify(value)};const options=[...document.querySelectorAll('[role="option"],.el-select-dropdown__item,.ant-select-item-option')].filter(e=>e.getBoundingClientRect().width&&e.innerText.trim().startsWith(value));if(options.length!==1)return false;options[0].click();return true;}`,
      );
      if (!selected) throw new BrowserError(`没有找到唯一的 ${value} 选项`);
    }
  }
  async query(page, skus, oms = false) {
    this.checkPause();
    let snapshot = await page.snapshot();
    if (oms && snapshot.includes("双击批量搜索")) {
      const match = snapshot.match(
        /uid=(\S+) textbox "[^"\n]*双击批量搜索[^"\n]*"/,
      );
      if (!match) throw new BrowserError("找不到 OMS 精确批量搜索输入框");
      await page.tool("click", { uid: match[1], dblClick: true });
      snapshot = await page.snapshot();
      const textarea = snapshot.match(/uid=(\S+) textbox[^\n]*multiline/);
      if (!textarea) throw new BrowserError("OMS 批量搜索窗口没有打开");
      await page.tool("fill", { uid: textarea[1], value: skus.join("\n") });
      await this.clickAny(page, ["确认", "确定"]);
    } else {
      const lines = snapshot
        .split("\n")
        .filter(
          (l) => /textbox/.test(l) && /逗号|多个|SKU|搜索|请输入/.test(l),
        );
      const line =
        lines.find((l) => /逗号|多个/.test(l)) ||
        lines.find((l) => /SKU/.test(l));
      const uid = line?.match(/uid=(\S+)/)?.[1];
      if (!uid)
        throw new BrowserError(
          "未找到商品 SKU 搜索框，请确认当前筛选类型为 SKU",
        );
      await page.tool("fill", { uid, value: skus.join(",") });
      await page.tool("press_key", { key: "Enter" });
    }
    await sleep(1200);
    const body = await page.body();
    if (/请登录|登录已过期|会话.*过期/.test(body))
      throw new BrowserError(
        "当前账号登录已过期，请在原专用 Chrome 登录后继续",
      );
    return body;
  }
  async selectAll(page, expected) {
    const rows = await page.evaluate(
      `() => {const scope=[...document.querySelectorAll('[role="dialog"],.ant-modal-content,.el-dialog')].filter(e=>e.getBoundingClientRect().width).at(-1)||document;return [...scope.querySelectorAll('tbody tr,[role="row"]')].filter(e=>e.getBoundingClientRect().width).map(e=>e.innerText);}`,
    );
    const found = new Set(
      expected.filter((sku) =>
        rows.some((row) => row.replace(/\s/g, "").includes(sku)),
      ),
    );
    if (found.size !== expected.length)
      throw new BrowserError(
        "当前页没有显示所有目标 SKU，请检查筛选结果或将每页数量调到足够大",
      );
    const other = rows
      .flatMap((row) => row.match(/NW[A-Za-z0-9._-]+/g) || [])
      .filter((sku) => !expected.includes(sku));
    if (other.length)
      throw new BrowserError("筛选结果包含本批以外的 SKU，未全选");
    const snapshot = await page.snapshot();
    const checkboxes = [...snapshot.matchAll(/uid=(\S+) checkbox([^\n]*)/g)];
    if (!checkboxes.length) throw new BrowserError("没有找到商品全选框");
    if (
      !checkboxes[0][2].includes("checked") ||
      checkboxes[0][2].includes("mixed")
    )
      await page.tool("click", { uid: checkboxes[0][1] });
  }
  async downloaded(trigger, destination, extension = ".xlsx") {
    const before = new Map();
    for (const name of await fs.readdir(this.downloads)) {
      const stat = await fs
        .stat(path.join(this.downloads, name))
        .catch(() => null);
      if (stat) before.set(name, `${stat.size}:${stat.mtimeMs}`);
    }
    const started = Date.now();
    await trigger();
    let previous;
    for (let attempt = 0; attempt < 55; attempt++) {
      this.checkPause();
      const files = [];
      for (const name of await fs.readdir(this.downloads)) {
        if (!name.toLowerCase().endsWith(extension)) continue;
        const stat = await fs
          .stat(path.join(this.downloads, name))
          .catch(() => null);
        if (
          stat?.isFile() &&
          stat.size &&
          stat.mtimeMs >= started - 1500 &&
          before.get(name) !== `${stat.size}:${stat.mtimeMs}`
        )
          files.push({ name, size: stat.size, time: stat.mtimeMs });
      }
      if (files.length > 1)
        throw new BrowserError(
          "同时出现多个下载文件，无法确认属于哪次导出，请核对后重试",
        );
      if (files.length === 1) {
        const file = files[0];
        const signature = `${file.name}:${file.size}:${file.time}`;
        if (signature === previous) {
          await fs.copyFile(path.join(this.downloads, file.name), destination);
          return destination;
        }
        previous = signature;
      }
      await sleep(1000);
    }
    throw new BrowserError(
      "没有在“下载”目录确认到导出文件；请检查平台下载进度及 Chrome 下载目录",
    );
  }
  async exportProducts(page, skus, stem) {
    // shortcut: one verified 100-row selection; add paginated selection for larger batches.
    if (skus.length > 100)
      throw new BrowserError(
        "本次清单超过 100 个 SKU，需要分段核对后执行，当前未开始创建商品",
      );
    await page.navigate(
      "https://app.upseller.com/zh-CN/products/product-list?showAdd=false",
    );
    const body = await this.query(page, skus);
    if (
      /暂无数据|没有数据|无匹配商品/.test(body) &&
      !skus.some((s) => body.replace(/\s/g, "").includes(s))
    )
      return { path: null, catalog: {} };
    const destination = path.join(this.artifacts, `${stem}.xlsx`);
    await this.downloaded(async () => {
      await page.clickText("导入 & 导出");
      const snapshot = await page.snapshot();
      const submenu = snapshot.match(/uid=(\S+) [^\n]*"导出商品SKU"/);
      if (submenu) await page.tool("hover", { uid: submenu[1] });
      else await page.clickText("导出商品SKU");
      await page.clickText("导出页数");
      await this.clickAny(page, ["导出", "开始导出"]);
      await page.wait("下载", 45000);
      await page.click("下载");
    }, destination);
    const catalog = await this.python("catalog", { path: destination });
    if (Object.keys(catalog).some((s) => !skus.includes(s)))
      throw new BrowserError("商品导出包含本批次以外的 SKU，请核对搜索筛选");
    return { path: destination, catalog };
  }
  async catalog() {
    const result = await this.exportProducts(
      this.common,
      this.items.map((i) => i.sku),
      "常用账号_商品核对",
    );
    const existing = Object.keys(result.catalog);
    const fresh = this.items.filter((i) => !existing.includes(i.sku));
    const missing = fresh.filter(item => !item.image || !item.name_pt);
    if (missing.length)
      throw new BrowserError(`新品资料未齐：${missing.map(item => item.sku).join('、')}。请补齐企微产品图和葡语名称，再重新匹配；尚未建单或创建商品。`);
    this.record({
      existing_skus: existing,
      new_skus: fresh.map((i) => i.sku),
      initial_catalog: result.path ? path.basename(result.path) : null,
    });
    this.log(
      `本批 ${this.items.length} 个 SKU：已有 ${existing.length} 个，新品 ${fresh.length} 个。`,
    );
    return `新品 ${fresh.length} 款，补货 ${existing.length} 款`;
  }
  async products() {
    const fresh = this.items.filter((i) =>
      this.records.new_skus.includes(i.sku),
    );
    const completed = new Set(this.records.saved_new_skus || []);
    for (const item of fresh) {
      this.checkPause();
      if (completed.has(item.sku)) continue;
      if (!item.name_pt || !item.image)
        throw new BrowserError(
          `${item.sku} 缺少新品所需的葡语名称或图片，请补齐清单后再处理`,
        );
      if (
        this.inflight?.type === "create_product" &&
        this.inflight.target === item.sku
      ) {
        await this.common.navigate(
          "https://app.upseller.com/zh-CN/products/product-list?showAdd=false",
        );
        const body = await this.query(this.common, [item.sku]);
        if (body.replace(/\s/g, "").includes(item.sku)) {
          completed.add(item.sku);
          this.record({ saved_new_skus: [...completed] });
          this.action(null);
          continue;
        }
        throw new BrowserError(
          `${item.sku} 上次保存结果不明，请在原页面核对。结果明确前不会重复保存。`,
        );
      }
      await advanceToNext(this.common.client.request, this.common.id);
      await this.common.fillLabel("SKU", item.sku);
      await this.common.fillLabel("商品标题", item.name_pt);
      await uploadLocalImage(
        this.common.client.request,
        this.common.id,
        path.join(this.input.job_dir, "images", item.image),
      );
      await sleep(1000);
      const state = await this.common.snapshot();
      if (
        !/StaticText "1"[\s\S]{0,200}StaticText "\/"/.test(state) &&
        !/1\s*\/\s*15/.test(await this.common.body())
      )
        throw new BrowserError(`${item.sku} 图片尚未显示在页面，未保存`);
      this.action({
        step: "products",
        type: "create_product",
        target: item.sku,
      });
      await this.common.click("保存");
      await sleep(1800);
      const after = await this.common.body();
      if (/SKU.*已存在|保存成功|创建成功/.test(after)) {
        await this.common.navigate(
          "https://app.upseller.com/zh-CN/products/product-list?showAdd=false",
        );
        const list = await this.query(this.common, [item.sku]);
        if (!list.replace(/\s/g, "").includes(item.sku))
          throw new BrowserError(`${item.sku} 保存后列表中未确认到商品`);
      } else
        throw new BrowserError(
          `${item.sku} 保存结果未确认，已停在此 SKU，请检查平台提示`,
        );
      completed.add(item.sku);
      this.record({ saved_new_skus: [...completed] });
      this.action(null);
      this.log(`已保存并核验新品：${item.sku}`);
    }
    return `已核验 ${fresh.length} 个新品`;
  }
  async omsExport(items, stem) {
    await this.oms.navigate(`${omsOrigin}/web/product/all`);
    await this.query(
      this.oms,
      items.map((i) => i.sku),
      true,
    );
    await this.selectAll(
      this.oms,
      items.map((i) => i.sku),
    );
    await this.oms.clickText("导入商品");
    await this.oms.clickText("编辑商品");
    const file = path.join(this.artifacts, `${stem}.xlsx`);
    await this.downloaded(async () => {
      await this.oms.clickText("按当前勾选的商品导出");
      if ((await this.oms.snapshot()).includes('button "下载"'))
        await this.oms.click("下载");
    }, file);
    return file;
  }
  async measurements() {
    const items = this.items.filter((i) =>
      this.records.new_skus.includes(i.sku),
    );
    if (!items.length) return "本批没有新品，无需批量改动尺寸重量";
    const source = await this.omsExport(items, "OMS_新品_原始导出");
    const destination = path.join(this.artifacts, "OMS_新品_尺寸重量.xlsx");
    await this.python("oms_update", { source, destination, items });
    await this.oms.upload("点击此处上传文件", [destination]);
    this.action({
      step: "measurements",
      type: "update_measurements",
      target: this.manifest.batch_name,
    });
    await this.oms.click("导入");
    await this.oms.wait("成功", 45000);
    const body = await this.oms.body();
    if (/失败\s*[：:]?\s*[1-9]|导入失败/.test(body))
      throw new BrowserError("OMS 导入有失败记录，请查看平台提示");
    const exported = await this.omsExport(items, "OMS_新品_回读核对");
    await this.python("oms_verify", { source, exported, items });
    this.action(null);
    return `已回读 ${items.length} 个 SKU，葡语名称和尺寸重量一致`;
  }
  async openMenu(page, menu, entry, group) {
    const previous = new Set(
      parsePages(await page.client.tool("list_pages")).map((p) => p.id),
    );
    const origin = new URL(await page.evaluate("() => location.href")).origin;
    await page.clickText(menu);
    if (group) await this.clickWithin(page, group, entry);
    else await page.clickText(entry);
    await sleep(900);
    const opened = parsePages(await page.client.tool("list_pages")).filter(
      (p) => !previous.has(p.id) && new URL(p.url).origin === origin,
    );
    if (opened.length > 1)
      throw new BrowserError("操作后打开了多个新标签页，请核对目标页面");
    if (opened.length === 1) page.id = opened[0].id;
  }
  async chooseProducts(page, label) {
    await page.click(label);
    await this.query(
      page,
      this.items.map((i) => i.sku),
    );
    await this.selectAll(
      page,
      this.items.map((i) => i.sku),
    );
    await this.clickAny(page, ["确认", "确定", "添加"]);
    await sleep(700);
  }
  async editableRows(page, prices = false) {
    return page.evaluate(
      String.raw`() => { const expected=${JSON.stringify(this.items.map((i) => i.sku))}; const output=[]; for(const sku of expected){ const leaf=[...document.querySelectorAll('span,a,td,div')].find(e=>!e.querySelector('input,button,table')&&e.getBoundingClientRect().width&&e.innerText?.replace(/\s/g,'')===sku); if(!leaf) continue; let node=leaf; for(let depth=0;depth<12&&node;depth++,node=node.parentElement){ const inputs=[...node.querySelectorAll('input')].filter(e=>e.type!=='checkbox'&&e.type!=='hidden'&&e.getBoundingClientRect().width); const contained=expected.filter(s=>node.innerText.replace(/\s/g,'').includes(s)); if(inputs.length>=${prices ? 2 : 1}&&contained.length===1){ const selected=inputs.slice(0,${prices ? 2 : 1}); selected.forEach((e,i)=>e.setAttribute('aria-label','oms-workbench-'+sku+'-'+i)); output.push({sku,values:selected.map(e=>e.value)});break;} } } return output; }`,
    );
  }
  async verifySavedOrder() {
    const actual = await this.invoice.evaluate(
      String.raw`() => {const expected=${JSON.stringify(this.items.map((i) => i.sku))};return expected.map(sku=>{const leaf=[...document.querySelectorAll('span,a,td,div')].find(e=>!e.querySelector('input,button,table')&&e.getBoundingClientRect().width&&e.innerText?.replace(/\s/g,'')===sku);let n=leaf;for(let i=0;i<12&&n;i++,n=n.parentElement){if(n.innerText.includes('税务信息')&&expected.filter(s=>n.innerText.replace(/\s/g,'').includes(s)).length===1)return {sku,text:n.innerText};}return null;}).filter(Boolean);}`,
    );
    checkSavedOrder(actual, this.items);
  }
  async fillRows(page, prices = false) {
    const rows = await this.editableRows(page, prices);
    if (rows.length !== this.items.length)
      throw new BrowserError("页面商品集合不完整，未填写数量或单价");
    for (const item of this.items) {
      this.checkPause();
      let snapshot = await page.snapshot();
      let uid = findUid(
        snapshot,
        "(?:textbox|spinbutton)",
        `oms-workbench-${item.sku}-0`,
      );
      await page.tool("fill", { uid, value: String(item.quantity) });
      if (prices) {
        snapshot = await page.snapshot();
        uid = findUid(
          snapshot,
          "(?:textbox|spinbutton)",
          `oms-workbench-${item.sku}-1`,
        );
        await page.tool("click", { uid });
        await page.tool("press_key", { key: "Control+A" });
        await page.tool("type_text", {
          text: item.unit_price.replace(".", ","),
          submitKey: "Tab",
        });
      }
    }
    checkQuantities(await this.editableRows(page, prices), this.items, prices);
  }
  async checkFlag(page, label) {
    const lines = (await page.snapshot()).split('\n').filter(line => /checkbox/.test(line) && line.includes(label));
    if (lines.length !== 1) throw new BrowserError(`无法核对 ${label} 选项`);
    if (!lines[0].includes('checked') || lines[0].includes('mixed')) await page.tool('fill', {uid: lines[0].match(/uid=(\S+)/)[1], value:'true'});
    const verified = (await page.snapshot()).split('\n').find(line => /checkbox/.test(line) && line.includes(label));
    if (!verified?.includes('checked') || verified.includes('mixed')) throw new BrowserError(`${label} 未保持勾选`);
  }
  async inbound() {
    if (this.records.inbound_no) {
      await this.openInbound();
      return "已定位原入库单，沿用该单据";
    }
    await this.openMenu(this.oms, "入库", "新建入库单", "一件代发入库");
    await this.selectLabel(this.oms, "仓库", "BomJesus");
    await this.selectLabel(this.oms, "到仓方式", "箱");
    await this.selectLabel(this.oms, "运输方式", "海运");
    await this.oms.clickText("按箱预报");
    await this.oms.clickText("SKU维度");
    await this.chooseProducts(this.oms, "选择商品");
    await this.oms.clickText("Excel快速编辑");
    await this.oms.clickText("第二步：粘贴导入");
    const snapshot = await this.oms.snapshot();
    const textarea = snapshot.match(/uid=(\S+) textbox[^\n]*multiline/);
    if (!textarea) throw new BrowserError("未打开 Excel 粘贴导入窗口");
    await this.oms.tool("fill", {
      uid: textarea[1],
      value:
        "SKU\t#1\n" +
        this.items.map((i) => `${i.sku}\t${i.quantity}`).join("\n"),
    });
    await this.clickAny(this.oms, ["确认", "确定"]);
    checkQuantities(await this.editableRows(this.oms), this.items);
    this.record({
      inbound_form_url: await this.oms.evaluate("() => location.href"),
    });
    return `已填写 ${this.items.length} 个 SKU、${this.manifest.totals.quantity} 件，等待提交确认`;
  }
  async observe(page) {
    await page.evaluate(
      `() => { window.__omsWorkbenchReceipts=[]; if(window.__omsWorkbenchObserved)return true; window.__omsWorkbenchObserved=true; const store=(url,data)=>{if(!/inbound|sku|product|order|invoice/i.test(url)||/login|auth|token/i.test(url))return; const values=[]; const walk=(v,d)=>{if(d>5||!v||typeof v!=='object')return; for(const [k,x]of Object.entries(v)){if(/^(id|orderNo|inboundNo|invoiceNo|code|success|msg|message)$/i.test(k)&&['string','number','boolean'].includes(typeof x))values.push({key:k,value:String(x).slice(0,200)});else if(x&&typeof x==='object')walk(x,d+1);}};walk(data,0);window.__omsWorkbenchReceipts.push({path:String(url).split('?')[0],values});}; const original=window.fetch;window.fetch=async function(...args){const response=await original.apply(this,args);try{const url=typeof args[0]==='string'?args[0]:args[0].url;response.clone().json().then(v=>store(url,v)).catch(()=>{});}catch{}return response;};const open=XMLHttpRequest.prototype.open;XMLHttpRequest.prototype.open=function(method,url,...rest){this.addEventListener('load',()=>{try{store(String(url),JSON.parse(this.responseText));}catch{}});return open.call(this,method,url,...rest);};return true; }`,
    );
  }
  async generatedNumber(page, prefix) {
    for (let attempt = 0; attempt < 12; attempt++) {
      const values = await page.evaluate(
        "() => ({text:document.body.innerText,receipts:window.__omsWorkbenchReceipts||[],url:location.href})",
      );
      const receiptMatches = values.receipts.flatMap((r) =>
        r.values
          .filter((v) => new RegExp(`^${prefix}[A-Za-z0-9]+$`).test(v.value))
          .map((v) => v.value),
      );
      const unique = [...new Set(receiptMatches)];
      if (unique.length === 1) return { number: unique[0], url: values.url };
      const bodyNumbers = [
        ...new Set(
          values.text.match(new RegExp(`${prefix}[A-Za-z0-9]+`, "g")) || [],
        ),
      ];
      if (
        bodyNumbers.length === 1 &&
        /成功|详情|已预报|待开票/.test(values.text)
      )
        return { number: bodyNumbers[0], url: values.url };
      await sleep(1000);
    }
    throw new BrowserError(
      "操作后没有确认到唯一的新单号；请在平台核对后，把已有单号填入工作台再继续",
    );
  }
  async submit() {
    if (this.records.inbound_no) {
      const status = await this.inboundStatus();
      if (/已预报|运输中|入库中|已完成/.test(status)) {
        this.action(null);
        return "原入库单已提交，未重复提交";
      }
      await this.oms.clickText(this.records.inbound_no);
      if (!(await this.oms.snapshot()).includes('button "保存并提交"'))
        await this.oms.click("编辑");
      checkQuantities(await this.editableRows(this.oms), this.items);
    } else if (this.inflight?.type === "submit_inbound")
      throw new BrowserError("上次提交结果待核对，请补充已生成的 OMS 入库单号");
    else {
      const url = await this.oms.evaluate("() => location.href");
      if (url !== this.records.inbound_form_url) {
        await this.inbound();
      }
      checkQuantities(await this.editableRows(this.oms), this.items);
    }
    await this.approvals.gate("submit", "确认提交 OMS 入库单", [
      this.manifest.batch_name,
      `仓库 BomJesus · 海运 · ${this.items.length} 个 SKU · ${this.manifest.totals.quantity} 件`,
    ]);
    this.checkPause();
    await this.observe(this.oms);
    this.action({
      step: "submit",
      type: "submit_inbound",
      target: this.manifest.batch_name,
    });
    await this.oms.click("保存并提交");
    const generated = await this.generatedNumber(this.oms, "IN");
    this.record({ inbound_no: generated.number, inbound_url: generated.url });
    const status = await this.inboundStatus();
    if (!/已预报|运输中|入库中|已完成/.test(status))
      throw new BrowserError(
        "单号已保存，但页面尚未确认已预报，请核对平台提交结果",
      );
    this.action(null);
    return generated.number;
  }
  async openInbound() {
    await this.openMenu(this.oms, "入库", "入库单管理", "一件代发入库");
    const body = await this.oms.body();
    if (!body.includes(this.records.inbound_no))
      throw new BrowserError(
        `列表中未找到 ${this.records.inbound_no}，请将目标单据筛选出来后继续`,
      );
    return this.oms.body();
  }
  async inboundStatus() {
    await this.openInbound();
    return this.oms.evaluate(
      `() => [...document.querySelectorAll('tr')].find(e=>e.innerText.includes(${JSON.stringify(this.records.inbound_no)}))?.innerText||''`,
    );
  }
  async ship() {
    const status = await this.inboundStatus();
    if (/运输中|入库中|已完成/.test(status)) {
      this.action(null);
      return "目标入库单已在运输中或后续状态";
    }
    if (!/已预报/.test(status))
      throw new BrowserError("目标入库单尚未处于已预报状态，不能发货");
    await this.approvals.gate("ship", "确认入库单发货", [
      this.records.inbound_no,
      `BomJesus · 海运 · ${this.manifest.totals.quantity} 件`,
    ]);
    this.action({
      step: "ship",
      type: "ship_inbound",
      target: this.records.inbound_no,
    });
    await this.clickWithin(
      this.oms,
      this.records.inbound_no,
      "发货",
      '[title="发货"],[aria-label="发货"],[data-tooltip="发货"]',
    );
    const snapshot = await this.oms.snapshot();
    if (snapshot.includes('button "确认"')) await this.oms.click("确认");
    await sleep(1000);
    await this.openInbound();
    const row = await this.oms.evaluate(
      `() => [...document.querySelectorAll('tr')].find(e=>e.innerText.includes(${JSON.stringify(this.records.inbound_no)}))?.innerText||''`,
    );
    if (!/运输中/.test(row))
      throw new BrowserError(
        "点击发货后没有确认到该入库单处于运输中，请核对原单据",
      );
    this.action(null);
    return `${this.records.inbound_no} 已在运输中`;
  }
  async openOrder() {
    await this.openMenu(this.invoice, "订单", "全部订单");
    const body = await this.invoice.body();
    if (!body.includes(this.records.manual_order_no)) {
      const snapshot = await this.invoice.snapshot();
      const input = snapshot
        .split("\n")
        .find((l) => /textbox/.test(l) && /订单号|请输入/.test(l));
      const uid = input?.match(/uid=(\S+)/)?.[1];
      if (!uid)
        throw new BrowserError(
          `请在开票账号打开 ${this.records.manual_order_no} 后继续`,
        );
      await this.invoice.tool("fill", {
        uid,
        value: this.records.manual_order_no,
      });
      await this.invoice.tool("press_key", { key: "Enter" });
      await sleep(1000);
    }
    await this.invoice
      .clickText("#" + this.records.manual_order_no)
      .catch(() => this.invoice.clickText(this.records.manual_order_no));
    await sleep(600);
    if (!(await this.invoice.body()).includes(this.records.manual_order_no))
      throw new BrowserError("打开的订单与记录单号不一致");
  }
  async syncInvoiceProducts(catalogPath) {
    const found = await this.exportProducts(
      this.invoice,
      this.items.map((i) => i.sku),
      "开票账号_商品核对",
    );
    const missing = this.items
      .filter((i) => !found.catalog[i.sku])
      .map((i) => i.sku);
    if (!missing.length) return;
    this.log(`开票账号缺少 ${missing.length} 个商品，按常用账号商品导出补齐。`);
    await this.invoice.clickText("导入 & 导出");
    await this.invoice.clickText("导入单品");
    const template = path.join(this.artifacts, "UpSeller_单品原模板.xlsx");
    await this.downloaded(async () => {
      await this.invoice.clickText("下载模板");
    }, template);
    const file = path.join(this.artifacts, "UpSeller_开票账号待导入.xlsx");
    await this.python("single_import", {
      template,
      destination: file,
      catalog: catalogPath,
      skus: missing,
    });
    const snapshot = await this.invoice.snapshot();
    const label = ["上传文件", "选择文件", "点击上传"].find((l) =>
      snapshot.includes(`"${l}"`),
    );
    if (!label)
      throw new BrowserError("未找到 UpSeller 单品导入的文件选择入口");
    await this.invoice.upload(label, [file]);
    this.action({
      step: "invoice_order",
      type: "import_invoice_products",
      target: this.manifest.batch_name,
    });
    await this.clickAny(this.invoice, ["导入", "开始导入"]);
    await this.invoice.wait("成功", 45000);
    const after = await this.exportProducts(
      this.invoice,
      this.items.map((i) => i.sku),
      "开票账号_导入后核对",
    );
    if (this.items.some((i) => !after.catalog[i.sku]))
      throw new BrowserError(
        "开票账号商品导入后仍有 SKU 缺失，请检查平台失败报告",
      );
    this.action(null);
  }
  async invoiceOrder() {
    const billing = billingConfig();
    if (this.records.manual_order_no) {
      await this.openOrder();
      await this.verifySavedOrder();
      this.action(null);
      return "沿用并核对原手工订单";
    }
    if (this.inflight?.type === "save_manual_order")
      throw new BrowserError(
        "上次订单保存结果待核对，请填写已经生成的手工订单号",
      );
    const result = await this.exportProducts(
      this.common,
      this.items.map((i) => i.sku),
      "常用账号_整批商品",
    );
    if (this.items.some((i) => !result.catalog[i.sku]))
      throw new BrowserError("常用账号商品还没有齐全，不能建立开票订单");
    this.record({ full_catalog: path.basename(result.path) });
    await this.syncInvoiceProducts(result.path);
    await this.openMenu(this.invoice, "订单", "手工订单");
    await this.selectLabel(this.invoice, "店铺", billing.shop);
    await this.invoice.fillLabel("收件人", billing.name);
    await this.selectLabel(this.invoice, "税号", "CNPJ");
    await this.invoice.fillLabel("税号", billing.cnpj);
    await this.invoice.tool("press_key", { key: "Tab" });
    await sleep(1800);
    const auto = await this.invoice.evaluate(
      '() => [...document.querySelectorAll("input")].map(e=>e.value).join("\\n")',
    );
    if (!auto.includes(billing.company) || !auto.includes(billing.name))
      throw new BrowserError("CNPJ 自动带出的收件信息尚未确认，请在平台核对");
    await this.chooseProducts(this.invoice, "添加商品");
    await this.fillRows(this.invoice, true);
    const total = (await this.editableRows(this.invoice, true)).reduce(
      (sum, item) => sum + Number(item.values[0]) * cents(item.values[1]),
      0,
    );
    if (total !== cents(this.manifest.totals.amount))
      throw new BrowserError("页面商品金额合计与清单不符");
    await this.observe(this.invoice);
    this.action({
      step: "invoice_order",
      type: "save_manual_order",
      target: this.manifest.batch_name,
    });
    await this.invoice.click("保存");
    const generated = await this.generatedNumber(this.invoice, "UP");
    this.record({ manual_order_no: generated.number });
    await this.openOrder();
    await this.verifySavedOrder();
    this.action(null);
    return `${generated.number} · R$ ${this.manifest.totals.amount}`;
  }
  async tax() {
    const source = await this.exportProducts(
      this.common,
      this.items.map((i) => i.sku),
      "常用账号_开票前NCM",
    );
    const missing = this.items.filter(
      (i) =>
        !/^\d{8}$/.test(source.catalog[i.sku]?.ncm || "") ||
        source.catalog[i.sku]?.ncm === "00000000",
    );
    if (missing.length)
      throw new BrowserError(
        `商品导出 AG 列缺少有效 NCM：${missing.map((i) => i.sku).join("、")}`,
      );
    await this.checkFlag(this.invoice,'需要开票');
    await this.checkFlag(this.invoice,'销售终端');
    await this.openOrder();
    await this.verifySavedOrder();
    for (const item of this.items) {
      this.checkPause();
      await this.clickWithin(this.invoice, item.sku, "税务信息");
      await sleep(500);
      await this.invoice.fillLabel("NCM", source.catalog[item.sku].ncm);
      const snapshot = await this.invoice.snapshot();
      if (snapshot.includes(`option "${source.catalog[item.sku].ncm}`)) {
        const option = snapshot.match(
          new RegExp(
            `uid=(\\S+) option "${source.catalog[item.sku].ncm}[^"\\n]*"`,
          ),
        );
        if (option) await this.invoice.tool("click", { uid: option[1] });
      }
      await this.selectLabel(
        this.invoice,
        "原产地",
        "2 - Estrangeira - Adquirida no mercado interno",
      );
      const check = await this.invoice.snapshot();
      const checkbox = check.match(/uid=(\S+) checkbox "保存税务信息"([^\n]*)/);
      if (!checkbox) throw new BrowserError("未找到“保存税务信息”复选框");
      if (!checkbox[2].includes("checked"))
        await this.invoice.tool("click", { uid: checkbox[1] });
      this.checkPause();
      await this.invoice.click("保存");
      await sleep(400);
      await this.clickWithin(this.invoice, item.sku, "税务信息");
      await sleep(500);
      const verified = await this.invoice.snapshot();
      if (
        !verified.includes(`value="${source.catalog[item.sku].ncm}"`) ||
        !/2 - Estrangeira - Adquirida/.test(verified)
      )
        throw new BrowserError(`${item.sku} 税务信息复开核验不一致`);
      await this.clickAny(this.invoice, ["关闭", "取消"]);
      this.log(`${item.sku}：NCM 与 AG 列一致，原产地代码 2 已核验。`);
    }
    this.record({ ncm_catalog: path.basename(source.path) });
    return `已逐项复核 ${this.items.length} 个 SKU`;
  }
  async invoiceStatus() {
    await this.invoice.evaluate(
      `() => {const elements=[...document.querySelectorAll('[role="dialog"] button[aria-label="Close"],.ant-modal-close,.el-dialog__headerbtn')].filter(e=>e.getBoundingClientRect().width&&e.getBoundingClientRect().height);if(elements.length===1)elements[0].click();return true;}`,
    );
    if (
      !(await this.invoice.evaluate("() => location.pathname")).includes(
        "/invoice-manage/brazil-nf-e/",
      )
    )
      await this.openMenu(this.invoice, "订单", "巴西 NF-e");
    const rows = await this.invoice.evaluate(
      `() => [...document.querySelectorAll('tr')].filter(e=>e.getBoundingClientRect().width&&e.innerText.includes(${JSON.stringify(this.records.manual_order_no)})).map(e=>e.innerText)`,
    );
    if (rows.length > 1)
      throw new BrowserError("同一订单查到多条发票，请在 NF-e 页面核对后继续");
    return rows[0] || "";
  }
  async issue() {
    if (this.inflight?.type === "issue_invoice" || this.records.invoice_no) {
      const status = await this.invoiceStatus();
      if (/已开票/.test(status)) {
        this.action(null);
        return "已在 NF-e 核实原订单开票成功，未重复开票";
      }
      throw new BrowserError(
        "上次开票结果待核对，请在巴西 NF-e 按原订单号筛选；确认已开票后继续",
      );
    }
    await this.openOrder();
    await this.verifySavedOrder();
    await this.approvals.gate("issue", "确认开具本批发票", [
      this.records.manual_order_no,
      `${billingConfig().name} · CNPJ ${billingConfig().cnpj}`,
      `${this.items.length} 个 SKU · ${this.manifest.totals.quantity} 件 · BRL ${this.manifest.totals.amount}`,
    ]);
    this.action({
      step: "issue",
      type: "issue_invoice",
      target: this.records.manual_order_no,
    });
    await this.invoice.click("开票");
    await sleep(2000);
    const snapshot = await this.invoice.snapshot();
    if (/dialog/.test(snapshot) && snapshot.includes('button "确认开票"'))
      await this.invoice.click("确认开票");
    for (let i = 0; i < 20; i++) {
      const status = await this.invoiceStatus();
      if (/开票失败|缺少必填|开票错误/.test(status))
        throw new BrowserError("平台报告开票未成功，请查看错误后处理原订单");
      if (/已开票/.test(status)) {
        this.action(null);
        return "NF-e 中原订单已开票状态已核验";
      }
      this.checkPause();
      await sleep(2500);
    }
    throw new BrowserError(
      "开票仍未得到明确结果，已保留原订单号；请核对平台状态再继续",
    );
  }
  async documents() {
    const invoicePrefix = this.manifest.batch_name + "入库发票";
    const pdf = path.join(this.artifacts, invoicePrefix + ".pdf");
    const xml = path.join(this.artifacts, invoicePrefix + ".xml");
    await this.invoice.navigate(
      "https://app.upseller.com/zh-CN/order/to-ship?showAdd=false",
    );
    await this.clickWithin(
      this.invoice,
      this.records.manual_order_no,
      "",
      'input[type="checkbox"]',
    );
    await this.invoice.clickText("更多操作");
    await this.invoice.clickText("打印发票");
    const pagesBefore = await this.invoice.client.tool("list_pages");
    await this.invoice.clickText("DANFE");
    await sleep(1800);
    const pagesAfter = await this.invoice.client.tool("list_pages");
    const beforeUrls = new Set(
      pagesBefore.match(/https:\/\/[^\s)]+\.pdf(?:\?[^\s)]+)?/g) || [],
    );
    const urls = [
      ...new Set(
        pagesAfter.match(/https:\/\/[^\s)]+\.pdf(?:\?[^\s)]+)?/g) || [],
      ),
    ].filter((u) => !beforeUrls.has(u));
    if (urls.length !== 1)
      throw new BrowserError(
        "没有确认到本次生成的唯一 DANFE PDF 预览，请检查平台打印进度",
      );
    const parsed = new URL(urls[0]);
    if (
      !parsed.hostname.endsWith(".upseller.cn") &&
      !parsed.hostname.endsWith(".upseller.com")
    )
      throw new BrowserError("PDF 下载地址不属于 UpSeller，已停止下载");
    const response = await fetch(urls[0], {
      signal: AbortSignal.timeout(45000),
      redirect: "error",
    });
    if (!response.ok)
      throw new BrowserError("DANFE PDF 下载失败，请在原预览页面检查");
    const bytes = Buffer.from(await response.arrayBuffer());
    if (bytes.subarray(0, 5).toString() !== "%PDF-")
      throw new BrowserError("平台返回的内容不是 PDF");
    await fs.writeFile(pdf, bytes);
    await this.openMenu(this.invoice, "订单", "巴西 NF-e");
    const body = await this.invoice.body();
    if (!body.includes(this.records.manual_order_no))
      throw new BrowserError(
        `请在巴西 NF-e 页面按 ${this.records.manual_order_no} 筛选已开具的发票后继续`,
      );
    await this.clickWithin(this.invoice, this.records.manual_order_no, "更多");
    await this.downloaded(() => this.invoice.clickText("导出XML"), xml, ".xml");
    const xmlText = await fs.readFile(xml, "utf8");
    const number = xmlText.match(/<nNF>(\d+)<\/nNF>/)?.[1];
    const total = xmlText.match(/<vNF>([\d.]+)<\/vNF>/)?.[1];
    if (
      !number ||
      !total ||
      cents(total) !== cents(this.manifest.totals.amount)
    )
      throw new BrowserError(
        "XML 发票号或发票金额与本批不匹配，请核对下载对象",
      );
    const verified = await this.python("invoice_verify", {
      pdf,
      xml,
      amount: this.manifest.totals.amount,
    });
    this.record({ invoice_no: verified.invoice_no });
    const files = await this.python("attachments", {
      source: this.input.source,
      pdf,
      xml,
      output: path.join(this.artifacts, "回传附件"),
      batch_name: this.manifest.batch_name,
    });
    this.record({
      attachment_files: files.map((f) =>
        path.relative(this.artifacts, f).replaceAll("\\", "/"),
      ),
    });
    this.send({
      type: "files",
      files: [
        ...files.map((f) =>
          path.relative(this.artifacts, f).replaceAll("\\", "/"),
        ),
        path.basename(xml),
      ],
    });
    return `PDF、XML 与 ZIP 已下载并核对，发票 ${number}`;
  }
  async communication() {
    await this.openInbound();
    const body = await this.oms.body();
    if (!body.includes(this.records.inbound_no))
      throw new BrowserError("当前 OMS 页面不属于目标入库单");
    if (!body.includes("基本信息")) {
      await this.oms.clickText(this.records.inbound_no);
      await sleep(500);
    }
    const detailUrl = await this.oms.evaluate("() => location.href");
    if (detailUrl.includes("/web/inbound/detail?"))
      this.record({ inbound_detail_url: detailUrl });
    const snapshot = await this.oms.snapshot();
    if (snapshot.includes('tab "沟通记录"'))
      await this.oms.click("沟通记录", "tab");
    else {
      await this.clickWithin(this.oms, this.records.inbound_no, "查看");
      await this.oms.click("去沟通");
    }
    await this.oms.wait("上传附件");
    return this.oms.body();
  }
  async sendAttachments(files) {
    const body = await this.oms.body();
    const pending = files.filter((file) => !body.includes(path.basename(file)));
    if (!pending.length) return;
    await this.oms.upload("上传附件", pending);
    await sleep(1600);
    const ready = await this.oms.evaluate(
      `() => {const items=[...document.querySelectorAll('.file-item')];return items.map(e=>({name:e.innerText,failed:[e,...e.querySelectorAll('*')].some(n=>{const rgb=getComputedStyle(n).color.match(/[0-9]+/g)?.map(Number);return /error|fail|danger/.test(String(n.className))||(rgb&&rgb[0]>160&&rgb[0]>rgb[1]*1.5&&rgb[0]>rgb[2]*1.5);})}));}`,
    );
    if (
      pending.some(
        (file) =>
          !ready.some(
            (item) => item.name.includes(path.basename(file)) && !item.failed,
          ),
      )
    )
      throw new BrowserError(
        "有附件未完成上传或被平台拒绝，请查看大小或格式提示",
      );
    const sent = await this.oms.evaluate(
      `() => { const failed=[...document.querySelectorAll('.file-item')].some(e=>/error|fail/.test(e.className)||[...e.querySelectorAll('*')].some(n=>/error|fail/.test(n.className||''))); const icon=[...document.querySelectorAll('svg use')].find(e=>(e.getAttribute('xlink:href')||e.getAttribute('href')||'')==='#icon-SendMessage'); const button=icon?.closest('[class*=cursor-pointer]'); if(failed||!button||button.className.includes('!cursor-not-allowed'))return false;button.click();return true;}`,
    );
    if (!sent)
      throw new BrowserError("附件尚未上传完成或发送按钮不可用，未发送");
    await sleep(1000);
    const state = await this.oms.evaluate(
      '() => ({text:document.body.innerText,drafts:document.querySelectorAll(".file-item").length})',
    );
    if (
      state.drafts ||
      pending.some((file) => !state.text.includes(path.basename(file)))
    )
      throw new BrowserError("发送结果尚未确认，请检查该单沟通记录再继续");
    this.log(
      `沟通记录已显示：${pending.map((f) => path.basename(f)).join("、")}`,
    );
  }
  async attachments() {
    await this.communication();
    const files = this.records.attachment_files.map((file) =>
      path.join(this.artifacts, file),
    );
    await this.approvals.gate("attachments", "确认发送入库单附件", [
      this.records.inbound_no,
      ...files.map((file) => path.basename(file)),
      "XML 保持原内容，放在 ZIP 内供对方解压。",
    ]);
    this.action({
      step: "attachments",
      type: "send_attachments",
      target: this.records.inbound_no,
    });
    await this.sendAttachments(files.slice(0, 2));
    await this.sendAttachments(files.slice(2));
    await this.communication();
    const body = await this.oms.body();
    if (files.some((file) => !body.includes(path.basename(file))))
      throw new BrowserError("沟通记录未确认到所有附件");
    this.action(null);
    return "PDF、清单和 XML ZIP 均已在目标单沟通记录中显示";
  }
  async run() {
    try {
      await this.initialize();
      for (const [id, label] of [
        ["catalog", "核对常用账号商品"],
        ["products", "新品录入并保存"],
        ["measurements", "更新 OMS 尺寸重量"],
        ["inbound", "填写入库单"],
        ["submit", "提交入库单"],
        ["ship", "入库单发货"],
        ["invoice_order", "创建开票手工订单"],
        ["tax", "填写 NCM 与原产地"],
        ["issue", "开具发票"],
        ["documents", "下载 PDF 与 XML"],
        ["attachments", "回传 OMS 附件"],
      ])
        await this.step(id, label, this[id]);
      this.send({
        type: "completed",
        message: "本批流程已完成，全部单号和附件已保存。",
      });
    } catch (error) {
      this.send({
        type: error instanceof Paused ? "paused" : "failed",
        message:
          error instanceof Paused
            ? "已在当前操作结束后暂停，进度保留。"
            : error.message,
      });
    } finally {
      if (this.ownsClients)
        for (const client of this.clients.values()) client.close();
    }
  }
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href
) {
  const input = JSON.parse(await fs.readFile(process.argv[2], "utf8"));
  const worker = new ShippingWorkflow(input);
  const commands = readline.createInterface({ input: process.stdin });
  commands.on("line", (line) => {
    try {
      worker.approvals.receive(JSON.parse(line));
    } catch {}
  });
  commands.on("close", () => worker.approvals.receive({ type: "pause" }));
  await worker.run();
  commands.close();
}
