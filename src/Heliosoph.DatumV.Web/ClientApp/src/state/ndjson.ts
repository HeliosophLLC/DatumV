// NDJSON-over-fetch reader. Two entry points:
//
//  - `postNdjson(url, body, signal)` — POSTs a JSON body and yields parsed
//    events from the response stream. Used by SQL tabs.
//
//  - `postNdjsonMultipart(url, formData, signal)` — POSTs a FormData body
//    (the browser sets the multipart boundary itself) and yields events.
//    Used by function tabs whose form has binary parameters; the server's
//    QueryRequestBinding dispatches on Content-Type.
//
// Both share the same reader half: yield JSON-per-line as the engine
// flushes, until either the body ends or the abort signal fires.
//
// Cancellation: when the AbortSignal fires, the fetch is cancelled and
// the iterator throws `AbortError`. Callers translate that into a
// "cancelled" status; the server sees `RequestAborted` and emits a
// terminal `error{message:"cancelled"}` event (which we won't receive
// after the abort, but the server-side cleanup still runs).

export async function* postNdjson<TEvent>(
  url: string,
  body: unknown,
  signal: AbortSignal,
): AsyncIterableIterator<TEvent> {
  const res = await fetch(url, {
    method: 'POST',
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      Accept: 'application/x-ndjson',
    },
    body: JSON.stringify(body),
    signal,
  });
  yield* readNdjsonResponse<TEvent>(res, signal);
}

export async function* postNdjsonMultipart<TEvent>(
  url: string,
  formData: FormData,
  signal: AbortSignal,
): AsyncIterableIterator<TEvent> {
  const res = await fetch(url, {
    method: 'POST',
    credentials: 'include',
    headers: {
      // No Content-Type — the browser fills in
      // `multipart/form-data; boundary=...` itself. Setting it manually
      // drops the boundary and the server's MultipartReader rejects
      // the request.
      Accept: 'application/x-ndjson',
    },
    body: formData,
    signal,
  });
  yield* readNdjsonResponse<TEvent>(res, signal);
}

async function* readNdjsonResponse<TEvent>(
  res: Response,
  signal: AbortSignal,
): AsyncIterableIterator<TEvent> {
  if (!res.ok) {
    // Pre-stream failures (4xx/5xx with a JSON error body) — surface
    // the error so the caller can show it instead of a silent abort.
    const text = await res.text().catch(() => '');
    throw new Error(
      `HTTP ${res.status} ${res.statusText}${text ? `: ${text}` : ''}`,
    );
  }

  if (!res.body) {
    throw new Error('Response has no body');
  }

  const reader = res.body.getReader();
  const decoder = new TextDecoder('utf-8');
  let buffer = '';

  try {
    while (true) {
      // Explicit abort check before each read. Browsers vary on how
      // promptly an aborted fetch causes the reader to reject — some
      // keep draining already-buffered chunks. Checking `signal.aborted`
      // here guarantees a tight exit when the user clicks Cancel.
      if (signal.aborted) {
        throw new DOMException('Aborted', 'AbortError');
      }

      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });

      // Yield each complete line; keep any trailing partial line in
      // the buffer for the next read iteration.
      let newlineIdx: number;
      while ((newlineIdx = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, newlineIdx);
        buffer = buffer.slice(newlineIdx + 1);
        if (line.length === 0) continue;
        yield JSON.parse(line) as TEvent;
        // Also check between events — if the consumer (runTab) doesn't
        // re-iterate before the signal fires, the loop's top-of-iteration
        // check above would catch it on the next read; but bailing here
        // avoids one extra event being applied to state after Cancel.
        if (signal.aborted) {
          throw new DOMException('Aborted', 'AbortError');
        }
      }
    }

    // Flush any final line that wasn't newline-terminated (the server
    // always terminates, but be lenient for resilience).
    const tail = buffer.trim();
    if (tail.length > 0) {
      yield JSON.parse(tail) as TEvent;
    }
  } finally {
    // Best-effort cleanup. cancel() rejects the underlying reader's
    // outstanding read AND signals the browser to close the connection
    // (the network tab will show the request as cancelled). Without
    // this, an aborted fetch sometimes leaves the connection draining
    // in the background.
    try {
      await reader.cancel();
    } catch {
      /* already cancelled or released */
    }
    try {
      reader.releaseLock();
    } catch {
      /* already released */
    }
  }
}
