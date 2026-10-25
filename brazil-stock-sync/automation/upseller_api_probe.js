const SENSITIVE_HEADERS = new Set(['authorization', 'cookie', 'set-cookie', 'x-api-key']);

function redactRequest({ url, method, headers = {}, postData = null }) {
  const safeHeaders = Object.fromEntries(
    Object.entries(headers).filter(([name]) => !SENSITIVE_HEADERS.has(String(name).toLowerCase())),
  );
  return { url, method, headers: safeHeaders, postData };
}

function recordPageRequests(page, sink) {
  page.on('request', (request) => {
    const url = request.url();
    if (!/upseller\.com|print-label\.upseller\.cn/.test(url)) return;
    sink(redactRequest({
      url,
      method: request.method(),
      headers: request.headers(),
      postData: request.postData(),
    }));
  });
}

module.exports = { redactRequest, recordPageRequests };
