// Refcount cache for base64-encoded media → blob URL. With table
// virtualization, MediaCell components mount/unmount as the user
// scrolls, so naive per-cell URL creation thrashes:
//   on mount   → atob → new Uint8Array → URL.createObjectURL
//   on unmount → URL.revokeObjectURL
// Scrolling the same image back and forth re-decodes the base64 every
// time. For image-heavy result sets this is both slow and wasteful.
//
// This cache keys URLs by their (mime, dataB64) pair. The first
// requester decodes; subsequent requesters share the same URL with a
// bumped refcount. Only when the last requester releases does the URL
// actually get revoked.
//
// Cache survives across data changes — if the same image bytes appear
// in two different queries' results, we share the URL. Memory is
// bounded only by how many unique images are in flight; for very long
// sessions, an LRU-with-cap eviction would be the next refinement.

interface CacheEntry {
  url: string;
  refCount: number;
}

const cache = new Map<string, CacheEntry>();

function cacheKey(mime: string, dataB64: string): string {
  // Length prefix avoids collisions when one mime/dataB64 pair is a
  // prefix of another (extremely unlikely but cheap to make safe).
  return `${mime.length}:${mime}|${dataB64}`;
}

function decodeToObjectUrl(dataB64: string, mime: string): string {
  const bin = atob(dataB64);
  const len = bin.length;
  const arr = new Uint8Array(len);
  for (let i = 0; i < len; i++) arr[i] = bin.charCodeAt(i);
  return URL.createObjectURL(new Blob([arr], { type: mime }));
}

// Acquire a blob URL for the given (mime, dataB64). Always pair with a
// release() call when the URL is no longer needed (typically in a
// useEffect cleanup).
export function acquireMediaUrl(mime: string, dataB64: string): string {
  const key = cacheKey(mime, dataB64);
  const existing = cache.get(key);
  if (existing) {
    existing.refCount++;
    return existing.url;
  }
  const url = decodeToObjectUrl(dataB64, mime);
  cache.set(key, { url, refCount: 1 });
  return url;
}

// Decrement the refcount and revoke the URL when it drops to zero.
// Pass the same (mime, dataB64) used in the matching acquire().
export function releaseMediaUrl(mime: string, dataB64: string): void {
  const key = cacheKey(mime, dataB64);
  const entry = cache.get(key);
  if (!entry) return;
  entry.refCount--;
  if (entry.refCount <= 0) {
    URL.revokeObjectURL(entry.url);
    cache.delete(key);
  }
}

// Drop everything — used by tests / debugging only. Production code
// should rely on refcount-driven eviction.
export function clearMediaUrlCache(): void {
  for (const entry of cache.values()) {
    URL.revokeObjectURL(entry.url);
  }
  cache.clear();
}
