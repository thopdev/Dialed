// The loading screen in index.html says "Dialing" while the app downloads and switches to "Dialed" when it is almost there.
// Blazor writes its progress to --blazor-load-percentage on <html>; this watches it. Nothing else depends on this file: without it
// the screen simply keeps saying "Dialing" until the app starts.
(function () {
    const threshold = 90;
    const root = document.documentElement;
    let done = false;

    function check() {
        if (done) {
            return;
        }

        const value = parseFloat(root.style.getPropertyValue('--blazor-load-percentage'));
        if (value >= threshold) {
            const screen = document.querySelector('.dialed-loading');
            if (screen) {
                screen.classList.add('dialed-loading-done');
                done = true;
                observer.disconnect();
            }
        }
    }

    const observer = new MutationObserver(check);
    observer.observe(root, { attributes: true, attributeFilter: ['style'] });
    check();
})();
