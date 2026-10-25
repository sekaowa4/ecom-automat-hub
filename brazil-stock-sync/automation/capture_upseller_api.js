const fs = require('node:fs');
const path = require('node:path');
const { runBatch } = require('./batch_export');
const { redactRequest } = require('./upseller_api_probe');

const output = path.resolve(__dirname, '..', 'runtime', 'upseller_api_requests.redacted.json');
const captured = new Map();

function flush(status, error = '') {
  fs.writeFileSync(output, JSON.stringify({
    capturedAt: new Date().toISOString(),
    status,
    error,
    requests: [...captured.values()],
  }, null, 2));
}

function capture(request) {
  const item = redactRequest({
    url: request.url(),
    method: request.method(),
    headers: request.headers(),
    postData: request.postData(),
  });
  if (!/\/api\/|export|download|report|task/i.test(item.url)) return;
  captured.set(`${item.method} ${item.url} ${item.postData || ''}`, item);
  flush('running');
}

runBatch({ onRequest: capture }).then((result) => {
  flush('completed');
  process.stdout.write(`${output}\n`);
}).catch((error) => {
  flush('failed', String(error.stack || error));
  process.stderr.write(`${error.stack || error}\n`);
  process.exit(1);
});
