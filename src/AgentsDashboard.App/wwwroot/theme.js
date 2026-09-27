// Sets data-theme on the page before anything is drawn, so the window never
// flashes the default palette first. That is why this is a blocking script in
// the head rather than part of app.js at the end of the body.
//
// The server writes the stored choice as data-theme-choice. "system" is not a
// palette: it resolves to dark or light here, and again whenever the OS changes.
// Everything else that colours itself (Monaco, Mermaid) asks isDark(), which
// reads the palette's own color-scheme, so a new theme in app.css needs nothing
// here.
(() => {
    const root = document.documentElement;
    const light = window.matchMedia ? window.matchMedia('(prefers-color-scheme: light)') : null;

    function apply() {
        const choice = root.dataset.themeChoice || 'system';
        const theme = choice === 'system' ? (light && light.matches ? 'light' : 'dark') : choice;
        if (root.dataset.theme === theme) {
            return;
        }

        root.dataset.theme = theme;
        window.dispatchEvent(new CustomEvent('agents-theme-change'));
    }

    window.agentsTheme = {
        set: (choice) => {
            root.dataset.themeChoice = choice;
            apply();
        },

        isDark: () => getComputedStyle(root).colorScheme !== 'light',

        // A palette variable, for the parts drawn outside the page's CSS.
        variable: (name) => getComputedStyle(root).getPropertyValue(name).trim(),
    };

    light?.addEventListener('change', apply);
    apply();
})();
