// Pure HTML helpers: no DOM event wiring, no global state.

export function htmlNode(html: string): ChildNode | null {
  const tpl = document.createElement('template');
  tpl.innerHTML = html.trim();
  return tpl.content.firstChild;
}

const HTML_ESCAPE_MAP: Record<string, string> = {
  '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
};

export function escapeHtml(s: unknown): string {
  return String(s).replace(/[&<>"']/g, (c) => HTML_ESCAPE_MAP[c]);
}

const ELLIPSIS = '…';

export function truncate(s: unknown, n: number): string {
  const str = String(s ?? '');
  return str.length > n ? str.slice(0, n - 1) + ELLIPSIS : str;
}
