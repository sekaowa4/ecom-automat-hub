const REQUIRED_SOURCES = ['inventory', 'full', '3天', '7天', '15天', '30天', 'target'];

function validateSourceManifest(manifest) {
  const missing = REQUIRED_SOURCES.filter((source) => !manifest || !manifest[source]);
  return {
    ok: missing.length === 0,
    missing,
    sources: Object.fromEntries(
      REQUIRED_SOURCES.filter((source) => manifest?.[source]).map((source) => [source, manifest[source]]),
    ),
  };
}

module.exports = { REQUIRED_SOURCES, validateSourceManifest };
