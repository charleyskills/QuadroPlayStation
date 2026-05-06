// Browser-side helpers for the Plex link flow and library search. Every call
// here goes from the browser to our own ASP.NET endpoints, which is critical:
// - cookie-based Plex session is HttpOnly, only carried on browser-issued requests
// - the .NET Blazor circuit can't read or write the cookie during SignalR events,
//   so the browser is the authoritative client for this auth surface

(function () {
    const json = (response) => {
        if (!response.ok) {
            return response.text().then(t => { throw new Error(`HTTP ${response.status}: ${t || response.statusText}`); });
        }
        return response.json();
    };

    async function startAuth() {
        const res = await fetch('/plex/auth/start', { method: 'POST', credentials: 'same-origin' });
        return json(res);
    }

    async function checkAuthStatus(pinId) {
        const res = await fetch(`/plex/auth/status?pinId=${encodeURIComponent(pinId)}`, { credentials: 'same-origin' });
        return json(res);
    }

    async function logout() {
        await fetch('/plex/auth/logout', { method: 'POST', credentials: 'same-origin' });
    }

    async function search(query, offset, limit) {
        const o = Number.isFinite(offset) ? offset : 0;
        const l = Number.isFinite(limit) ? limit : 50;
        const url = `/plex/search?q=${encodeURIComponent(query)}&offset=${o}&limit=${l}`;
        const res = await fetch(url, { credentials: 'same-origin' });
        return json(res);
    }

    // IntersectionObserver wired to a sentinel element. When the sentinel scrolls into
    // view, we invoke the supplied .NET method (typically NowPlaying.LoadMorePlexAlbums)
    // exactly once until the observer is reset.
    const _observers = new Map();

    function installLoadMoreObserver(sentinelId, dotNetRef, methodName) {
        uninstallLoadMoreObserver(sentinelId);
        const el = document.getElementById(sentinelId);
        if (!el) return false;
        const root = el.closest('.sheet-scroll') || null;
        const obs = new IntersectionObserver((entries) => {
            for (const entry of entries) {
                if (!entry.isIntersecting) continue;
                try { dotNetRef.invokeMethodAsync(methodName); } catch (e) { /* circuit gone */ }
            }
        }, { root, rootMargin: '120px 0px', threshold: 0 });
        obs.observe(el);
        _observers.set(sentinelId, obs);
        return true;
    }

    function uninstallLoadMoreObserver(sentinelId) {
        const obs = _observers.get(sentinelId);
        if (obs) { try { obs.disconnect(); } catch {} }
        _observers.delete(sentinelId);
    }

    async function getAlbumTracks(ratingKey) {
        const res = await fetch(`/plex/album/${encodeURIComponent(ratingKey)}/tracks`, { credentials: 'same-origin' });
        return json(res);
    }

    async function prepare(track) {
        const body = JSON.stringify({
            id: track.id,
            title: track.title,
            artist: track.artist,
            album: track.album,
            remoteSourceUrl: track.remoteSourceUrl,
            // Optional manual channel-pair override; null/undefined = let the
            // server auto-pick from the source's channel_layout.
            frontL: track.frontL,
            frontR: track.frontR,
            backL: track.backL,
            backR: track.backR,
            // Optional codec override ("mp3"|"flac"); null/undefined = server default.
            outputFormat: track.outputFormat,
        });
        const res = await fetch('/plex/prepare', {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json' },
            body,
        });
        return json(res);
    }

    function openLinkPage(code) {
        try {
            window.open(`https://plex.tv/link/?code=${encodeURIComponent(code)}`, '_blank', 'noopener');
        } catch (e) {
            // popup blocked — caller renders the link as a clickable anchor as a fallback
        }
    }

    window.plexInterop = {
        startAuth,
        checkAuthStatus,
        logout,
        search,
        getAlbumTracks,
        prepare,
        openLinkPage,
        installLoadMoreObserver,
        uninstallLoadMoreObserver,
    };
})();
