# Vinyl Shell Album Browser — Implementation Description

## TL;DR

Implement this as a **mobile-first stacked album-cover scroller** where covers overlap vertically like vinyl sleeves.

Do not focus on the decorative background. Focus on:

- the card stack
- snap scrolling
- active album state
- 80% visibility rule
- layered vinyl-sleeve feeling

---

## Core Idea

Create a **vertical album browser** where album covers are displayed as a stacked list.

The layout should feel like a user is browsing vinyl sleeves in a crate, but the implementation should be clean and app-focused, without relying on a realistic store background.

The main UI behavior:

- Album covers are square cards.
- Cards overlap vertically.
- The active album cover is the largest and most visible.
- The next album cover overlaps around **20%** of the active card.
- Therefore, the active card remains around **80% visible**.
- Scrolling should move one album at a time.
- The scroll should snap to each album.

---

## Screen Structure

The screen should have a simple mobile layout:

```txt
┌─────────────────────────┐
│ Header                  │
│                         │
│ Album stack area        │
│                         │
│ [Back album partially]  │
│ ┌─────────────────────┐ │
│ │ Active album cover  │ │
│ │ 80% visible         │ │
│ └─────────────────────┘ │
│ ┌─────────────────────┐ │
│ │ Next album cover    │ │
│ └─────────────────────┘ │
│ ┌─────────────────────┐ │
│ │ Next album cover    │ │
│ └─────────────────────┘ │
│                         │
│ Bottom navigation       │
└─────────────────────────┘
```

The background can simply be:

- solid dark
- soft gradient
- neutral app surface
- no realistic shop/store image required

---

## Album Stack Layout

The album stack should be the main component.

Each album card should be placed in the same vertical flow, but with a **negative bottom margin** or transform offset so the cards overlap.

Recommended values:

```css
--album-size: min(82vw, 380px);
--overlap: calc(var(--album-size) * -0.2);
```

Each card:

```css
.album-card {
  width: var(--album-size);
  height: var(--album-size);
  margin-bottom: var(--overlap);
}
```

This means each next card moves upward by 20% of the card height.

So visually:

```txt
Card height: 360px
Overlap: 72px
Visible part before next card covers it: 288px
Visible ratio: 80%
```

---

## Active Card Behavior

The active card should look like the selected album.

It should have:

```txt
scale: 1
opacity: 1
z-index: highest
brightness: normal
shadow: strong
```

It should also show album information on top of the cover:

```txt
Album title
Artist name
Price or action button
```

The information can be placed as a bottom overlay inside the album cover.

Example:

```txt
┌─────────────────────────┐
│                         │
│      album artwork      │
│                         │
│                         │
│ Title                   │
│ Artist          $29.99  │
└─────────────────────────┘
```

Use a subtle gradient overlay at the bottom of the cover so text remains readable.

---

## Inactive Card Behavior

Inactive cards should still be visible, but visually secondary.

They should have:

```txt
scale: 0.94–0.98
opacity: 0.75–0.9
brightness: slightly reduced
z-index: lower than active card
```

Cards farther away from the active item can be slightly darker or smaller.

Example:

```txt
Active card:
scale(1)
opacity 1

1 card away:
scale(0.96)
opacity 0.88

2 cards away:
scale(0.94)
opacity 0.76
```

This creates depth without needing a realistic background.

---

## Layering Rules

The focused album should always visually sit above nearby albums.

Use dynamic `z-index`.

Example logic:

```txt
distance = Math.abs(index - activeIndex)

z-index = 100 - distance
scale = 1 - distance * 0.03
opacity = 1 - distance * 0.12
```

Clamp the values so cards do not become too small or invisible.

Recommended minimums:

```txt
minimum scale: 0.9
minimum opacity: 0.55
```

---

## Scroll Behavior

Use vertical scrolling with snap points.

The user should not freely stop between covers. The interface should land on one album at a time.

Use:

```css
scroll-snap-type: y mandatory;
```

Each card should use:

```css
scroll-snap-align: center;
```

The scroll container should feel smooth and controlled.

The active card can be detected by checking which card is closest to the vertical center of the viewport.

---

## Motion Behavior

When the user scrolls down:

```txt
Current active album moves upward
Next album slides into focus
Next album becomes brighter
Next album scales up to 1
Previous album scales down slightly
```

When the user scrolls up:

```txt
Previous album returns to focus
Current album moves downward
Depth order updates smoothly
```

Use transitions for:

```txt
transform
opacity
filter
box-shadow
```

Suggested transition:

```css
transition: transform 220ms ease, opacity 220ms ease, filter 220ms ease, box-shadow 220ms ease;
```

---

## Component Requirements for Claude Code

Use this as the direct implementation brief:

```txt
Create a mobile-first React component for a music marketplace album browser.

Do not create a realistic background. Use a simple dark app background or gradient. Focus on the album stack interaction.

The main component is a vertically scrollable stack of square album cover cards. Cards should overlap like vinyl sleeves in a crate. Each card should overlap the previous card by approximately 20% of its height, so the focused/top album remains around 80% visible.

The user should scroll one album at a time using scroll snapping.

The active album should be visually emphasized:
- scale 1
- full opacity
- strongest shadow
- highest z-index
- readable title, artist, and price/action button overlay

Inactive albums should:
- be slightly smaller
- be slightly darker
- have lower opacity
- sit visually behind the active album
- remain partially visible to show the stacked vinyl effect

Implementation details:
- use React
- use CSS or Tailwind
- use a vertical scroll container
- use scroll-snap-type: y mandatory
- use scroll-snap-align: center
- calculate activeIndex from scroll position or IntersectionObserver
- apply dynamic scale, opacity, z-index, and brightness based on distance from activeIndex
- use negative margin or transform offset to create 20% overlap
- keep the interface clean, modern, and mobile-first
```

---

## Visual Style

Use a minimal app UI style:

```txt
Background:
dark neutral, gradient optional

Cards:
square
rounded corners
overflow hidden
soft shadow
album art fills the card

Text overlay:
bottom of active card
white text
small artist name
price button on the right

Depth:
active card appears closest
inactive cards recede behind it
```

The design should feel like a **digital vinyl crate**, not a normal playlist.

The important part is the overlapping card mechanic and the one-by-one scroll behavior.

---

## Key Implementation Principle

The interface should not be built like a standard list.

It should be built like a **physical stack of album sleeves**:

```txt
One album is active.
The next album overlaps it by 20%.
The active album remains 80% visible.
The user scrolls one album at a time.
The depth changes smoothly as the active item changes.
```
