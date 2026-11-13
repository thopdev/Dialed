// The loading screen in index.html brews along with the download: a bloom, a rest, a first pour, a rest, a second pour, and "Dialed"
// when the app is almost there. Blazor writes its progress to --blazor-load-percentage on <html>; this turns it into a stage name on
// the screen (data-stage), and css/app.css draws each stage. Nothing else depends on this file: without it the screen stays on the bloom,
// in English. The words follow the app language: the app takes its UI culture from the browser language (there is no language setting),
// so this reads the same navigator.languages before .NET has started and swaps in Dutch where needed. "Dialed" is the brand and stays.
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
    // Words per language. English is also in index.html as the fallback. Dutch home baristas say "bloom" (also "pre-infusie"), so it stays.
    const words = {
        nl: { '.dialed-loading-pour-word': 'Gieten' }
    };
    const root = document.documentElement;
    let current = '';
    let wordsSet = false;

    function language() {
        const preferred = (navigator.languages && navigator.languages[0]) || navigator.language || '';
        return String(preferred).slice(0, 2).toLowerCase();
    }

    function setWords(screen) {
        wordsSet = true;
        const map = words[language()];
        if (!map) {
            return;
        }

        Object.keys(map).forEach(function (selector) {
            const element = screen.querySelector(selector);
            if (element) {
                element.textContent = map[selector];
                element.setAttribute('lang', language());
            }
        });
    }

    function update() {
        const screen = document.querySelector('.dialed-loading');
        const value = parseFloat(root.style.getPropertyValue('--blazor-load-percentage'));
        if (screen && !wordsSet) {
            setWords(screen);
        }

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
    // The script runs in <head>, before the screen exists: set the words as soon as the page is parsed, not only at the first progress update.
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', update);
    }
})();
