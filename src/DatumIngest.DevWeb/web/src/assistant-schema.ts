// First-boot seed for the AI-assistant tables (conversations,
// messages, uploads). Idempotent — `CREATE TABLE IF NOT EXISTS`
// makes every call after the first a no-op against the engine's
// catalog. Ships as part of the dev assistant; the production
// inference service won't import this module.
//
// Type-name choices reflect the engine's DataKind enum, not SQL
// standard names: Int64 (not BIGINT), Int32 (not INT), String (not
// TEXT), DateTime (not TIMESTAMP). The schema mirrors the AI-
// assistant plan's m:1 attachment shape — `messages.upload_id` is
// nullable (every column is nullable by default) and a foreign key
// only by convention until cross-table FK constraints land.

const SEED_SQL = `
CREATE TABLE IF NOT EXISTS conversations (
  id         Int64 PRIMARY KEY IDENTITY,
  workspace  String,
  title      String,
  started_at DateTime
);

CREATE TABLE IF NOT EXISTS uploads (
  id          Int64 PRIMARY KEY IDENTITY,
  workspace   String,
  bytes       Image,
  mime        String,
  size_bytes  Int32,
  uploaded_at DateTime
);

CREATE TABLE IF NOT EXISTS messages (
  id              Int64 PRIMARY KEY IDENTITY,
  conversation_id Int64,
  turn_index      Int32,
  role            String,
  content         String,
  upload_id       Int64,
  tool_call_id    String,
  created_at      DateTime
);
`.trim();

let seedPromise: Promise<void> | null = null;

// Runs the seed once per page load. The catalog persists across
// reloads, so a second invocation immediately after the first would
// be a no-op anyway, but we still memoise the in-flight Promise so
// concurrent calls (e.g. assistant-panel toggle racing app boot)
// don't fire two stream requests.
export function ensureAssistantSchema(): Promise<void> {
  if (seedPromise) return seedPromise;

  seedPromise = (async () => {
    try {
      const response = await fetch('/api/query/stream', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sql: SEED_SQL, maxRows: 1 }),
      });
      if (!response.ok) {
        console.warn(
          '[assistant] schema seed HTTP error',
          response.status,
          await response.text().catch(() => ''),
        );
        return;
      }
      // Drain the NDJSON stream so any per-cell `error` events
      // surface in the console. CREATE TABLE IF NOT EXISTS produces
      // no rows; we only care that the executor didn't fail.
      if (!response.body) return;
      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buf = '';
      // eslint-disable-next-line no-constant-condition
      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        buf += decoder.decode(value, { stream: true });
        const lines = buf.split('\n');
        buf = lines.pop() ?? '';
        for (const line of lines) {
          const trimmed = line.trim();
          if (!trimmed) continue;
          try {
            const event = JSON.parse(trimmed) as { type: string; message?: string };
            if (event.type === 'error') {
              console.warn('[assistant] schema seed error:', event.message);
            }
          } catch {
            /* ignore */
          }
        }
      }
    } catch (err) {
      console.warn('[assistant] schema seed network error:', err);
    }
  })();

  return seedPromise;
}
