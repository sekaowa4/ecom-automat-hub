async a => {
  const text = e => (e?.innerText || e?.textContent || '').trim();
  const cleanId = value => String(value || '').replace(/^#?MLB/i, '').replace(/^#/, '').trim();
  const sku = String(a?.sku || '').trim();
  const itemId = cleanId(a?.itemId);
  const more = /^(查看更多促销|查看更多促销|查看更多促销活动|查看更多促销活动|ver mais promoções|see more promotions)$/i;
  const buttons = new Set();
  for (const row of document.querySelectorAll('.sc-list-row')) {
    const rowSku = text(row.querySelector('.sc-list-description__sku')).replace(/^SKU\s+/i, '');
    const rowId = cleanId(text(row.querySelector('.sc-list-description__id')));
    if (rowSku !== sku || itemId && rowId !== itemId) continue;
    const candidates = new Set(row.querySelectorAll('button, [role="button"], [data-testid*="show-more"], [data-testid*="more-promotions"]'));
    const group = row.closest('.sc-list-row-group');
    if (group) for (const button of group.querySelectorAll('button, [role="button"], [data-testid*="show-more"], [data-testid*="more-promotions"]')) {
      if (button.closest('.sc-list-row-group') === group && !button.closest('.sc-list-row')) candidates.add(button);
    }
    for (const button of candidates) {
      const label = (button.getAttribute('aria-label') || text(button)).replace(/\s+/g, ' ').trim();
      if (!button.disabled && button.getAttribute('aria-expanded') !== 'true' && more.test(label)) buttons.add(button);
    }
  }
  if (buttons.size) {
    const scopes = new Set();
    for (const button of buttons) scopes.add(button.closest('.sc-list-row') || button.closest('.sc-list-row-group') || button.parentElement);
    await new Promise(resolve => {
      const started = performance.now();
      let lastMutation = started;
      let finished = false;
      const observer = new MutationObserver(() => { lastMutation = performance.now(); });
      for (const scope of scopes) if (scope) observer.observe(scope, {subtree:true,childList:true,characterData:true,attributes:true});
      const finish = () => { if (finished) return; finished = true; clearInterval(timer); observer.disconnect(); resolve(); };
      const timer = setInterval(() => {
        const elapsed = performance.now() - started;
        if (elapsed >= 2500 && performance.now() - lastMutation >= 2500 || elapsed >= 10000) finish();
      }, 100);
      for (const button of buttons) button.click();
    });
  }
  return buttons.size;
}
