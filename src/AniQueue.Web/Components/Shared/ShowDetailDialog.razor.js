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
        dialog.showModal();
    }
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
