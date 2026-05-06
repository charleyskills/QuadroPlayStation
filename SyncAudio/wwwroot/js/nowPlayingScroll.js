// Tiny scroll-fade helper for the QuadroSound Now Playing cover.
// Reads scrollTop from a designated scroll container, writes a 1→0.3
// `--np-cover-fade` CSS variable on the cover element. rAF-throttled,
// so no SignalR roundtrip per scroll event.
window.npScroll = {
    _detach: null,

    attach: function (scrollSel, coverSel) {
        this.detach();
        let scroll = document.querySelector(scrollSel);
        let cover = document.querySelector(coverSel);
        if (!scroll || !cover) {
            // On mobile the page itself is the scroll root — fall back gracefully.
            scroll = scroll || document.scrollingElement || document.documentElement;
        }
        if (!scroll || !cover) return;

        let raf = 0;
        let onScroll = function () {
            if (raf) return;
            raf = requestAnimationFrame(function () {
                raf = 0;
                let top = scroll.scrollTop || window.scrollY || 0;
                let t = Math.min(1, Math.max(0, top / 220));
                cover.style.setProperty('--np-cover-fade', String(1 - 0.7 * t));
            });
        };

        // Window fallback when the named scroll container actually scrolls the page.
        let target = scroll === document.scrollingElement || scroll === document.documentElement
            ? window
            : scroll;
        target.addEventListener('scroll', onScroll, { passive: true });
        // Apply once so the variable is set before first scroll.
        onScroll();

        this._detach = function () {
            target.removeEventListener('scroll', onScroll);
            if (raf) cancelAnimationFrame(raf);
        };
    },

    detach: function () {
        if (this._detach) { this._detach(); this._detach = null; }
    }
};
