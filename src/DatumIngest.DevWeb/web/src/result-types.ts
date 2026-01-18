// Wire-format types for /api/query/stream and the in-memory result
// shapes the runtime accumulates from streaming events. Mirrors the C#
// DTOs in JsonCell.cs / NdjsonStreamingSink.cs / Program.cs (the
// `WriteEvent(new SchemaEvent...)` etc. calls). camelCase on the wire
// because ASP.NET Core's JsonSerializerOptions are configured to use
// camelCase property naming.

// ===== Schema column =====

export interface SchemaColumn {
  name: string;
  kind: string;
  isArray?: boolean;
}

// ===== Cell =====
//
// Server emits one of these per cell. `kind` discriminates the variant;
// `text` is the canonical string representation when no richer payload
// fits. `media` / `media_array` carry binary content base64-encoded;
// `json` carries pretty-printable JSON text the renderer parses
// client-side into a tree.

export interface MediaItem {
  mime: string;
  dataB64: string;
}

export type Cell =
  | { kind: 'null' }
  | { kind: 'media'; mime: string; dataB64: string; text?: string }
  | { kind: 'media_array'; items: MediaItem[]; text?: string }
  | { kind: 'json'; text: string }
  // Catchall for primitive / Type / Struct / etc. — server formats them
  // into a `text` field. The string `kind` is kept for diagnostics /
  // future cell-type extensions, but the renderer only inspects `text`.
  | { kind: string; text?: string };

// ===== Result set =====
//
// One per row-producing statement in the SQL. Multi-statement scripts
// produce multiple sets, each with its own schema + rows.

export interface ResultSet {
  schema: SchemaColumn[] | null;
  rows: Cell[][];
  rowCount: number;
  truncated: boolean;
}

// ===== Streaming model output chunk =====

export interface StreamingChunk {
  model: string;
  text: string;
}

// ===== In-memory result accumulator =====
//
// The runtime owns one of these per running tab; the same shape is
// also what gets persisted to IDB on completion.

export interface QueryResult {
  resultSets: ResultSet[];
  rowCount: number;
  elapsedMs: number;
  trace: string | null;
  error: string | null;
  detail?: string;
  sessionId: string | null;
  // Live token-stream chunks from `models.X` calls — all chunks across
  // all model invocations in the run, in arrival order. Used for the
  // streaming-output pane during the run and rebuilt from this list on
  // tab switch-back.
  chunks: StreamingChunk[];
}

// ===== Wire events from /api/query/stream =====

export type StreamEvent =
  | { type: 'session'; id: string }
  | { type: 'cell_started'; cell?: string }
  | { type: 'schema'; cell: string; columns: SchemaColumn[] }
  | { type: 'chunk'; cell: string; model: string; text: string }
  | { type: 'row'; cell: string; cells: Cell[] }
  | { type: 'truncated'; cell: string; rowCount: number }
  | { type: 'trace'; text: string }
  | { type: 'cell_completed'; cell?: string }
  | { type: 'complete'; elapsedMs: number }
  | { type: 'error'; cell?: string; message: string; detail?: string };
