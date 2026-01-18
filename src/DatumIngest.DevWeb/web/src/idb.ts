// Result payloads (potentially many MB for image/audio queries) live here
// instead of localStorage. One object store keyed by tabId.

const DB_NAME = 'datum-devweb';
const DB_VERSION = 1;
const STORE_NAME = 'results';

let dbPromise: Promise<IDBDatabase> | null = null;
let unavailable = false;

function open(): Promise<IDBDatabase> {
  if (unavailable) return Promise.reject(new Error('IndexedDB unavailable'));
  if (!dbPromise) {
    if (!('indexedDB' in window)) {
      unavailable = true;
      return Promise.reject(new Error('IndexedDB unavailable'));
    }
    dbPromise = new Promise<IDBDatabase>((resolve, reject) => {
      const req = indexedDB.open(DB_NAME, DB_VERSION);
      req.onupgradeneeded = (e) => {
        const db = (e.target as IDBOpenDBRequest).result;
        if (!db.objectStoreNames.contains(STORE_NAME)) {
          db.createObjectStore(STORE_NAME, { keyPath: 'key' });
        }
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => { unavailable = true; reject(req.error); };
      req.onblocked = () => reject(new Error('IndexedDB open blocked'));
    });
  }
  return dbPromise;
}

interface ResultRecord {
  key: string;
  tabId: string;
  result: unknown;
  savedAt: number;
}

export async function saveResult(
  tabId: string,
  result: unknown,
): Promise<void> {
  const db = await open();
  return new Promise<void>((resolve, reject) => {
    const tx = db.transaction(STORE_NAME, 'readwrite');
    tx.objectStore(STORE_NAME).put({
      key: tabId,
      tabId,
      result,
      savedAt: Date.now(),
    });
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
    tx.onabort = () => reject(tx.error);
  });
}

export async function loadResult(tabId: string): Promise<unknown> {
  const db = await open();
  return new Promise<unknown>((resolve, reject) => {
    const tx = db.transaction(STORE_NAME, 'readonly');
    const req = tx.objectStore(STORE_NAME).get(tabId);
    req.onsuccess = () => {
      const record = req.result as ResultRecord | undefined;
      resolve(record ? record.result : null);
    };
    req.onerror = () => reject(req.error);
  });
}

export async function deleteResult(tabId: string): Promise<void> {
  const db = await open();
  return new Promise<void>((resolve, reject) => {
    const tx = db.transaction(STORE_NAME, 'readwrite');
    tx.objectStore(STORE_NAME).delete(tabId);
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
  });
}

export function isUnavailable(): boolean {
  return unavailable;
}
