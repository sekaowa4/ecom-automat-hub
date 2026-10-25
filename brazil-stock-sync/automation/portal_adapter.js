function classifyUpSellerPage(url) {
  const value = String(url || '');
  if (/\/analytics\/product-sales(?:\?|$)/.test(value)) return 'product_sales';
  return 'unknown';
}

function classifyOmsPage(url) {
  const value = String(url || '');
  if (/\/web\/inventory\/index(?:\?|$)/.test(value)) return 'inventory';
  return 'unknown';
}

function isAuthenticated(snapshot) {
  const url = String(snapshot?.url || '').toLowerCase();
  const bodyText = String(snapshot?.bodyText || '');
  if (/login|signin|auth/.test(url)) return false;
  return bodyText.includes('导出') || bodyText.includes('库存') || bodyText.includes('产品销量');
}

function chooseExportButton(buttons) {
  return (buttons || []).find((button) => button?.visible && /导出|export/i.test(String(button.text || ''))) || null;
}

module.exports = {
  classifyUpSellerPage,
  classifyOmsPage,
  isAuthenticated,
  chooseExportButton,
};
