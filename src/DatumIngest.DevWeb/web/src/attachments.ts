// Global "attachments" state — files the user has dropped into the
// editor or attachments panel and can reference from SQL via $name.
//
// Each attachment has:
//   - name: SQL-friendly identifier the SQL editor's $name autocomplete
//     and the multipart request body use to refer to the file. Default
//     derived from the original filename, normalised to a valid SQL
//     identifier; user-renameable from the panel.
//   - originalFilename: the file's name as dropped, kept verbatim for
//     display in the panel.
//   - kind: inferred DataKind ('Image' | 'Audio' | 'Video' | 'UInt8').
//   - size: byte length, for the panel's metadata row.
//   - blob: the actual File payload, used to construct multipart parts
//     at submit time.
//
// Attachments are intentionally not persisted across reloads — they
// live in browser memory only. This keeps multi-MB drops from filling
// localStorage (which would refuse the write anyway) and matches the
// "stage in browser memory" model the plan describes.

import { proxy, ref } from 'valtio';

export type AttachmentKind = 'Image' | 'Audio' | 'Video' | 'UInt8';

export interface Attachment {
  id: string;
  name: string;
  originalFilename: string;
  kind: AttachmentKind;
  size: number;
  // ref() prevents valtio from proxying the File — we never read its
  // properties through a snapshot, only pass it directly to FormData.
  blob: File;
}

interface AttachmentsState {
  items: Attachment[];
  // Visibility of the side panel. Independent of items.length so the
  // user can collapse the drawer while keeping their attachments.
  open: boolean;
}

export const attachmentsState = proxy<AttachmentsState>({
  items: [],
  open: false,
});

// MIME-type → DataKind heuristic. Handles the common cases; falls back
// to 'UInt8' for anything else (treats unknown payloads as opaque
// byte arrays, which is the safest default — the SQL author can
// `CAST(...)` if they need a specific kind).
export function inferKindFromMime(mime: string): AttachmentKind {
  if (!mime) return 'UInt8';
  if (mime.startsWith('image/')) return 'Image';
  if (mime.startsWith('audio/')) return 'Audio';
  if (mime.startsWith('video/')) return 'Video';
  return 'UInt8';
}

// Normalise a filename to a SQL-friendly identifier:
//   - drop the extension
//   - lowercase
//   - replace non-[a-z0-9_] with `_`
//   - prefix with `_` if it would start with a digit
//   - dedupe empty result to `attachment`
export function nameFromFilename(filename: string): string {
  const dot = filename.lastIndexOf('.');
  const stem = (dot > 0 ? filename.slice(0, dot) : filename).toLowerCase();
  let normalised = stem.replace(/[^a-z0-9_]/g, '_').replace(/_+/g, '_');
  normalised = normalised.replace(/^_+|_+$/g, '');
  if (!normalised) normalised = 'attachment';
  if (/^\d/.test(normalised)) normalised = `_${normalised}`;
  return normalised;
}

// Resolve a name collision by appending a numeric suffix. Idempotent
// when the name is already unique.
function uniquifyName(candidate: string, existing: ReadonlyArray<Attachment>): string {
  const taken = new Set(existing.map((a) => a.name));
  if (!taken.has(candidate)) return candidate;
  for (let i = 2; i < 10_000; i++) {
    const next = `${candidate}_${i}`;
    if (!taken.has(next)) return next;
  }
  // Theoretical fallthrough — 10k duplicates of one filename is the
  // user clearly trying to break things.
  return `${candidate}_${Date.now().toString(36)}`;
}

let nextId = 1;
function freshId(): string {
  return `att-${nextId++}`;
}

// Stage a dropped file. Inferred-name collisions auto-suffix; the user
// can rename from the panel. Returns the Attachment so the caller can
// surface the staged entry in the UI.
export function addAttachment(file: File): Attachment {
  const baseName = nameFromFilename(file.name);
  const name = uniquifyName(baseName, attachmentsState.items);
  const att: Attachment = {
    id: freshId(),
    name,
    originalFilename: file.name,
    kind: inferKindFromMime(file.type),
    size: file.size,
    blob: ref(file),
  };
  attachmentsState.items.push(att);
  return att;
}

export function removeAttachment(id: string): void {
  const idx = attachmentsState.items.findIndex((a) => a.id === id);
  if (idx >= 0) attachmentsState.items.splice(idx, 1);
}

// Rename an attachment. Returns true when the rename succeeded;
// returns false (and leaves the attachment unchanged) when the new
// name is empty, malformed, or collides with another attachment's
// name. The panel's input field re-validates on commit.
export function renameAttachment(id: string, newName: string): boolean {
  const trimmed = newName.trim();
  if (!trimmed || !/^[a-z_][a-z0-9_]*$/i.test(trimmed)) return false;
  const att = attachmentsState.items.find((a) => a.id === id);
  if (!att) return false;
  if (attachmentsState.items.some((a) => a.id !== id && a.name === trimmed)) {
    return false;
  }
  att.name = trimmed;
  return true;
}

// Pretty-print bytes for the panel ("1.2 MB"). Threshold-based so
// small files show 'B' / 'KB' instead of '0.0 MB'.
export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

// Find an attachment by SQL-side name (the form used in `$name`
// references). Case-insensitive to match the parameter binder's
// OrdinalIgnoreCase comparer on the server.
export function attachmentByName(name: string): Attachment | undefined {
  const lower = name.toLowerCase();
  return attachmentsState.items.find((a) => a.name.toLowerCase() === lower);
}

// All distinct $name references in a SQL string. Used by runQuery to
// build the multipart `parameters` payload, and by the Monaco
// autocomplete provider's match radar.
export function extractParameterNames(sql: string): string[] {
  const seen = new Set<string>();
  const re = /\$([a-z_][a-z0-9_]*)/gi;
  let m: RegExpExecArray | null;
  while ((m = re.exec(sql)) !== null) {
    seen.add(m[1]);
  }
  return Array.from(seen);
}
