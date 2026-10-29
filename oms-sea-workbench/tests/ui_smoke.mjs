import { chromium } from "playwright";
import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { localConfig } from '../webapp/config.mjs';

const base = process.env.OMS_UI_BASE;
if (!base?.startsWith("http://127.0.0.1:"))
  throw new Error("UI tests require a disposable loopback fixture server");
const screenshotDir = path.join(os.tmpdir(), 'oms-workbench-ui');
await fs.mkdir(screenshotDir, { recursive: true });
const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  const page = await browser.newPage({
    viewport: { width: 1536, height: 1024 },
  });
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto(base);
  await page
    .getByRole("heading", { name: "批次任务", exact: true })
    .waitFor();
  assert.equal(await page.locator('.batch-toggle').count(), 0);
  await page.screenshot({
    path: path.join(screenshotDir, 'web-workbench-preview.png'),
    fullPage: true,
  });
  const buffer = await fs.readFile(
    localConfig.acceptance_files?.source || (() => { throw new Error('Configure local acceptance_files.source before this optional UI suite'); })(),
  );
  const uploadFinished = page.waitForResponse(
    response => response.url().includes('/api/jobs?filename=') && response.request().method() === 'POST',
    { timeout: 120000 },
  );
  await page
    .locator("input[type=file]")
    .setInputFiles({
      name: "第十七批_界面验收.xlsx",
      mimeType:
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
      buffer,
    });
  const uploadResponse = await uploadFinished;
  assert.equal(uploadResponse.status(), 201);
  await uploadResponse.finished();
  const batch = page.locator('.batch-toggle').filter({ hasText: '第十七批' });
  await batch.waitFor();
  await page.locator('.batch-detail').waitFor();
  await page.waitForFunction(() => [...document.querySelectorAll('.batch-detail img')].every(img => img.complete));
  assert.equal(await batch.getAttribute('aria-expanded'), 'true');
  assert.equal(await page.getByLabel('正清表对应批次单价子表').inputValue(), '');
  await batch.click();
  assert.equal(await page.locator('.batch-detail').count(), 0);
  await batch.click();
  await page.waitForFunction(() => [...document.querySelectorAll('.batch-detail img')].every(img => img.complete));
  await page.getByRole('tab', { name: '运行记录', exact: true }).click();
  await page.getByRole('heading', { name: '运行记录', exact: true }).waitFor();
  await page.getByRole('tab', { name: '文件与单号', exact: true }).click();
  await page.getByText('尚未生成单据或附件。').waitFor();
  await page.getByRole('tab', { name: /^商品明细/ }).click();
  assert.equal(await page.locator('.button.primary').first().evaluate(el => getComputedStyle(el).backgroundColor), 'rgb(255, 77, 0)');
  const state = await (await fetch(base + "/api/state")).json();
  const job = state.jobs[0];
  const expected = localConfig.acceptance_checks;
  assert.equal(job.manifest.totals.skus, expected.skus);
  assert.equal(job.manifest.totals.quantity, expected.quantity);
  assert.equal(job.manifest.totals.amount, '0.00'); // Do not use manifest prices before online batch matching.
  assert.equal(job.status, "ready");
  await page
    .getByRole("textbox", { name: "搜索 SKU 或名称" })
    .fill(expected.search_sku);
  assert.equal(await page.locator("tbody tr").count(), 1);
  assert.ok((await page.locator("tbody tr").innerText()).includes(String(expected.search_quantity)));
  await page.getByRole("textbox", { name: "搜索 SKU 或名称" }).clear();
  await page.reload();
  await page.locator('.batch-detail').waitFor();
  await page.waitForFunction(() => [...document.querySelectorAll('.batch-detail img')].every(img => img.complete));
  assert.equal(await page.locator('.sku-cell img').count(), 0); // Unmatched manifests must not display embedded source images.
  await page.screenshot({
    path: path.join(screenshotDir, 'web-workbench-batch-preview.png'),
    fullPage: true,
  });
  await page.getByRole("button", { name: "历史记录", exact: true }).click();
  await page.getByRole("heading", { name: "历史记录", exact: true }).waitFor();
  await page.getByRole("button", { name: "查看", exact: true }).click();
  await page.getByRole("button", { name: "开始处理", exact: true }).click();
  await page.getByRole("heading", { name: "连接设置", exact: true }).waitFor();
  await page.getByRole('heading', {name: '企微资料连接（只读）', exact: true}).waitFor();
  await page.getByLabel('产品图来源表链接').fill('https://doc.weixin.qq.com/sheet/e3_demo_image');
  await page.getByLabel('正清表链接（尺寸重量及批次单价）').fill('https://doc.weixin.qq.com/sheet/e3_demo_size');
  assert.equal(await page.getByRole('button', {name: '连接企微', exact: true}).isEnabled(), true);
  await page.screenshot({path: path.join(screenshotDir, 'web-workbench-wecom-settings.png'), fullPage: true});
  assert.equal(
    await page
      .getByRole("button", { name: "保存连接设置", exact: true })
      .isDisabled(),
    true,
  );
  await page.getByRole("button", { name: "当前批次", exact: true }).click();
  await page.getByRole('tab', { name: '文件与单号', exact: true }).click();
  await fetch(base + "/_test/gate", {
    method: "POST",
    body: JSON.stringify({ id: job.id }),
  });
  await page.getByRole("button", { name: "确认并继续", exact: true }).waitFor();
  assert.equal(await page.getByRole('tab', { name: '文件与单号', exact: true }).getAttribute('aria-selected'), 'true');
  const stale = await fetch(`${base}/api/jobs/${job.id}/approve`, {
    method: "POST",
    body: JSON.stringify({ id: "stale-nonce" }),
  });
  assert.equal(stale.status, 400);
  await batch.click();
  assert.equal(await page.getByRole('button', { name: '新建批次', exact: true }).isDisabled(), true);
  await page.getByRole('button', { name: '查看进行中批次', exact: true }).click();
  await page.getByRole('button', { name: '确认并继续', exact: true }).waitFor();
  await page.getByRole("button", { name: "确认并继续", exact: true }).click();
  await page
    .getByRole("button", { name: "确认并继续", exact: true })
    .waitFor({ state: "detached" });
  const result = await (
    await fetch(base + "/_test/finish", {
      method: "POST",
      body: JSON.stringify({ id: job.id }),
    })
  ).json();
  assert.deepEqual(JSON.parse(result.commands), {
    type: "approve",
    id: "qa-submit-nonce",
  });
  await page
    .locator(".summary .job-status")
    .filter({ hasText: "已暂停" })
    .waitFor();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({
    path: path.join(screenshotDir, 'web-workbench-mobile.png'),
    fullPage: true,
  });
  assert.equal(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
    true,
  );
  assert.deepEqual(errors, []);
  await page.setViewportSize({ width: 1536, height: 1024 });
  const extraResponse = await fetch(base + '/api/jobs?filename=' + encodeURIComponent('第十八批.csv'), {
    method: 'POST',
    body: '产品,SKU,葡语名称,总数量,单套包装长,单套包装宽,单套包装高,单套含包装重量（kg）,开票单价\n示例,DEMO260102SA,Produto,156,10,20,30,0.5,12\n',
  });
  assert.equal(extraResponse.status, 201);
  const extraJob = await extraResponse.json();
  const extraBatch = page.locator('.batch-toggle').filter({ hasText: '第十八批' });
  await extraBatch.waitFor();
  await extraBatch.click();
  await page.getByRole('textbox', { name: '搜索 SKU 或名称' }).fill('DEMO260102SA');
  assert.equal(await page.locator('tbody tr').count(), 1);
  await fetch(base + '/_test/complete', { method: 'POST', body: JSON.stringify({ id: extraJob.id }) });
  await extraBatch.locator('.batch-status.completed').waitFor();
  assert.equal(await page.getByRole('button', { name: '开始处理', exact: true }).isDisabled(), true);
  await page.locator('.batch-filters button').filter({ hasText: '已完成' }).click();
  assert.equal(await page.locator('.batch-toggle').count(), 1);
  assert.match(await page.locator('.batch-toggle').innerText(), /第十八批/);
  await page.locator('.batch-filters button').filter({ hasText: '全部批次' }).click();
  await page.getByRole('textbox', { name: '搜索批次或单号' }).fill('第十七批');
  assert.equal(await page.locator('.batch-toggle').count(), 1);
  await batch.click();
  await page.getByRole('textbox', { name: '搜索 SKU 或名称' }).fill(expected.search_sku);
  assert.ok((await page.locator('tbody tr').innerText()).includes(String(expected.search_quantity)));
  assert.deepEqual(errors, []);
  console.log(
    JSON.stringify({
      ok: true,
      screenshots: screenshotDir,
      checks: [
        "actual XLSX upload",
        "batch accordion and detail tabs",
        "confirmation remains visible on files tab",
        "collapsed active batch still blocks upload",
        "multiple batch selection and search",
        "completed batch cannot start",
        "configured SKU count and K-column total",
        "manifest prices ignored until online batch E-column matching",
        "SKU search",
        "refresh recovery",
        "history",
        "connection gating",
        "stale approval rejected",
        "current approval executed once",
        "390px responsive layout",
        "no browser errors",
      ],
    }),
  );
} finally {
  await browser.close();
}
