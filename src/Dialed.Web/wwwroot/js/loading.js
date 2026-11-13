// The loading screen in index.html brews along with the download: a bloom, a rest, a first pour, a rest, a second pour, and "Dialed"
// when the app is almost there. Blazor writes its progress to --blazor-load-percentage on <html>; this turns it into a stage name on
// the screen (data-stage), and css/app.css draws each stage. Nothing else depends on this file: without it the screen stays on the bloom.
(function () {
    // Where each stage starts, in percent of the download.
    const stages = [
        [90, 'done'],
        [68, 'pour2'],
        [58, 'rest2'],
        [28, 'pour1'],
        [18, 'rest1'],
        [0, 'bloom']
    ];
    const root = document.documentElement;
    let current = '';

    function update() {
        const screen = document.querySelector('.dialed-loading');
        const value = parseFloat(root.style.getPropertyValue('--blazor-load-percentage'));
        if (!screen || isNaN(value)) {
            return;
        }

        const stage = stages.find(function (entry) { return value >= entry[0]; })[1];
        if (stage !== current) {
            current = stage;
            screen.setAttribute('data-stage', stage);
        }

        if (stage === 'done') {
            observer.disconnect();
        }
    }

    const observer = new MutationObserver(update);
    observer.observe(root, { attributes: true, attributeFilter: ['style'] });
    update();
})();
