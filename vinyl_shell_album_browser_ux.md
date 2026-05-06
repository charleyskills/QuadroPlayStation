# Vinyl Shell Album Browser — UX Decision Quiz

Answer each question, then follow the advice for your answer.

---

## Q1 — How many albums will the user typically browse at once?

**A) Fewer than 20**
→ Show all cards in the initial render. No lazy loading needed. Skip to Q2.

**B) 20–100**
→ Render all, but attach an IntersectionObserver to fire color extraction only when a card enters the viewport. This avoids 100 canvas reads on mount.

**C) 100+**
→ Use virtual/windowed rendering or paginated infinite scroll. Keep a sentinel element at the bottom and load the next page when it comes into view. Color extraction still fires per-card on intersection.

---

## Q2 — How much of the active card should be visible?

**A) The full card — I want one album to own the whole screen**
→ Set `height: 100svh` and `margin-top: 0` (no overlap). Use `scroll-snap-align: center`. The card fills the viewport; the next card peeks only via the snap-scroll hint.

**B) About 80% — I want the next card to peek from below (vinyl-shelf feel)**
→ Set card height to a fixed value (e.g. `70vh`) and `margin-top: -14vh` (20% of 70vh). This is the spec target. Use `scroll-snap-align: center` and `padding-block: 15vh` on the scroll container so the first and last cards also center.

**C) About 60% — I want a tighter stack, more cards visible**
→ Set `margin-top: -28vh` (40% of 70vh). Be aware: at this density the active card's art becomes harder to read; compensate by increasing the scale contrast between active and inactive cards.

---

## Q3 — How visually distinct should inactive cards be from the active one?

**A) Barely different — I want all cards to look like full album covers**
→ `transform: scale(0.98)`, `opacity: 0.85`. No blur. Users will rely on scroll position to know which is active.

**B) Noticeably smaller and dimmer, but still readable (spec default)**
→ `transform: scale(0.96)`, `opacity: 0.72`. No blur. This matches the vinyl-shelf spec and keeps art legible.

**C) Strongly pushed back — the active card dominates**
→ `transform: scale(0.80) translateZ(-140px)` (requires `perspective` on the scroll container), `opacity: 0.4`, `filter: blur(2px)`. Dramatic but makes inactive art hard to read on small screens.

---

## Q4 — How should z-index be managed?

**A) Binary: active vs everyone else**
→ Inactive: `z-index: 1`. Active: `z-index: 10`. Simple; add `--active` class via JS scroll listener.

**B) Tiered by distance from active**
→ Add three JS-managed classes: `--prev` (z-index: 8, opacity: 0.75), `--next` (z-index: 9, opacity: 0.85), and leave the rest at the default inactive style. More nuanced; costs one extra `querySelectorAll` pass per scroll frame.

---

## Q5 — How should the active card be detected?

**A) IntersectionObserver at a high threshold (≥ 0.5)**
→ Works but fires late — the card must be more than half visible before it activates. Feels laggy on slow scrolls.

**B) Scroll listener + rAF, find card whose center is closest to scroll-container center**
→ Recommended. Accurate at any scroll speed. Throttle with `requestAnimationFrame` to avoid jank. Unregister on component teardown.

**C) CSS `:focus` / `:target`**
→ Only works if you control focus programmatically (e.g. keyboard nav). Not reliable for touch/mouse scroll.

---

## Q6 — Where should dominant color extraction happen?

**A) On mount, for all cards at once**
→ Fast on small lists. On 50+ cards this blocks the main thread for ~100–200 ms. Prefer B instead.

**B) Lazily, when the card first scrolls into the viewport**
→ Use IntersectionObserver at `threshold: 0.05`. Draw a 24×24 thumbnail to `OffscreenCanvas`, sample HSL values, pick two well-separated saturated pixels. Cache result with `data-colors-applied="1"` so it never runs twice per card.

**C) Server-side**
→ Valid for static catalogs. Pre-compute dominant colors and embed them as inline CSS vars in the rendered HTML. Zero client-side canvas work; good for SSR.

---

## Q7 — Should the scroll container use `overflow: hidden` or `overflow-y: scroll`?

**A) `overflow: hidden` on the overlay, `overflow-y: scroll` on the inner scroll container**
→ Correct. The overlay clips the overflowing cards. The inner container handles scroll and snap. Add `box-sizing: border-box` to the scroll container if it has `padding-block`, otherwise the container becomes taller than the viewport and the last card can never center.

**B) `overflow-y: scroll` directly on the overlay**
→ Works but you lose the ability to use `position: fixed` children (like a close button) that sit outside the scroll flow. Use A instead.

---

## Q8 — What scroll-snap setting gives the best feel?

**A) `scroll-snap-type: y mandatory` + `scroll-snap-align: center`**
→ Recommended. Snaps exactly one card at a time. The `center` alignment works with `padding-block` to center the first and last cards correctly.

**B) `scroll-snap-type: y proximity`**
→ Only snaps if the user stops near a snap point. Feels more like a free scroll; lose the vinyl-crate "click" feel.

**C) No snap — free scroll with JS animation**
→ Full control, but you must implement inertia yourself. Not recommended unless the platform doesn't support CSS scroll snap.

---

## Quick-reference: Spec-compliant defaults

```css
.discover-scroll {
    box-sizing: border-box;       /* critical: prevents last-card bug */
    height: 100%;
    overflow-y: scroll;
    scroll-snap-type: y mandatory;
    scrollbar-width: none;
    padding-block: 15vh;          /* (viewport - card) / 2 = (100 - 70) / 2 */
}

.discover-card {
    height: 70vh;
    width: min(78vw, 60vh);
    margin-top: -14vh;            /* 20% of 70vh → 80% visible */
    margin-top: 0;                /* first-child override */
    scroll-snap-align: center;
    transform: scale(0.96);
    opacity: 0.72;
    z-index: 1;
    transition: transform 0.55s cubic-bezier(0.22,1,0.36,1), opacity 0.55s ease;
}

.discover-card--active {
    transform: scale(1);
    opacity: 1;
    z-index: 10;
}
```
