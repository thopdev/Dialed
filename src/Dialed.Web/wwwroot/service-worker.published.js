// Caches the app shell and the .NET runtime so Dialed starts offline (#115).
// Updates: a new worker installs in the background and WAITS; the app shows "Update available" and,
// when the user accepts, sends SKIP_WAITING so the switch happens at a moment they choose.
// Caching of API data is not done here - that is the local database's job (#37/#38).

self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall()));
self.addEventListener('activate', event => event.waitUntil(onActivate()));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));
self.addEventListener('message', event => {
    if (event.data === 'SKIP_WAITING') {
        self.skipWaiting();
    }
});

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.webmanifest$/];
const offlineAssetsExclude = [/^service-worker\.js$/, /^icons\/splash\//]; // splash screens are only used by iOS at launch; keep them out of the offline cache

// Replace with the real base path if the app is hosted below the root.
const base = '/';
const baseUrl = new URL(base, self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

async function onInstall() {
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
}

async function onActivate() {
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
}

async function onFetch(event) {
    if (event.request.method !== 'GET') {
        return fetch(event.request);
    }
    const url = new URL(event.request.url);
    // API calls and other origins go straight to the network.
    if (url.origin !== self.location.origin) {
        return fetch(event.request);
    }
    // Page navigations get the app shell so deep links work offline (and in the installed app).
    const shouldServeIndexHtml = event.request.mode === 'navigate'
        && !manifestUrlList.some(u => u === event.request.url);
    const request = shouldServeIndexHtml ? 'index.html' : event.request;
    const cache = await caches.open(cacheName);
    const cached = await cache.match(request);
    return cached || fetch(event.request);
}
