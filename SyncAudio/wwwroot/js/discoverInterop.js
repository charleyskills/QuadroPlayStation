// Discover view interop: color extraction, active-card tracking, load-more sentinel.
//
// Cards are stacked with 20% overlap (vinyl-crate feel). The active card is the
// one whose center is closest to the scroll-container center. Scale, opacity,
// z-index and box-shadow are applied as inline styles based on distance from the
// active index — no CSS class toggling needed.

(function () {
    const _state = new Map();          // containerId → { colorObs, scroll, onScroll, rafId }
    const _loadMoreObservers = new Map();

    // ── Color extraction ────────────────────────────────────────────────────────

    function rgbToHsl(r, g, b) {
        r /= 255; g /= 255; b /= 255;
        const max = Math.max(r, g, b), min = Math.min(r, g, b);
        const l = (max + min) / 2;
        if (max === min) return [0, 0, l];
        const d = max - min;
        const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        let h;
        switch (max) {
            case r: h = ((g - b) / d + (g < b ? 6 : 0)) / 6; break;
            case g: h = ((b - r) / d + 2) / 6; break;
            default: h = ((r - g) / d + 4) / 6;
        }
        return [h, s, l];
    }

    function colorDistance(a, b) {
        const dh = Math.min(Math.abs(a[0] - b[0]), 1 - Math.abs(a[0] - b[0]));
        return dh * 2 + Math.abs(a[1] - b[1]) + Math.abs(a[2] - b[2]);
    }

    function extractColors(imgEl) {
        try {
            const SIZE = 24;
            const canvas = new OffscreenCanvas(SIZE, SIZE);
            const ctx = canvas.getContext('2d', { willReadFrequently: true });
            ctx.drawImage(imgEl, 0, 0, SIZE, SIZE);
            const { data } = ctx.getImageData(0, 0, SIZE, SIZE);

            const pixels = [];
            for (let i = 0; i < data.length; i += 4) {
                const [h, s, l] = rgbToHsl(data[i], data[i + 1], data[i + 2]);
                if (s > 0.15 && l > 0.1 && l < 0.9) {
                    pixels.push({ h, s, l, r: data[i], g: data[i + 1], b: data[i + 2] });
                }
            }
            if (pixels.length === 0) return ['#5EC8FF', '#7A3CFF'];

            pixels.sort((a, b) => b.s - a.s);
            const p0 = pixels[0];
            const c0 = [p0.h, p0.s, p0.l];
            let p1 = null;
            for (const p of pixels) {
                if (colorDistance([p.h, p.s, p.l], c0) > 0.25) { p1 = p; break; }
            }
            if (!p1) p1 = pixels[Math.floor(pixels.length / 2)] || pixels[0];
            return [
                `rgb(${p0.r},${p0.g},${p0.b})`,
                `rgb(${p1.r},${p1.g},${p1.b})`,
            ];
        } catch {
            return ['#5EC8FF', '#7A3CFF'];
        }
    }

    function applyCardColors(card) {
        if (card.dataset.colorsApplied === '1') return;
        const img = card.querySelector('.album-card__img');
        if (!img) return;
        const apply = () => {
            const [c0, c1] = extractColors(img);
            card.dataset.gc0 = c0;
            card.dataset.gc1 = c1;
            card.dataset.colorsApplied = '1';
        };
        if (img.complete && img.naturalWidth > 0) {
            apply();
        } else {
            img.addEventListener('load', apply, { once: true });
        }
    }

    // ── Active-card detection ──────────────────────────────────────────────────
    // distance = |index - activeIndex|
    // scale   = clamp(1 - distance * 0.03, 0.90, 1)
    // opacity = clamp(1 - distance * 0.12, 0.55, 1)
    // z-index = 100 - distance

    function updateActiveCard(container, scroll) {
        const scrollRect = scroll.getBoundingClientRect();
        const centerY = scrollRect.top + scrollRect.height / 2;
        const cards = Array.from(container.querySelectorAll('.album-card'));

        let bestIdx = 0;
        let bestDist = Infinity;
        cards.forEach((card, i) => {
            const rect = card.getBoundingClientRect();
            const dist = Math.abs(rect.top + rect.height / 2 - centerY);
            if (dist < bestDist) { bestDist = dist; bestIdx = i; }
        });

        cards.forEach((card, i) => {
            const distance = Math.abs(i - bestIdx);
            const scale   = Math.max(0.90, 1 - distance * 0.03);
            const opacity = Math.max(0.55, 1 - distance * 0.12);
            const zIndex  = 100 - distance;

            card.style.transform = `scale(${scale})`;
            card.style.opacity   = opacity;
            card.style.zIndex    = zIndex;

            if (distance === 0 && card.dataset.gc0) {
                card.style.boxShadow =
                    `0 20px 60px -8px ${card.dataset.gc0}, 0 8px 32px rgba(0,0,0,0.55)`;
            } else {
                card.style.boxShadow = '0 8px 24px rgba(0,0,0,0.35)';
            }
        });
    }

    function installCardObserver(containerId) {
        uninstallCardObserver(containerId);
        const container = document.getElementById(containerId);
        if (!container) return;
        const scroll = container.querySelector('.discover-scroll');
        if (!scroll) return;

        // Lazy color extraction — fires once per card as it enters the viewport.
        const colorObs = new IntersectionObserver((entries) => {
            entries.forEach(entry => {
                if (entry.isIntersecting) applyCardColors(entry.target);
            });
        }, { threshold: 0.05 });
        container.querySelectorAll('.album-card').forEach(card => colorObs.observe(card));

        const state = { colorObs, scroll, rafId: 0, onScroll: null };

        const onScroll = () => {
            if (state.rafId) return;
            state.rafId = requestAnimationFrame(() => {
                state.rafId = 0;
                updateActiveCard(container, scroll);
            });
        };
        state.onScroll = onScroll;

        scroll.addEventListener('scroll', onScroll, { passive: true });
        _state.set(containerId, state);

        requestAnimationFrame(() => updateActiveCard(container, scroll));
    }

    function uninstallCardObserver(containerId) {
        const state = _state.get(containerId);
        if (!state) return;
        try { state.colorObs.disconnect(); } catch {}
        try { state.scroll.removeEventListener('scroll', state.onScroll); } catch {}
        if (state.rafId) cancelAnimationFrame(state.rafId);
        _state.delete(containerId);
    }

    // ── Load-more sentinel ──────────────────────────────────────────────────────

    function installDiscoverLoadMore(sentinelId, dotNetRef, methodName) {
        uninstallDiscoverLoadMore(sentinelId);
        const el = document.getElementById(sentinelId);
        if (!el) return;
        const obs = new IntersectionObserver(entries => {
            entries.forEach(e => {
                if (e.isIntersecting) {
                    try { dotNetRef.invokeMethodAsync(methodName); } catch {}
                }
            });
        }, { rootMargin: '300px 0px', threshold: 0 });
        obs.observe(el);
        _loadMoreObservers.set(sentinelId, obs);
    }

    function uninstallDiscoverLoadMore(sentinelId) {
        const obs = _loadMoreObservers.get(sentinelId);
        if (obs) { obs.disconnect(); _loadMoreObservers.delete(sentinelId); }
    }

    window.discoverInterop = {
        installCardObserver,
        uninstallCardObserver,
        installDiscoverLoadMore,
        uninstallDiscoverLoadMore,
    };
})();
