function selectPortalPage(context, hostname, preferredUrl = '') {
  if (preferredUrl) {
    const preferred = context.pages().find((candidate) => candidate.url().includes(preferredUrl));
    if (preferred) return preferred;
  }
  return context.pages().find((candidate) => candidate.url().includes(hostname));
}

module.exports = { selectPortalPage };
