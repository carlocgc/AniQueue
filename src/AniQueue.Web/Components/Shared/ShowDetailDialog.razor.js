/*
   Opening and closing a native <dialog>.

   The whole of the JavaScript this dialog needs. It exists because the one
   thing Blazor cannot do from C# is the part that matters most here. Rendering
   <dialog open> produces a *non-modal* dialog: no backdrop, no inert page behind it,
   no focus trap, and Escape does nothing. Only showModal() gives those, and only
   from script.

   So the accessibility of this component is not something added on top of the markup
   — it is the reason the module is here at all. Labelling, initial focus and
   returning focus on close the platform does for free once showModal has been called.

   Dismissing by clicking the backdrop it does not do, and that is the rest of this
   file.
*/

/**
 * The dialog currently open, or null.
 *
 * Module scope rather than per component, because the browser caches an ES module by
 * URL: every ShowDetailDialog on every page imports this and gets the same instance.
 * One pair of listeners serves all of them, and they are registered once — attaching
 * per element leaked a pair on every page navigation, and attaching per open leaked a
 * pair on every title.
 */
let openDialog = null;

let listening = false;

/** Whether the press that began the current gesture landed on the backdrop. */
let pressedOutside = false;

/**
 * Opens the dialog modally, if it is not already open.
 *
 * Guarded because showModal() on an already-open dialog throws, and a double-click
 * on a row, or a re-render arriving between the click and the interop call, both
 * produce exactly that.
 */
export function showModal(dialog) {
    if (dialog && !dialog.open) {
        listen();
        openDialog = dialog;
        pressedOutside = false;
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

    if (openDialog === dialog) {
        openDialog = null;
    }
}

/**
 * Watches for a click on the backdrop, once for the life of the page.
 *
 * On the document rather than on the dialog. A real press on the backdrop does target
 * the dialog element, so the usual version would work — but this one depends on
 * nothing about which element the platform picks, and the first attempt at this
 * shipped broken on exactly that kind of assumption.
 */
function listen() {
    if (listening) {
        return;
    }

    listening = true;

    // Capture, so that nothing inside the dialog can stop this seeing the gesture.
    // Only points outside the dialog are acted on, so a click within it is unaffected
    // by being seen first.
    // No detail check on this one, and that is the whole of what broke the first
    // attempt. detail is a click count; a real pointerdown carries 0, so requiring it
    // to be non-zero meant no press was ever recorded as outside and the click below
    // never fired. It passed a synthetic test because the test dispatched a
    // pointerdown with detail: 1 — a test asserting the assumption rather than the
    // platform. Read off a real press: PointerEvent on DIALOG at (59, 642), detail 0.
    //
    // Both events, because this must not depend on which of them arrives. A browser
    // fires pointerdown and then mousedown for the same press, so the second overwrites
    // the first with the same answer; something that sends only one still gets counted.
    // A press that goes unseen would leave the last gesture's answer standing, and the
    // stale value is what a drag out of the dialog would then close it with.
    for (const name of ["pointerdown", "mousedown"]) {
        document.addEventListener(name, (event) => {
            pressedOutside = isOutside(event);
        }, true);
    }

    // Escape, which the platform is supposed to do for a modal dialog and could not be
    // observed doing. Measured with the dialog modal and focus on its own close button:
    // the keydown arrives, nothing calls preventDefault, no cancel event is raised, and
    // the dialog stays open. Whether that is the browser or the automation driving it
    // was not settled — so this closes it either way, and a second close from the
    // platform would be the no-op close() already guards.
    document.addEventListener("keydown", (event) => {
        if (event.key === "Escape" && openDialog) {
            const dialog = openDialog;

            openDialog = null;
            dialog.close();
        }
    }, true);

    document.addEventListener("click", (event) => {
        // Both ends of the gesture have to be outside, or a drag that starts on a
        // paragraph inside the dialog and finishes past its edge closes it — which is
        // how somebody loses the sentence they were halfway through highlighting.
        // A click the keyboard synthesised carries detail 0 and coordinates of 0,0,
        // which is outside every dialog on screen — so without this, operating the
        // close button by keyboard would take this path instead of its own. It belongs
        // here and not on the press, because a keyboard activation produces no press.
        const dismiss = pressedOutside && event.detail > 0 && isOutside(event) && openDialog;

        // Spent either way. A press belongs to one click, and leaving the answer behind
        // lets the next gesture inherit it.
        pressedOutside = false;

        if (dismiss) {
            const dialog = openDialog;

            openDialog = null;
            dialog.close();
        }
    }, true);
}

/**
 * Whether a pointer event landed on the backdrop rather than on the open dialog.
 *
 * Measured against the dialog's own box rather than by comparing event.target, which
 * is the usual trick and not enough here: this dialog scrolls, so a click on its own
 * scrollbar also targets the dialog element and would dismiss it mid-scroll.
 */
function isOutside(event) {
    if (!openDialog || !openDialog.open) {
        return false;
    }

    const box = openDialog.getBoundingClientRect();

    return event.clientX < box.left
        || event.clientX > box.right
        || event.clientY < box.top
        || event.clientY > box.bottom;
}
