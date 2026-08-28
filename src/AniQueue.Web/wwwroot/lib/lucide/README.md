# Lucide icons (vendored)

| | |
|---|---|
| Version | 1.34.0 |
| Licence | ISC |
| Source | <https://cdn.jsdelivr.net/npm/lucide-static@1.34.0/icons/> |
| Upstream | <https://github.com/lucide-icons/lucide> |
| Size | 3,651 bytes |

Seventeen glyphs, assembled by hand into one `<symbol>` sprite. Sixteen were chosen in
Phase 18a, where a phone-sized navigation bar and thumb-sized row controls both needed
icons; `refresh-cw` arrived with the square Run now button on Settings.

## Why a sprite of seventeen rather than a package

There is no Node.js and no frontend build system here, by decision (ROADMAP.md §1),
so an icon package would arrive with a toolchain to unpack it. ISC permits
redistribution on the terms D46 tests a dataset against, and a committed file needs
no §12 approval because it is not a dependency: nothing resolves it, nothing updates
it, and it cannot drift. The precedent is `lib/sortablejs` beside it.

## How it is drawn

The symbols carry shapes and nothing else. `Components/Shared/Icon.razor` supplies
`fill`, `stroke`, `stroke-width` and the line joins on the host `<svg>`, and every one
of those is an inherited property — so they cross into the cloned shadow tree that
`<use>` creates, which stylesheet rules would not. `stroke="currentColor"` is what
makes an icon take the colour of the button it sits in, in both themes.

The reference is the fingerprinted asset URL rather than `#id`. A same-document
fragment would be the obvious choice, and it does not work here: `App.razor` sets
`<base href="/">`, against which `#power` resolves to `/#power` — a *different*
document, fetched and not found.

## Updating it

Fetch the replacements and rebuild the sprite; the symbol body is everything between
the opening `<svg>` tag and its close.

```bash
curl -O https://cdn.jsdelivr.net/npm/lucide-static@<version>/icons/<name>.svg
```

Then load any page and check the navigation bar, because a symbol whose id no longer
matches renders as nothing at all rather than as an error.
