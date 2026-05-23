// Thin fetch helpers for the per-catalog tabs state file (`.datumv/tabs.json`).
// The NSwag-generated FilesClient surfaces these endpoints but mistypes the
// GET as Promise<string> (the body is JSON, not a string literal) and the
// PUT as Promise<FileResponse> (a no-content endpoint). Calling fetch
// directly avoids the round-trip cast that would otherwise hide the
// real shape from TypeScript.

export async function fetchTabsState(): Promise<unknown | null> {
  const r = await fetch('/api/files/state/tabs', { credentials: 'include' });
  if (r.status === 204) return null;
  if (!r.ok) {
    throw new Error(`GET /api/files/state/tabs failed: ${r.status}`);
  }
  return r.json();
}

export async function putTabsState(body: unknown): Promise<void> {
  const r = await fetch('/api/files/state/tabs', {
    method: 'PUT',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!r.ok) {
    throw new Error(`PUT /api/files/state/tabs failed: ${r.status}`);
  }
}
