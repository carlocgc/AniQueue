/*
   Opening and closing a native <dialog>.

   This is the whole of the JavaScript Phase 9c needs, and it exists because the one
   thing Blazor cannot do from C# is the part that matters most here. Rendering
   <dialog open> produces a *non-modal* dialog: no backdrop, no inert page behind it,
   no focus trap, and Escape does nothing. Only showModal() gives those, and only
   from script.

   So the accessibility of this component is not something added on top of the markup
   — it is the reason the module is here at all. Everything else (labelling, initial
   focus, returning focus on close) the platform does for free once showModal has been
   called, which is why there is nothing else in this file.
*/

/**
 * Opens the dialog modally, if it is not already open.
 *
 * Guarded because showModal() on an already-open dialog throws, and a double-click
 * on a row, or a re-render arriving between the click and the interop call, both
 * produce exactly that.
 */
export function showModal(dialog) {
    if (dialog && !dialog.open) {
        closeOnBackdropClick(dialog);
        dialog.showModal();
    }
}

/**
 * Makes a click on the backdrop dismiss the dialog.
 *
 * The comment on close() below has always said a click on the backdrop was one of
 * the three ways out. It was not: a native <dialog> gives Escape and nothing else,
 * and clicking the dark area around this one did nothing at all.
 *
 * Attached once per element rather than per open. The dialog outlives every title
 * shown in it — one instance per page (D49) — so adding a listener on each showModal
 * would stack them.
 */
function closeOnBackdropClick(dialog) {
    if (dialog.dataset.backdropClose) {
        return;
    }

    dialog.dataset.backdropClose = "true";

    // Both ends of the gesture have to be outside, or a drag that starts on a
    // selection inside the dialog and finishes past its edge closes it — which is
    // how you lose a paragraph you were halfway through highlighting.
    let startedOutside = false;

    dialog.addEventListener("pointerdown", (event) => {
        startedOutside = isOutside(dialog, event);
    });

    dialog.addEventListener("click", (event) => {
        if (startedOutside && isOutside(dialog, event)) {
            dialog.close();
        }
    });
}

/**
 * Whether a pointer event landed on the backdrop rather than on the dialog.
 *
 * Measured against the dialog's own box rather than by comparing event.target,
 * which is the usual trick and wrong here: this dialog scrolls, so a click on its
 * scrollbar also targets the dialog element and would dismiss it mid-scroll.
 *
 * detail is the click count, and it is zero for a click the keyboard synthesised —
 * pressing Enter on a button reports coordinates of 0,0, which is outside every
 * dialog on screen. Without this, operating the close button by keyboard would take
 * this path instead of its own.
 */
function isOutside(dialog, event) {
    if (event.detail === 0) {
        return false;
    }

    const box = dialog.getBoundingClientRect();

    return event.clientX < box.left
        || event.clientX > box.right
        || event.clientY < box.top
        || event.clientY > box.bottom;
}

/**
 * Closes it, if it is open.
 *
 * Also a no-op when it is not, because the close can arrive from three places — the
 * button, Escape, and a click on the backdrop — and the first of those re-enters
 * through .NET after the platform has already done it.
 */
export function close(dialog) {
    if (dialog && dialog.open) {
        dialog.close();
    }
}
