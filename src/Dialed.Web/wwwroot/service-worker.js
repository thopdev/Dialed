// Development: no caching, so code changes always show up. The published build uses service-worker.published.js.
self.importScripts('./push-handlers.js');
self.addEventListener('fetch', () => { });
