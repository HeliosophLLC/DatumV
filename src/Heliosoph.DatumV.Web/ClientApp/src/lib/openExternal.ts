// Opens a URL in the user's OS-level browser. Inside Electron the call
// routes through the preload's `shell.openExternal` bridge; in a plain
// web context it falls back to a `noopener` window.open so the link still
// lands somewhere useful. Callers should `e.preventDefault()` before
// invoking this so the embedded webview doesn't navigate to the URL.
export function openExternalUrl(url: string): void {
  if (typeof window.electronHost?.openExternal === 'function') {
    void window.electronHost.openExternal(url);
    return;
  }
  window.open(url, '_blank', 'noopener,noreferrer');
}

// Cheap discriminator for "should this open in the OS browser." Matches
// http / https / mailto / tel — anything with an absolute scheme that
// the embedded view shouldn't try to render itself. Anchors (`#x`),
// relative paths, and `javascript:` (defensively) are NOT external.
export function isExternalUrl(href: string | undefined): href is string {
  if (!href) return false;
  return /^(https?|mailto|tel):/i.test(href);
}

// True when the text is, in its entirety, an http(s) URL — the shape
// that earns a click-to-open affordance on a results cell. Deliberately
// stricter than isExternalUrl: a cell is a value, so scheme-less domains
// ("acme.com") and URLs embedded in prose stay plain text.
export function isHttpUrl(text: string | undefined): text is string {
  if (!text) return false;
  return /^https?:\/\/\S+$/i.test(text.trim());
}
