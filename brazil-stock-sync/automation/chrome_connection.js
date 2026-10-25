const DEFAULT_ENDPOINT = 'http://127.0.0.1:9222';
const WORKBENCH_URL = 'http://127.0.0.1:8765/';
const UPSeller_LOGIN_URL = 'https://app.upseller.com/zh-CN/analytics/product-sales?warehouseId=';
const CONNECT_ATTEMPTS = 4;
const CONNECT_TIMEOUT_MS = 30_000;
const CONNECT_RETRY_DELAY_MS = 1_500;

function getChromeConnectionConfig(env = process.env) {
  return {
    mode: 'cdp',
    endpoint: String(env.BRAZIL_STOCK_CDP_ENDPOINT || DEFAULT_ENDPOINT).trim() || DEFAULT_ENDPOINT,
  };
}

function buildChromeLaunchArgs(profileDir, endpoint = DEFAULT_ENDPOINT) {
  const port = new URL(endpoint).port || '9222';
  return [
    `--remote-debugging-port=${port}`,
    `--user-data-dir=${profileDir}`,
    WORKBENCH_URL,
    UPSeller_LOGIN_URL,
  ];
}

async function connectToExistingChrome(chromium, env = process.env, options = {}) {
  const config = getChromeConnectionConfig(env);
  const attempts = options.attempts ?? CONNECT_ATTEMPTS;
  const timeout = options.timeout ?? CONNECT_TIMEOUT_MS;
  const delay = options.delay ?? CONNECT_RETRY_DELAY_MS;
  const sleep = options.sleep || ((ms) => new Promise((resolve) => setTimeout(resolve, ms)));
  let lastError;
  for (let attempt = 1; attempt <= attempts; attempt += 1) {
    try {
      const browser = await chromium.connectOverCDP(config.endpoint, { timeout });
      const contexts = browser.contexts();
      if (!contexts.length) throw new Error('Chrome 调试连接已建立，但没有可用浏览器上下文');
      return contexts[0];
    } catch (error) {
      lastError = error;
      if (attempt < attempts) await sleep(delay);
    }
  }
  throw new Error(`连接专用 Chrome 调试端口失败（已重试 ${attempts} 次）：${lastError?.message || lastError}`);
}

module.exports = { DEFAULT_ENDPOINT, WORKBENCH_URL, UPSeller_LOGIN_URL, getChromeConnectionConfig, buildChromeLaunchArgs, connectToExistingChrome };
