# SortableJS (vendored)

| | |
|---|---|
| Version | 1.15.7 |
| Licence | MIT |
| Source | <https://cdn.jsdelivr.net/npm/sortablejs@1.15.7/Sortable.min.js> |
| Upstream | <https://github.com/SortableJS/Sortable> |
| SHA-384 | `DgmC6Xe2bSN2WjTDXzWYbUbxyhNP+NNkGDR/g78pCXV7E7rcVTGxVg0uIVCUUcBc` |
| Size | 45,478 bytes |

The only third-party JavaScript in AniQueue, pre-approved by ROADMAP.md §12 and
only for the Phase 4 queue reordering it is used for.

## Why the file is committed rather than fetched

There is no Node.js and no frontend build system in this repository, by decision
(ROADMAP.md §1). That leaves two ways to obtain a library: reference a CDN at
runtime, or commit it. A CDN reference would make a self-hosted application
depend on the public internet to reorder a list, which defeats the point of
self-hosting, so the file is committed and served from the application's own
`wwwroot`.

## Where it is used

Loaded on demand by `Components/Pages/UpNext.razor.js`, which is the only thing
that imports it — the Up Next page pays for it, and no other page does. That
module also documents the interop pattern that keeps SortableJS and Blazor's
renderer from fighting over the DOM (ROADMAP.md §9).

## Updating it

Download the replacement, check the header comment states the expected version,
and verify the hash above changes to whatever the new file's is:

```bash
curl -o sortable.min.js https://cdn.jsdelivr.net/npm/sortablejs@<version>/Sortable.min.js
```

Then re-test drag reordering on `/up-next` — including that the row lands where
it was dropped and that no row is duplicated afterwards, which is the specific
failure mode §9 describes.
