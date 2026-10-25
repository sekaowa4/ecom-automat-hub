const assert = require('node:assert/strict');
const test = require('node:test');
const { launchPortalChrome } = require('./launch_portal_chrome');

test('starts Chrome detached with the automation profile', () => {
  const calls = [];
  const result = launchPortalChrome({
    chromePath: 'C:/Chrome/chrome.exe',
    profileDir: 'D:/runtime/portal-profile-chrome',
    spawnImpl: (command, args, options) => {
      calls.push({ command, args, options });
      return { unref: () => calls.push({ unref: true }) };
    },
    mkdirImpl: (directory, options) => calls.push({ directory, options }),
  });

  assert.equal(result.started, true);
  assert.deepEqual(calls[0], { directory: 'D:/runtime/portal-profile-chrome', options: { recursive: true } });
  assert.equal(calls[1].command, 'C:/Chrome/chrome.exe');
  assert.equal(calls[1].args[0], '--remote-debugging-port=9222');
  assert.equal(calls[1].options.detached, true);
  assert.deepEqual(calls[2], { unref: true });
});
