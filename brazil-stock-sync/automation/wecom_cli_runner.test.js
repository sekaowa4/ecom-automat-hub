const test = require('node:test');
const assert = require('node:assert/strict');

const { buildCliArgs, runWeComCli } = require('./wecom_cli_runner');

test('passes CLI JSON as one intact argument on Windows', () => {
  const args = buildCliArgs('sheet_get_info', {
    url: 'https://doc.weixin.qq.com/sheet/test?scode=abc',
  });

  assert.deepEqual(args, [
    'dlx', '--yes', '@wecom/cli', 'doc', 'sheet_get_info',
    '{"url":"https://doc.weixin.qq.com/sheet/test?scode=abc"}',
  ]);
});

test('invokes pnpm cmd wrapper through CALL on Windows', () => {
  let captured;
  runWeComCli({
    pnpmPath: 'C:/tools/pnpm.cmd',
    configDir: 'D:/project/runtime/wecom-cli',
    command: 'sheet_get_info',
    params: { url: 'https://example.test/sheet' },
    spawnImpl: (command, args) => {
      captured = { command, args };
      return { status: 0, stdout: '{"ok":true}', stderr: '' };
    },
    nodePath: 'node.exe',
    pnpmModule: 'pnpm.mjs',
  });

  assert.equal(captured.command, 'node.exe');
  assert.deepEqual(captured.args.slice(0, 2), ['pnpm.mjs', 'dlx']);
});

test('uses the project directory as CLI cwd, not the encrypted config directory', () => {
  let captured;
  runWeComCli({
    pnpmPath: 'C:/tools/pnpm.cmd',
    configDir: 'D:/project/runtime/wecom-cli',
    workDir: 'D:/project',
    command: 'sheet_get_info',
    params: { url: 'https://example.test/sheet' },
    spawnImpl: (command, args, options) => {
      captured = options;
      return { status: 0, stdout: '{"ok":true}', stderr: '' };
    },
    comspec: 'cmd.exe',
  });

  assert.equal(captured.cwd, 'D:/project');
});
