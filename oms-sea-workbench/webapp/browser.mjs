import { spawn } from "node:child_process";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { omsOrigin } from "./config.mjs";

export const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
export const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
export class BrowserError extends Error {}

export function resultText(response) {
  if (response.error || response.result?.isError)
    throw new BrowserError(
      response.error?.message ||
        response.result?.content
          ?.filter((c) => c.type === "text")
          .map((c) => c.text)
          .join("\n") ||
        "浏览器操作失败",
    );
  return (
    response.result?.content
      ?.filter((c) => c.type === "text")
      .map((c) => c.text)
      .join("\n") || ""
  );
}

export function parsePages(text) {
  return [
    ...text.matchAll(
      /^(\d+): (.*?) \((https?:\/\/[^\n]*?)\)(?: \[selected\])?$/gm,
    ),
  ]
    .map((m) => ({
      id: Number(m[1]),
      title: m[2],
      url: m[3],
      site:
        new URL(m[3]).origin === "https://app.upseller.com"
          ? "upseller"
          : new URL(m[3]).origin === omsOrigin
            ? "oms"
            : "other",
    }))
    .filter((p) => p.site !== "other");
}

export async function connect(source = "auto") {
  if (!["auto", "9222", "9223", "9224"].includes(source))
    throw new BrowserError("无效的本机浏览器通道");
  const cli = path.join(
    root,
    "node_modules/chrome-devtools-mcp/build/src/bin/chrome-devtools-mcp.js",
  );
  const route =
    source === "auto"
      ? ["--autoConnect"]
      : ["--browserUrl", `http://127.0.0.1:${source}`];
  const child = spawn(
    process.execPath,
    [
      cli,
      ...route,
      "--no-usage-statistics",
      "--no-performance-crux",
      "--redactNetworkHeaders",
      `--workspace=${root}`,
    ],
    { cwd: root, stdio: ["pipe", "pipe", "pipe"], windowsHide: true },
  );
  let serial = 0,
    buffer = "",
    diagnostics = "";
  const pending = new Map();
  child.stderr.on("data", (data) => {
    diagnostics = (diagnostics + data.toString()).slice(-3000);
  });
  child.stdout.on("data", (data) => {
    buffer += data;
    let end;
    while ((end = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, end);
      buffer = buffer.slice(end + 1);
      let message;
      try {
        message = JSON.parse(line);
      } catch {
        continue;
      }
      const waiter = pending.get(message.id);
      if (waiter) {
        clearTimeout(waiter.timer);
        pending.delete(message.id);
        waiter.resolve(message);
      }
    }
  });
  function rejectAll(error) {
    for (const waiter of pending.values()) {
      clearTimeout(waiter.timer);
      waiter.reject(error);
    }
    pending.clear();
  }
  child.on("error", (error) => rejectAll(error));
  child.on("exit", (code) =>
    rejectAll(
      new BrowserError(
        `专用 Chrome 连接中断 (${code})。${diagnostics.includes("MODULE_NOT_FOUND") ? "请先安装工作台依赖。" : "请检查专用 Chrome 是否仍然打开。"}`,
      ),
    ),
  );
  function request(method, params = {}) {
    return new Promise((resolve, reject) => {
      const id = ++serial;
      const timer = setTimeout(() => {
        pending.delete(id);
        reject(
          new BrowserError(
            "浏览器连接超时。请确认专用 Chrome 的远程调试已启用；本次不会循环申请授权。",
          ),
        );
      }, 60000);
      pending.set(id, { resolve, reject, timer });
      child.stdin.write(
        JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n",
      );
    });
  }
  await request("initialize", {
    protocolVersion: "2025-03-26",
    capabilities: {},
    clientInfo: { name: "oms-workbench", version: "1.0.0" },
  });
  child.stdin.write(
    JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) +
      "\n",
  );
  return {
    request,
    tool: async (name, args = {}) =>
      resultText(await request("tools/call", { name, arguments: args })),
    close() {
      child.kill();
    },
  };
}

export function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
export function findUid(snapshot, role, label) {
  const matches = [
    ...snapshot.matchAll(
      new RegExp(`uid=(\\S+) ${role} "${escapeRegex(label)}"(?: |$)`, "gm"),
    ),
  ];
  if (matches.length !== 1)
    throw new BrowserError(
      `“${label}”控件${matches.length ? "有多个匹配，需要核对当前页面" : "未出现，请核对页面或登录状态"}`,
    );
  return matches[0][1];
}

export class Page {
  constructor(client, id, origin = null) {
    this.client = client;
    this.id = Number(id);
    this.origin = origin;
  }
  async tool(name, args = {}) {
    if (
      this.origin &&
      [
        "click",
        "fill",
        "fill_form",
        "upload_file",
        "type_text",
        "press_key",
        "hover",
      ].includes(name)
    ) {
      const checked = await this.client.tool("evaluate_script", {
        pageId: this.id,
        function: `() => location.origin === ${JSON.stringify(this.origin)}`,
        waitForStableDom: false,
      });
      if (!/```json\s*true\s*```/.test(checked))
        throw new BrowserError(
          "当前标签页已离开指定业务网站，请核对后重新连接",
        );
    }
    return this.client.tool(name, { pageId: this.id, ...args });
  }
  snapshot() {
    return this.tool("take_snapshot");
  }
  async evaluate(fn) {
    const text = await this.tool("evaluate_script", {
      function: this.origin
        ? `async () => { if(location.origin !== ${JSON.stringify(this.origin)}) throw new Error('页面已离开指定业务网站'); return await (${fn.toString()})(); }`
        : fn.toString(),
      waitForStableDom: false,
    });
    const match = text.match(/```json\s*([\s\S]*?)\s*```/);
    if (!match) throw new BrowserError("无法读取页面执行结果");
    return JSON.parse(match[1]);
  }
  async navigate(url) {
    if (this.origin && new URL(url).origin !== this.origin)
      throw new BrowserError("导航地址与指定业务网站不一致");
    await this.tool("navigate_page", { type: "url", url });
    return this.snapshot();
  }
  async click(label, role = "button") {
    const snapshot = await this.snapshot();
    return this.tool("click", {
      uid: findUid(snapshot, role, label),
      includeSnapshot: false,
    });
  }
  async clickText(text) {
    const result = await this.evaluate(
      `() => { const text=${JSON.stringify(text)}; const els=[...document.querySelectorAll('button,a,[role="menuitem"],[role="option"],span,div')].filter(e=>e.getBoundingClientRect().width&&e.getBoundingClientRect().height&&e.innerText?.trim()===text&&!e.querySelector('button,a,[role="menuitem"],[role="option"]')&&!e.disabled); const minimal=els.filter(e=>!els.some(x=>x!==e&&e.contains(x))); if(minimal.length!==1) return {ok:false,count:minimal.length}; minimal[0].click(); return {ok:true}; }`,
    );
    if (!result.ok)
      throw new BrowserError(
        `无法唯一定位“${text}”（${result.count} 个），请核对当前页面`,
      );
  }
  async fillLabel(label, value) {
    const snapshot = await this.snapshot();
    const after = snapshot.split(`StaticText "${label}"`)[1];
    const match = after
      ?.slice(0, 650)
      .match(/uid=(\S+) (?:textbox|spinbutton)/);
    if (!match) throw new BrowserError(`找不到“${label}”输入框`);
    await this.tool("fill", { uid: match[1], value: String(value) });
  }
  async fillPlaceholder(placeholder, value) {
    const snapshot = await this.snapshot();
    await this.tool("fill", {
      uid: findUid(snapshot, "textbox", placeholder),
      value: String(value),
    });
  }
  async wait(text, timeout = 15000) {
    return this.tool("wait_for", { text: [text], timeout });
  }
  async body() {
    return this.evaluate("() => document.body.innerText");
  }
  async upload(label, files) {
    const snapshot = await this.snapshot();
    const expression = new RegExp(
      `uid=(\\S+) (?:generic|button|menuitem|StaticText) "${escapeRegex(label)}"`,
    );
    const match = snapshot.match(expression);
    if (!match) throw new BrowserError(`找不到上传入口“${label}”`);
    await this.tool("upload_file", { uid: match[1], filePaths: files });
  }

  async account() {
    return this.evaluate(
      `() => { const leaves=[...document.querySelectorAll('span,button,a')].filter(e=>e.getBoundingClientRect().top>=0&&e.getBoundingClientRect().top<100&&e.getBoundingClientRect().left>innerWidth-330&&e.getBoundingClientRect().width&&e.children.length===0); return leaves.map(e=>e.innerText?.trim()).find(t=>t&&t.length<60&&/[a-zA-Z][a-zA-Z0-9@._-]{4,}/.test(t))||''; }`,
    );
  }
}

export async function probeBrowser(
  pool = null,
  sources = ["auto", "9223", "9224", "9222"],
) {
  const pages = [],
    errors = [];
  for (const [index, source] of sources.entries()) {
    if (source !== "auto") {
      try {
        const response = await fetch(
          `http://127.0.0.1:${source}/json/version`,
          { signal: AbortSignal.timeout(1200) },
        );
        const version = await response.json();
        if (!version.webSocketDebuggerUrl) continue;
      } catch {
        continue;
      }
    }
    let client;
    try {
      client = pool?.get(source) || (await connect(source));
      for (const page of parsePages(await client.tool("list_pages"))) {
        let account = "";
        try {
          account = await new Page(client, page.id).account();
        } catch {}
        pages.push({
          ...page,
          id: index * 1000000 + page.id,
          page_id: page.id,
          source,
          account,
          login_required: /\/(login|signin|sign-in|passport|verify)(?:\/|$)/i.test(new URL(page.url).pathname) || /登录|\blogin\b/i.test(page.title),
        });
      }
      pool?.set(source, client);
    } catch (error) {
      errors.push(error.message);
      client?.close();
      pool?.delete(source);
    } finally {
      if (!pool) client?.close();
    }
  }
  return {
    ok: pages.length > 0,
    pages,
    ...(pages.length
      ? {}
      : {
          error: errors.length
            ? "未找到已经开启远程调试的专用 Chrome。请在原专用 Chrome 启用远程调试后，再点击连接浏览器。"
            : "已连接的 Chrome 中未发现 UpSeller 或 OMS 标签页，请先打开并登录。",
          detail: errors[0] || "",
        }),
  };
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href
) {
  let client;
  try {
    console.log(JSON.stringify(await probeBrowser()));
  } catch (error) {
    console.log(JSON.stringify({ ok: false, error: error.message }));
    process.exitCode = 1;
  } finally {
    client?.close();
  }
}
