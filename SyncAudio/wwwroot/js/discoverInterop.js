// Discover view interop: active-card tracking, load-more sentinel.
//
// Cards are stacked with 20% overlap (vinyl-crate feel). The active card is the
// one whose center is closest to the scroll-container center. Scale, opacity,
// z-index and box-shadow are applied as inline styles based on distance from the
// active index — no CSS class toggling needed.

(function () {
    const _state = new Map();          // containerId → { scroll, onScroll, rafId }
    const _loadMoreObservers = new Map();

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

            if (distance === 0) {
                card.style.boxShadow = '0 16px 48px rgba(0,0,0,0.65), 0 4px 16px rgba(0,0,0,0.45)';
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

        const state = { scroll, rafId: 0, onScroll: null };

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
