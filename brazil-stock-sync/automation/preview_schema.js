const BLOCKED_STATUSES = new Set(['login_required', 'unreachable_or_timeout', 'error']);

function buildPreviewSummary(results) {
  const portals = (results || []).map((result) => {
    const portal = {
      name: String(result.name || ''),
      status: String(result.status || 'error'),
    };
    if (result.url) portal.url = String(result.url);
    if (result.title) portal.title = String(result.title);
    if (result.pageType) portal.pageType = String(result.pageType);
    if (result.error) portal.error = String(result.error);
    return portal;
  });
  const readyCount = portals.filter((portal) => portal.status === 'ready_for_export').length;
  const blockedCount = portals.filter((portal) => BLOCKED_STATUSES.has(portal.status)).length;
  return {
    mode: 'preview_only',
    overallStatus: portals.length > 0 && blockedCount === 0 ? 'ready' : 'blocked',
    readyCount,
    blockedCount,
    portals,
  };
}

module.exports = { buildPreviewSummary };
