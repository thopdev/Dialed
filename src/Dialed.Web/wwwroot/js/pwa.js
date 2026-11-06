// Browser side of the PWA support (#115): registers the service worker, reports updates to Blazor
// and reports how the app is running (installed or in a browser tab).

let registration = null;
let reloading = false;

export async function initialize(dotNetRef) {
    const ua = navigator.userAgent;
    const iPadOS = navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1;
    const ios = /iPhone|iPad|iPod/.test(ua) || iPadOS;
    const standalone = window.matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
    // Only Safari on iOS can add to the home screen; Chrome/Firefox/Edge on iOS cannot do it themselves.
    const safari = ios && !/CriOS|FxiOS|EdgiOS|OPiOS/.test(ua);
    const info = { isStandalone: standalone, isIosSafari: safari };

    if ('serviceWorker' in navigator) {
        try {
            registration = await navigator.serviceWorker.register('service-worker.js', { updateViaCache: 'none' });
            const notify = () => dotNetRef.invokeMethodAsync('OnUpdateAvailable');
            if (registration.waiting && navigator.serviceWorker.controller) {
                notify();
            }
            registration.addEventListener('updatefound', () => {
                const worker = registration.installing;
                worker?.addEventListener('statechange', () => {
                    // "installed" with an existing controller = an update, not the very first install.
                    if (worker.state === 'installed' && navigator.serviceWorker.controller) {
                        notify();
                    }
                });
            });
            navigator.serviceWorker.addEventListener('controllerchange', () => {
                if (!reloading) {
                    reloading = true;
                    window.location.reload();
                }
            });
            // Look for a new version whenever the app comes back to the foreground.
            document.addEventListener('visibilitychange', () => {
                if (document.visibilityState === 'visible') {
                    registration.update().catch(() => { });
                }
            });
        } catch (error) {
            console.warn('Service worker registration failed', error);
        }
    }
    return info;
}

export function applyUpdate() {
    if (registration?.waiting) {
        registration.waiting.postMessage('SKIP_WAITING');
    } else {
        window.location.reload();
    }
}
