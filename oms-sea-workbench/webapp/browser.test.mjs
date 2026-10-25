import test from "node:test";
import assert from "node:assert/strict";
import { chromium } from "playwright";
import { parsePages, findUid, Page, probeBrowser } from "./browser.mjs";
import { omsOrigin, billingConfig } from "./config.mjs";

test('missing billing settings block invoicing instead of choosing a recipient', () => {
  assert.throws(() => billingConfig({}), /local-config/);
  const billing = {shop: 'Demo', name: 'Demo', cnpj: '00.000.000/0001-00', company: 'Demo company'};
  assert.deepEqual(billingConfig({billing}), billing);
  assert.throws(() => billingConfig({billing: {...billing, cnpj: '123'}}), /local-config/);
});
import {
  ApprovalController,
  ShippingWorkflow,
  cents,
  checkQuantities,
  checkSavedOrder,
} from "./runner.mjs";

test("page discovery keeps business targets and exact origin", () => {
  const pages = parsePages(
    `2: 商品 - UpSeller (https://app.upseller.com/zh-CN/products/product-list) [selected]\n14: OMS (${omsOrigin}/web/product/all)\n15: Other (https://example.com/)`,
  );
  assert.deepEqual(
    pages.map((p) => [p.id, p.site]),
    [
      [2, "upseller"],
      [14, "oms"],
    ],
  );
  assert.deepEqual(
    parsePages(
      "0: fake (https://evil.example/?to=https://app.upseller.com/a)\n1: fake (https://fake.app.upseller.com/)",
    ),
    [],
  );
});

test("probe and resume reuse the same browser client without closing it", async () => {
  let reads = 0,
    closes = 0;
  const client = {
    tool: async (name) => {
      if (name === "list_pages") {
        reads++;
        return "0: 商品 - UpSeller (https://app.upseller.com/zh-CN/products/product-list)";
      }
      return '```json\n"account-name"\n```';
    },
    close() {
      closes++;
    },
  };
  const pool = new Map([["auto", client]]);
  assert.equal((await probeBrowser(pool, ["auto"])).pages[0].id, 0);
  await probeBrowser(pool, ["auto"]);
  assert.equal(reads, 2);
  assert.equal(closes, 0);
  assert.equal(pool.get("auto"), client);
});

test("a changed website origin blocks input before it is sent", async () => {
  const calls = [];
  const page = new Page(
    {
      tool: async (name) => {
        calls.push(name);
        return "```json\nfalse\n```";
      },
    },
    1,
    "https://app.upseller.com",
  );
  await assert.rejects(page.tool("fill", { uid: "1", value: "data" }), /离开/);
  assert.deepEqual(calls, ["evaluate_script"]);
});

test("saved order reconciliation rejects a wrong quantity or unit price", () => {
  const expected = [{ sku: "NW123", quantity: 156, unit_price: "12.00" }];
  checkSavedOrder(
    [{ sku: "NW123", text: "NW123 × 156 R$ 12,00 税务信息" }],
    expected,
  );
  assert.throws(
    () =>
      checkSavedOrder(
        [{ sku: "NW123", text: "NW123 × 2 R$ 12,00 税务信息" }],
        expected,
      ),
    /不一致/,
  );
});
test("ambiguous save button cannot trigger a write", () => {
  assert.throws(
    () =>
      findUid('uid=1_1 button "保存"\nuid=1_2 button "保存"', "button", "保存"),
    /多个/,
  );
  assert.equal(findUid('uid=1_1 button "保存"', "button", "保存"), "1_1");
});
test("approval waits for the exact nonce and pause cancels pending action", async () => {
  const events = [];
  const gate = new ApprovalController((e) => events.push(e));
  let released = false;
  const pending = gate.gate("submit", "确认", []).then(() => {
    released = true;
  });
  gate.receive({ type: "approve", id: "stale" });
  await Promise.resolve();
  assert.equal(released, false);
  gate.receive({ type: "approve", id: events[0].id });
  await pending;
  assert.equal(released, true);
  const cancelled = gate.gate("issue", "开票", []);
  gate.receive({ type: "pause" });
  await assert.rejects(cancelled);
});
test("money and quantities use exact cents, mismatches block progress", () => {
  assert.equal(cents("R$ 123.456,78"), 12345678);
  assert.equal(cents("16.80"), 1680);
  assert.throws(() => cents("-2,00"));
  const expected = [{ sku: "NW1", quantity: 156, unit_price: "12.00" }];
  checkQuantities([{ sku: "NW1", values: ["156", "12,00"] }], expected, true);
  assert.throws(
    () =>
      checkQuantities([{ sku: "NW1", values: ["2", "12,00"] }], expected, true),
    /件数/,
  );
  assert.throws(
    () =>
      checkQuantities(
        [{ sku: "NW1", values: ["156", "120,00"] }],
        expected,
        true,
      ),
    /单价/,
  );
});
test("actual DOM row matching survives SKU wrapping and keeps selectors scoped", async () => {
  const browser = await chromium.launch({ channel: "chrome", headless: true });
  try {
    const browserPage = await browser.newPage();
    await browserPage.setContent(
      '<table><tr><td><span>DEMO0001S<br>A</span></td><td><input value="156"></td><td><input value="12,00"></td><td><button onclick="window.clicked=1">税务信息</button></td></tr><tr><td><span>DEMO0002SA</span></td><td><input value="594"></td><td><input value="8,00"></td><td><button onclick="window.clicked=2">税务信息</button></td></tr></table>',
    );
    const page = { evaluate: (code) => browserPage.evaluate(`(${code})()`) };
    const worker = new ShippingWorkflow(
      {
        manifest: {
          items: [
            { sku: "DEMO0001SA", quantity: 156, unit_price: "12.00" },
            { sku: "DEMO0002SA", quantity: 594, unit_price: "8.00" },
          ],
        },
        records: {},
        completed_steps: [],
        job_dir: ".",
      },
      () => {},
    );
    const rows = await worker.editableRows(page, true);
    checkQuantities(rows, worker.items, true);
    await worker.clickWithin(page, "DEMO0002SA", "税务信息");
    assert.equal(await browserPage.evaluate(() => window.clicked), 2);
    await browserPage.setContent(
      "<div><label>仓库</label><select><option>请选择</option><option>BomJesus</option></select></div><div><label>原产地</label><select><option>0 - Nacional</option><option>2 - Estrangeira - Adquirida no mercado interno, exceto a indicada no código 7</option></select></div>",
    );
    await worker.selectLabel(page, "仓库", "BomJesus");
    await worker.selectLabel(
      page,
      "原产地",
      "2 - Estrangeira - Adquirida no mercado interno",
    );
    assert.deepEqual(
      await browserPage
        .locator("select")
        .evaluateAll((nodes) => nodes.map((n) => n.selectedOptions[0].text)),
      [
        "BomJesus",
        "2 - Estrangeira - Adquirida no mercado interno, exceto a indicada no código 7",
      ],
    );
  } finally {
    await browser.close();
  }
});
