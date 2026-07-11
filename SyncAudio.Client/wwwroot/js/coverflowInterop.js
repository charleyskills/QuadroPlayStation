// Vertical 3D Coverflow interop — port of the Album Coverflow design
// (`https://api.anthropic.com/v1/design/h/z4YY1CRgpHT0VtTlMISC9g`).
//
// Wheel-driven momentum physics with a Shadow Overlay blur mode. All per-frame
// styling (transforms, overlay alpha, info text, position dots, ambient bg) is
// applied directly to the DOM each rAF tick — Blazor never re-renders during
// scrolling. Card layout: every card lives inside `.cf-card-container` and is
// absolutely positioned at `left:50%; top:50%`; the JS-set transform stack
// (`translate(-50%,-50%) translateY(...) scale(...)`) recenters it.

(function () {
    const _state = new Map();

    // ── Physics constants — match coverflow.jsx ────────────────────────────────
    const FRICTION       = 0.96;
    const STOP_THRESHOLD = 0.0005;
    const WHEEL_GAIN     = 0.003 * 0.6;   // matches push(delta) in useFreeScroll
    const VEL_CLAMP      = 0.5;
    const VISIBLE_RANGE  = 3.5;           // cards with absD > 3.5 are hidden

    function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }

    function computeCoverSize() {
        return window.innerWidth <= 640
            ? Math.min(window.innerWidth - 40, 340)
            : 310;
    }

    function applyFrame(s) {
        const coverSize = s.coverSize;
        const gap       = coverSize * 0.06;

        for (let i = 0; i < s.cards.length; i++) {
            const card = s.cards[i];
            const diff = (s.windowStart + i) - s.pos;   // card i is album (windowStart + i)
            const absD = Math.abs(diff);

            if (absD > VISIBLE_RANGE) {
                if (card.style.display !== 'none') card.style.display = 'none';
                continue;
            }
            if (card.style.display === 'none') card.style.display = '';

            const sign  = diff > 0 ? 1 : -1;
            const scale = Math.max(0.55, 1 - absD * 0.08);
            const y     = sign * absD * (coverSize * scale + gap);
            const zIdx  = Math.round(100 - absD * 10);

            card.style.transform = `translate(-50%, -50%) translateY(${y}px) scale(${scale})`;
            card.style.zIndex    = zIdx;

            // Shadow Overlay blur mode (from BLUR_MODES['depth-shadow']).
            const overlay = card._cfOverlay;
            if (overlay) {
                const a = Math.min(absD * 0.22, 0.6);
                overlay.style.background = `rgba(0,0,0,${a})`;
            }
        }

        // Nearest-album driven UI (info pane, count, ambient bg, position dots).
        const nearest     = clamp(Math.round(s.pos), 0, s.total - 1);
        const infoOpacity = 1 - Math.min(1, Math.abs(s.pos - nearest) * 2.5);

        if (s.countEl)  s.countEl.textContent = `${nearest + 1} of ${s.total}`;

        const nearestCard = s.cards[nearest - s.windowStart];
        if (s.infoEl && nearestCard) {
            s.infoEl.style.opacity = infoOpacity;
            if (s.infoTitleEl)  s.infoTitleEl.textContent  = nearestCard.dataset.title  || '';
            if (s.infoArtistEl) s.infoArtistEl.textContent = nearestCard.dataset.artist || '';
        }

        if (s.bgEl && nearestCard) {
            const url = nearestCard.dataset.coverUrl || '';
            if (url && url !== s.currentBgUrl) {
                s.bgEl.style.backgroundImage = `url('${url}')`;
                s.currentBgUrl = url;
            }
            s.bgEl.style.opacity = url ? String(0.35 * infoOpacity) : '0';
        }

        for (let i = 0; i < s.dots.length; i++) {
            const dot  = s.dots[i];
            const dist = Math.abs(s.pos - i);
            const h    = dist < 0.5 ? 4 + (1 - dist * 2) * 14 : 4;
            dot.style.height     = `${h}px`;
            dot.style.background = dist < 0.5 ? '#fff' : 'rgba(255,255,255,0.25)';
        }

        // Notify Blazor when pos is approaching a window edge (windowing only active when
        // cards.length < total, i.e. the library doesn't fit in one window).
        if (!s.windowUpdatePending && s.dotNetRef && s.cards.length < s.total) {
            const windowEnd       = s.windowStart + s.cards.length - 1;
            const SHIFT_THRESHOLD = 17;  // overshoot tolerance: (17 − 3.5) / 0.5 / 60 ≈ 450 ms round-trip
            if (nearest < s.windowStart + SHIFT_THRESHOLD ||
                nearest > windowEnd - SHIFT_THRESHOLD) {
                s.windowUpdatePending = true;
                try { s.dotNetRef.invokeMethodAsync('UpdateWindowCenter', nearest); } catch {}
            }
        }
    }

    function tick(s) {
        s.rafId = 0;
        s.velocity *= FRICTION;
        if (Math.abs(s.velocity) < STOP_THRESHOLD) {
            s.velocity = 0;
            applyFrame(s);
            return;
        }
        s.pos = clamp(s.pos + s.velocity, 0, s.total - 1);
        applyFrame(s);
        startTick(s);
    }

    function startTick(s) {
        if (s.rafId) return;
        s.rafId = requestAnimationFrame(() => tick(s));
    }

    function wireCard(s, card) {
        if (card._cfWired) return;
        card._cfWired   = true;
        card._cfOverlay = card.querySelector('[data-cf-overlay]');
        card.addEventListener('click', () => {
            if (!s.dotNetRef) return;
            const rk = card.dataset.ratingKey;
            if (!rk) return;
            try { s.dotNetRef.invokeMethodAsync('OnCoverflowSelect', rk); } catch {}
        });
    }

    function install(containerId, dotNetRef, total, windowStart) {
        uninstall(containerId);

        const container = document.getElementById(containerId);
        if (!container) return;

        const cards = Array.from(container.querySelectorAll('[data-cf-card]'));

        const coverSize = computeCoverSize();
        container.style.setProperty('--cf-cover-size', `${coverSize}px`);

        const s = {
            container, cards,
            total:               total ?? cards.length,   // full album count; used to clamp pos
            windowStart:         windowStart ?? 0,
            windowUpdatePending: false,
            coverSize,
            pos:       0,
            velocity:  0,
            rafId:     0,
            dotNetRef,
            currentBgUrl: '',
            // Cached DOM refs — read once, not every frame.
            countEl:      container.querySelector('[data-cf-count]'),
            infoEl:       container.querySelector('[data-cf-info]'),
            infoTitleEl:  container.querySelector('[data-cf-info-title]'),
            infoArtistEl: container.querySelector('[data-cf-info-artist]'),
            bgEl:         container.querySelector('[data-cf-bg]'),
            dots:         Array.from(container.querySelectorAll('[data-cf-dot]')),
            onWheel:      null,
            onResize:     null,
            onTouchStart: null,
            onTouchMove:  null,
            onTouchEnd:   null,
        };

        cards.forEach(c => wireCard(s, c));

        // Wheel-only momentum (touch/drag intentionally removed by the user).
        const onWheel = (e) => {
            e.preventDefault();
            s.velocity = clamp(s.velocity + e.deltaY * WHEEL_GAIN, -VEL_CLAMP, VEL_CLAMP);
            startTick(s);
        };
        s.onWheel = onWheel;
        container.addEventListener('wheel', onWheel, { passive: false });

        // Touch input — iOS Safari doesn't synthesize wheel events from swipes.
        // Finger Δy is pushed into velocity (NOT direct drag) so the same momentum
        // + friction model handles touch and wheel identically. Direction is
        // inverted vs wheel: finger moving down should reveal earlier items.
        let lastTouchY = 0;
        const onTouchStart = (e) => {
            if (e.touches.length !== 1) return;
            lastTouchY = e.touches[0].clientY;
            // Tap halts an in-flight flick — matches native iOS scroll feel.
            s.velocity = 0;
            if (s.rafId) { cancelAnimationFrame(s.rafId); s.rafId = 0; }
        };
        const onTouchMove = (e) => {
            if (e.touches.length !== 1) return;
            e.preventDefault(); // CSS already sets touch-action:none; we own the gesture.
            const y  = e.touches[0].clientY;
            const dy = y - lastTouchY;
            lastTouchY = y;
            // Invert: finger moving down (dy > 0) → pos decreases → reveal earlier items.
            s.velocity = clamp(s.velocity - dy * WHEEL_GAIN, -VEL_CLAMP, VEL_CLAMP);
            startTick(s);
        };
        const onTouchEnd = () => {
            // Residual velocity is the flick — friction loop takes over from here.
            if (s.velocity !== 0) startTick(s);
        };
        s.onTouchStart = onTouchStart;
        s.onTouchMove  = onTouchMove;
        s.onTouchEnd   = onTouchEnd;
        container.addEventListener('touchstart',  onTouchStart, { passive: true });
        container.addEventListener('touchmove',   onTouchMove,  { passive: false });
        container.addEventListener('touchend',    onTouchEnd,   { passive: true });
        container.addEventListener('touchcancel', onTouchEnd,   { passive: true });

        // Recompute cover size on resize so the layout stays responsive.
        const onResize = () => {
            const next = computeCoverSize();
            if (next !== s.coverSize) {
                s.coverSize = next;
                container.style.setProperty('--cf-cover-size', `${next}px`);
                applyFrame(s);
            }
        };
        s.onResize = onResize;
        window.addEventListener('resize', onResize);

        _state.set(containerId, s);

        // Initial layout pass.
        if (s.total > 0) applyFrame(s);
    }

    // Called by Blazor after load-more appends new cards or the window shifts.
    function refresh(containerId, windowStart, total) {
        const s = _state.get(containerId);
        if (!s) return;
        if (windowStart !== undefined) s.windowStart = windowStart;
        if (total        !== undefined) s.total       = total;
        s.windowUpdatePending = false;
        s.cards = Array.from(s.container.querySelectorAll('[data-cf-card]'));
        s.dots  = Array.from(s.container.querySelectorAll('[data-cf-dot]'));
        s.cards.forEach(c => wireCard(s, c));
        if (s.total > 0) applyFrame(s);
    }

    function uninstall(containerId) {
        const s = _state.get(containerId);
        if (!s) return;
        try { s.container.removeEventListener('wheel',       s.onWheel);      } catch {}
        try { s.container.removeEventListener('touchstart',  s.onTouchStart); } catch {}
        try { s.container.removeEventListener('touchmove',   s.onTouchMove);  } catch {}
        try { s.container.removeEventListener('touchend',    s.onTouchEnd);   } catch {}
        try { s.container.removeEventListener('touchcancel', s.onTouchEnd);   } catch {}
        try { window.removeEventListener('resize',           s.onResize);     } catch {}
        if (s.rafId) cancelAnimationFrame(s.rafId);
        _state.delete(containerId);
    }

    window.coverflowInterop = { install, refresh, uninstall };
})();
