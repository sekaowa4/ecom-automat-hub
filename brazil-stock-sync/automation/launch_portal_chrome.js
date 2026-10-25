const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { buildChromeLaunchArgs } = require('./chrome_connection');

function chromeExecutable(env = process.env) {
  const candidates = [
    env.BRAZIL_STOCK_CHROME,
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
  ].filter(Boolean);
  return candidates.find((candidate) => fs.existsSync(candidate)) || '';
}

function launchPortalChrome({ chromePath = chromeExecutable(), profileDir = path.join(__dirname, '..', 'runtime', 'portal-profile-chrome'), spawnImpl = spawn, mkdirImpl = fs.mkdirSync } = {}) {
  if (!chromePath) throw new Error('未找到 Google Chrome，请安装 Chrome 后重试');
  mkdirImpl(profileDir, { recursive: true });
  const child = spawnImpl(chromePath, buildChromeLaunchArgs(profileDir), {
    detached: true,
    stdio: 'ignore',
    windowsHide: false,
  });
  child.unref();
  return { started: true };
}

if (require.main === module) {
  try {
    process.stdout.write(`${JSON.stringify(launchPortalChrome())}\n`);
  } catch (error) {
    process.stderr.write(`${error.stack || error}\n`);
    process.exitCode = 1;
  }
}

module.exports = { chromeExecutable, launchPortalChrome };
