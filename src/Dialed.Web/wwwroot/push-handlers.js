// What the service worker does with a push (#100). Loaded by service-worker.js and service-worker.published.js through importScripts.
// A push carries a title, one line, a relative link and a tag: never anything private (see docs/push-notifications.md).
(function (scope) {
    // Only a path inside the app: no scheme, no host, no protocol-relative links.
    function safeLink(link) {
        return typeof link === 'string' && /^[a-z0-9][a-z0-9/_#?=&.-]*$/i.test(link) ? link : '';
    }

    function readPush(event) {
        try {
            const data = event.data ? event.data.json() : null;
            return data && typeof data.title === 'string' && data.title ? data : null;
        } catch {
            return null;
        }
    }

    async function onPush(event) {
        const data = readPush(event);
        if (!data) return;
        const pages = await scope.clients.matchAll({ type: 'window', includeUncontrolled: true });
        // The window the person is looking at already shows the news: tell it instead of interrupting with a banner.
        const open = pages.filter(page => page.visibilityState === 'visible' && page.focused);
        if (open.length > 0) {
            open.forEach(page => page.postMessage({ type: 'dialed-push', url: safeLink(data.url), tag: data.tag || '' }));
            return;
        }
        await scope.registration.showNotification(data.title, {
            body: typeof data.body === 'string' ? data.body : '',
            tag: typeof data.tag === 'string' && data.tag ? data.tag : undefined,
            icon: 'icons/icon-192.png',
            badge: 'icons/icon-192.png',
            data: { url: safeLink(data.url) }
        });
    }

    async function onClick(event) {
        event.notification.close();
        const link = safeLink(event.notification.data && event.notification.data.url);
        const target = new URL(link, scope.registration.scope).href;
        const pages = await scope.clients.matchAll({ type: 'window', includeUncontrolled: true });
        const page = pages.find(p => p.url.startsWith(scope.registration.scope));
        if (page) {
            // js/pwa.js receives this and navigates inside the running app, so nothing reloads.
            page.postMessage({ type: 'dialed-navigate', url: link });
            await page.focus();
            return;
        }
        await scope.clients.openWindow(target);
    }

    scope.addEventListener('push', event => event.waitUntil(onPush(event)));
    scope.addEventListener('notificationclick', event => event.waitUntil(onClick(event)));
})(self);
