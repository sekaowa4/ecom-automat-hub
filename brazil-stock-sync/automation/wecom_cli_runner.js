const path = require('node:path');
const { spawnSync } = require('node:child_process');

function quoteWindowsArg(value) {
  return `"${String(value).replace(/"/g, '^"')}"`;
}

function buildCliArgs(command, params) {
  return [
    'dlx', '--yes', '@wecom/cli', 'doc', command, JSON.stringify(params),
  ];
}

function runWeComCli({ pnpmPath, configDir, workDir = process.cwd(), command, params, spawnImpl = spawnSync, nodePath, pnpmModule }) {
  const args = buildCliArgs(command, params);
  const pnpmExecutable = nodePath || path.resolve(path.dirname(pnpmPath), '..', '..', 'node', 'bin', 'node.exe');
  const pnpmEntry = pnpmModule || path.resolve(path.dirname(pnpmPath), '..', '..', 'node', 'node_modules', 'pnpm', 'bin', 'pnpm.mjs');
  const result = spawnImpl(pnpmExecutable, [pnpmEntry, ...args], {
    cwd: workDir,
    env: { ...process.env, WECOM_CLI_CONFIG_DIR: configDir },
    encoding: 'utf8',
    windowsHide: true,
    shell: false,
  });

  if (result.error) throw result.error;
  if (result.status !== 0) {
    const detail = String(result.stderr || result.stdout || '').trim();
    throw new Error(`企业微信 CLI 执行失败（${result.status}）：${detail}`);
  }
  return String(result.stdout || '').trim();
}

module.exports = { buildCliArgs, quoteWindowsArg, runWeComCli };
