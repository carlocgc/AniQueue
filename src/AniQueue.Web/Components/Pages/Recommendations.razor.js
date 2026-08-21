/*
   Getting a request out of the browser: to the clipboard, or to a file.

   Both operate on text the page already holds, rather than fetching it from an
   endpoint. That is deliberate and it is a correctness point, not a convenience
   one: the request is built from a live database, so a second round trip could
   return a payload that differs from the one on screen — a sync landing between
   the two is enough. Copy, download and the text the user is reading are then
   three views of one string instead of three requests with three answers.
*/

/**
 * Copies text, with a fallback for the case this application is usually in.
 *
 * navigator.clipboard exists only in a secure context. A self-hosted AniQueue is
 * reached over plain http at a LAN address far more often than over https, so on
 * the target deployment the modern API is simply absent — treating that as an
 * error would mean the copy button never works for most of the people this is
 * built for.
 *
 * The fallback is execCommand, which is deprecated and still the only thing that
 * works there. It needs a real selection, so the textarea is attached, selected
 * and removed; it is positioned off-screen rather than hidden, because a
 * display:none element cannot hold a selection.
 *
 * @param {string} text
 * @returns {Promise<boolean>} whether the text reached the clipboard.
 */
export async function copyText(text) {
    if (navigator.clipboard && globalThis.isSecureContext) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            // Denied by permission policy, or the document was not focused. Fall
            // through rather than reporting failure while another route remains.
        }
    }

    const area = document.createElement('textarea');

    area.value = text;
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.top = '-1000px';
    area.style.opacity = '0';

    document.body.appendChild(area);

    try {
        area.select();
        area.setSelectionRange(0, text.length);
        return document.execCommand('copy');
    } catch {
        return false;
    } finally {
        document.body.removeChild(area);
    }
}

/**
 * Saves text as a file.
 *
 * A blob and an object URL rather than a data: URI, which Chrome refuses above a
 * few megabytes — a request for a large backlog is exactly that size. The URL is
 * revoked once the click has been dispatched; holding it would pin the blob in
 * memory for the lifetime of the document.
 *
 * Returns a boolean like copyText does, so the caller has one shape to handle
 * rather than a void call whose failure is indistinguishable from success.
 *
 * @param {string} filename
 * @param {string} text
 * @returns {boolean}
 */
export function downloadText(filename, text) {
    const url = URL.createObjectURL(new Blob([text], { type: 'application/json' }));
    const link = document.createElement('a');

    link.href = url;
    link.download = filename;

    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);

    URL.revokeObjectURL(url);
    return true;
}
