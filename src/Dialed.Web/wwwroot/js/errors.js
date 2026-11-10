// Catches JavaScript errors from the first moment the page runs (#91, ADR-0012): uncaught errors, unhandled promise rejections and
// errors the service worker posts. Until the app is ready they wait in a short queue; then platform.js forwards each one to .NET,
// where ErrorReporter applies the opt-out, scrubbing and limits. Only the error name and stack leave this file; never the message.
(function () {
    const maxQueued = 20;
    const queue = [];
    let forward = null;

    function capture(kind, error, fallbackFrame) {
        try {
            const isError = error !== null && typeof error === 'object' && typeof error.stack === 'string';
            const report = {
                kind,
                // A rejection with a plain value (reject('text')) has no name; the value itself is never sent.
                name: error && typeof error === 'object' && typeof error.name === 'string' && error.name
                    ? error.name
                    : (kind === 'rejection' ? 'UnhandledRejection' : 'ScriptError'),
                stack: isError && error.stack ? error.stack : (fallbackFrame || '')
            };
            if (forward) {
                forward(report);
            } else if (queue.length < maxQueued) {
                queue.push(report);
            }
        } catch {
            // Reporting must never cause a second error.
        }
    }

    window.addEventListener('error', event => {
        // Resource load errors (an image that fails to load) reach this listener without being script errors.
        if (!(event instanceof ErrorEvent)) {
            return;
        }
        const frame = event.filename ? `at ${event.filename}:${event.lineno || 0}:${event.colno || 0}` : '';
        capture('script', event.error, frame);
    });

    window.addEventListener('unhandledrejection', event => capture('rejection', event.reason, ''));

    if ('serviceWorker' in navigator) {
        navigator.serviceWorker.addEventListener('message', event => {
            const data = event.data;
            if (data && data.type === 'dialed-sw-error') {
                capture('service-worker', { name: data.name, stack: typeof data.stack === 'string' ? data.stack : '' }, '');
            }
        });
    }

    // Called by platform.js once .NET listens; sends what was caught so far.
    window.dialedErrors = {
        connect(handler) {
            forward = handler;
            for (const report of queue.splice(0)) {
                try {
                    handler(report);
                } catch {
                    // see capture
                }
            }
        }
    };
})();
