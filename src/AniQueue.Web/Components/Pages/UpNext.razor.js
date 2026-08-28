/*
   Drag reordering for the Up Next queue.

   This is the whole of ROADMAP.md §9's "budget a spike in Phase 4", and the shape
   below is the point of it. Blazor's renderer diffs against its own virtual tree
   and patches the live DOM through direct node references. SortableJS physically
   moves nodes behind its back. Left alone, the two disagree about what the list
   contains, and the next render duplicates or resurrects rows.

   The working pattern is three steps, in this order:

     1. Every row carries @key, so the renderer moves nodes rather than rewriting
        the contents of every row between the old and new positions.
     2. onEnd puts the node back exactly where Sortable found it. After this the
        DOM matches what Blazor still believes, so the tree is consistent again.
     3. Only then does .NET hear about it. The server reorders, re-renders, and
        that render is what actually moves the row.

   So the drag is a gesture that reports two indices; it never edits the list. The
   visible move always comes from the server, which is why a rejected or clamped
   reorder cannot leave the page showing an order the database does not hold.
*/

/**
 * Loads SortableJS on first use.
 *
 * Deliberately not a <script> tag in App.razor: 45KB on every page, to support a
 * gesture that exists on one. The UMD build assigns window.Sortable when it is
 * imported as a module, which is what the caching below leans on.
 *
 * @param {string} url Fingerprinted asset path, resolved by the caller.
 */
let loading = null;

function loadSortable(url) {
    if (globalThis.Sortable) {
        return Promise.resolve(globalThis.Sortable);
    }

    // Resolved against <base href> rather than imported as given. Blazor's asset
    // helper produces a path relative to the application root, and a bare relative
    // specifier is not a valid module specifier — import() rejects it outright.
    // Going through baseURI rather than prefixing "/" also keeps this correct when
    // the application is hosted under a sub-path, which for something self-hosted
    // behind a reverse proxy is the normal case rather than an exotic one.
    const resolved = new URL(url, document.baseURI).href;

    loading ??= import(resolved).then(() => globalThis.Sortable);

    return loading;
}

/**
 * Makes the queue's rows draggable.
 *
 * @param {HTMLElement} list The list holding the cards.
 * @param {object} dotNet Reference to the component, for the drop callback.
 * @param {string} sortableUrl Where to load SortableJS from.
 * @returns The Sortable instance, held by .NET so it can be destroyed.
 */
export async function attach(list, dotNet, sortableUrl) {
    const Sortable = await loadSortable(sortableUrl);

    // The same setting the stylesheet honours; an animated reorder is exactly the
    // kind of motion this preference exists to suppress.
    const reducedMotion = globalThis.matchMedia("(prefers-reduced-motion: reduce)").matches;

    return Sortable.create(list, {
        // Dragging is confined to the grip. A card carries a poster, a title and
        // up to five buttons, and making the whole card draggable turns every one
        // of them into a gamble about whether a click was really a very short drag.
        handle: ".drag-handle",

        animation: reducedMotion ? 0 : 150,
        ghostClass: "drag-ghost",
        chosenClass: "drag-chosen",

        // On touch, a drag has to be distinguishable from a scroll: without the
        // delay, any swipe that begins on a grip drags the row instead of moving
        // the page. Pointer input needs no such disambiguation, so it is exempt.
        delay: 150,
        delayOnTouchOnly: true,

        onEnd: async (event) => {
            const { item, from, oldIndex, newIndex } = event;

            if (oldIndex === newIndex) {
                return;
            }

            // Step 2 above. children[] counts elements only, so any comment marker
            // Blazor left between rows is ignored, and a null reference node
            // appends — which is the right answer when the row came from the end.
            from.removeChild(item);
            from.insertBefore(item, from.children[oldIndex] ?? null);

            await dotNet.invokeMethodAsync("OnDroppedAsync", oldIndex, newIndex);
        }
    });
}

/**
 * Tears the instance down.
 *
 * Called when the queue empties and when the circuit ends. Sortable attaches
 * document-level listeners, so an instance left behind after its list is gone
 * keeps them — and on a long-lived Blazor Server circuit that accumulates.
 */
export function detach(sortable) {
    // Guarded on the element rather than on the instance. destroy() nulls its own
    // el and then writes to it, so calling it twice throws "Cannot set properties
    // of null" — and the caller is a render callback, which can reach here more
    // than once for the same instance. The .NET side claims the reference before
    // awaiting so this should not happen; the guard is here because the failure
    // mode is a torn-down circuit rather than a wasted call.
    if (sortable?.el) {
        sortable.destroy();
    }
}
