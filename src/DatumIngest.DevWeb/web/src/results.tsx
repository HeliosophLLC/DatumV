// React components that render a single group's results pane. Mounted
// once per group by main.ts via mountResultsPane(); unmounted when the
// group is dissolved. Components subscribe to the valtio state via
// useSnapshot — no imperative re-render calls; mutations through the
// proxy trigger the right components to re-render automatically.

import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import { useSnapshot } from 'valtio';
import { useVirtualizer } from '@tanstack/react-virtual';
import {
  state,
  type SelectionState,
  type Tab,
} from './state.js';
import {
  renderJsonNode,
  type JsonValue,
} from './json-render.js';
import { openImageLightbox } from './modal.js';
import * as IDB from './idb.js';
import {
  acquireMediaUrl,
  releaseMediaUrl,
} from './media-url-cache.js';
import {
  isCellSelected,
  isColSelected,
  selectCell,
  selectColumn,
} from './selection.js';
import type {
  Cell as CellData,
  MediaItem,
  QueryResult,
  ResultSet,
  SchemaColumn,
  StreamingChunk,
} from './result-types.js';

// ===== Top-level pane =====

export function ResultsPane({ groupId }: { groupId: string }) {
  const snap = useSnapshot(state);
  const group = snap.groups.find((g) => g.id === groupId);
  if (!group) return null;
  const tab = snap.tabs.find((t) => t.id === group.activeTabId);
  if (!tab) return null;
  const selection = (tab.selection ?? null) as SelectionState | null;

  if (tab.running && tab.runningRes) {
    return (
      <RunningView
        res={tab.runningRes as QueryResult}
        tabId={tab.id}
        selection={selection}
      />
    );
  }

  if (tab.lastResult === undefined) {
    return <SavedResultLoader tabId={tab.id} groupId={groupId} />;
  }

  if (!tab.lastResult) {
    return (
      <div className="meta">
        {tab.lastRunAt
          ? '(saved result not found — re-run to see it)'
          : 'No results yet. Press Run.'}
      </div>
    );
  }

  return (
    <FinalResultView
      res={tab.lastResult as QueryResult}
      tabId={tab.id}
      selection={selection}
    />
  );
}

// ===== Saved-result loader =====
//
// tab.lastResult === undefined means "the snapshot was hydrated from
// localStorage and we haven't fetched the body from IDB yet." Show a
// placeholder; on success/failure write the result back to the live
// proxy so this component doesn't re-mount the loader on the next
// re-render. Guard against the user switching tabs in this group while
// the IDB read is in flight.

function SavedResultLoader({
  tabId,
  groupId,
}: {
  tabId: string;
  groupId: string;
}) {
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    const stillActive = () => {
      const liveGroup = state.groups.find((g) => g.id === groupId);
      return !!liveGroup && liveGroup.activeTabId === tabId;
    };
    (async () => {
      try {
        const r = (await IDB.loadResult(tabId)) as QueryResult | null;
        if (cancelled || !stillActive()) return;
        const liveTab = state.tabs.find((t) => t.id === tabId);
        if (liveTab) liveTab.lastResult = r;
      } catch (err) {
        if (cancelled || !stillActive()) return;
        const liveTab = state.tabs.find((t) => t.id === tabId);
        if (liveTab) liveTab.lastResult = null;
        setError(err instanceof Error ? err.message : String(err));
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [tabId, groupId]);

  if (error) return <div className="error">Couldn't load saved result: {error}</div>;
  return <div className="meta">Loading saved result…</div>;
}

// ===== Running view =====
//
// In-flight query. Shows a "Running…" line plus the streaming model
// output (if any) plus any result sets that have schemas/rows so far.

function RunningView({
  res,
  tabId,
  selection,
}: {
  res: QueryResult;
  tabId: string;
  selection: SelectionState | null;
}) {
  return (
    <>
      <div className="meta">
        Running… (Esc to cancel) · {res.rowCount.toLocaleString()}{' '}
        {res.rowCount === 1 ? 'row' : 'rows'} so far
      </div>
      {res.chunks.length > 0 && <StreamingOutput chunks={res.chunks} />}
      {res.resultSets.map((set, i) => (
        <ResultSetView
          key={i}
          set={set}
          label={res.resultSets.length > 1 ? i + 1 : null}
          tabId={tabId}
          setIndex={i}
          selection={selection}
        />
      ))}
    </>
  );
}

function StreamingOutput({ chunks }: { chunks: readonly StreamingChunk[] }) {
  const preRef = useRef<HTMLPreElement>(null);
  // Auto-scroll to bottom on new chunks. Reading chunks.length keeps the
  // effect dependency stable across the array growing.
  useEffect(() => {
    const el = preRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [chunks.length]);
  const headerModel = chunks[0]?.model ?? '';
  return (
    <>
      <div className="meta stream-header">streaming from models.{headerModel}</div>
      <pre ref={preRef} className="streaming-output">
        {chunks.map((c) => c.text).join('')}
      </pre>
    </>
  );
}

// ===== Final result view =====

function FinalResultView({
  res,
  tabId,
  selection,
}: {
  res: QueryResult;
  tabId: string;
  selection: SelectionState | null;
}) {
  if (res.error) {
    return (
      <div className="error">
        {res.error}
        {res.detail ? `\n\n${res.detail}` : ''}
      </div>
    );
  }
  return (
    <>
      {res.resultSets.length === 0 && <div className="meta">(no rows)</div>}
      {res.resultSets.map((set, i) => (
        <ResultSetView
          key={i}
          set={set}
          label={res.resultSets.length > 1 ? i + 1 : null}
          tabId={tabId}
          setIndex={i}
          selection={selection}
        />
      ))}
      {res.trace && <TraceView trace={res.trace} />}
    </>
  );
}

function TraceView({ trace }: { trace: string }) {
  const lineCount = useMemo(
    () => trace.split('\n').filter((l) => l.length > 0).length,
    [trace],
  );
  return (
    <details className="trace">
      <summary>
        Execution trace ({lineCount.toLocaleString()}{' '}
        {lineCount === 1 ? 'line' : 'lines'})
      </summary>
      <pre>{trace}</pre>
    </details>
  );
}

// ===== Result set =====

function ResultSetView({
  set,
  label,
  tabId,
  setIndex,
  selection,
}: {
  set: ResultSet;
  label: number | null;
  tabId: string;
  setIndex: number;
  selection: SelectionState | null;
}) {
  // Selection is scoped to a single result set's coordinate space —
  // pass null down for sets the selection isn't in so they don't paint
  // stale row/col highlights.
  const ownSelection =
    selection && selection.resultSetIndex === setIndex ? selection : null;
  return (
    <>
      {label !== null && (
        <div className="meta result-set-label">
          Result {label} · {set.rowCount.toLocaleString()}{' '}
          {set.rowCount === 1 ? 'row' : 'rows'}
        </div>
      )}
      {set.truncated && (
        <div className="warn">
          ⚠ Truncated at {set.rowCount} rows (raise the max rows control to see
          more).
        </div>
      )}
      {!set.schema || set.schema.length === 0 ? (
        <div className="meta">(no columns)</div>
      ) : (
        <DataTable
          schema={set.schema}
          rows={set.rows}
          tabId={tabId}
          setIndex={setIndex}
          selection={ownSelection}
        />
      )}
    </>
  );
}

// ===== Data table =====
//
// Below this row count, the table renders every row to the DOM (the
// "plain" path that's been here since Phase 3 — known good, all existing
// behaviour intact). At/above this threshold, switch to the virtualized
// path so 20k-row queries don't blow up the DOM. 500 chosen as a point
// where the plain path's cost is still negligible (~6.5k DOM nodes for
// 13 columns) but virtualization's first-paint measurement jitter would
// be unwelcome.

const VIRTUALIZE_THRESHOLD = 500;

interface DataTableProps {
  schema: readonly SchemaColumn[];
  rows: readonly (readonly CellData[])[];
  tabId: string;
  setIndex: number;
  selection: SelectionState | null;
}

function DataTable(props: DataTableProps) {
  if (props.rows.length < VIRTUALIZE_THRESHOLD) {
    return <PlainDataTable {...props} />;
  }
  return <VirtualDataTable {...props} />;
}

// Header is rendered identically in both variants — extracted so a
// schema change doesn't touch row code.
function HeaderRow({
  schema,
  selection,
  tabId,
  setIndex,
}: Pick<DataTableProps, 'schema' | 'selection' | 'tabId' | 'setIndex'>) {
  return (
    <tr>
      {schema.map((col, ci) => {
        const colSelected = isColSelected(selection, ci);
        return (
          <th
            key={ci}
            className={colSelected ? 'col-selected' : undefined}
            onClick={(e) => {
              selectColumn(tabId, setIndex, ci, {
                shift: e.shiftKey,
                meta: e.metaKey || e.ctrlKey,
              });
            }}
          >
            {col.name}
            <span className="kind">
              {col.isArray ? `Array<${col.kind}>` : col.kind}
            </span>
          </th>
        );
      })}
    </tr>
  );
}

// One row's <td> children, shared by both plain and virtual paths.
function RowCells({
  row,
  rowIndex,
  selection,
  tabId,
  setIndex,
}: {
  row: readonly CellData[];
  rowIndex: number;
  selection: SelectionState | null;
  tabId: string;
  setIndex: number;
}) {
  return (
    <>
      {row.map((cell, ci) => (
        <Cell
          key={ci}
          cell={cell}
          row={rowIndex}
          col={ci}
          tabId={tabId}
          setIndex={setIndex}
          selected={isCellSelected(selection, rowIndex, ci)}
        />
      ))}
    </>
  );
}

// ===== Plain (non-virtualized) data table =====

function PlainDataTable({
  schema,
  rows,
  tabId,
  setIndex,
  selection,
}: DataTableProps) {
  return (
    <table className="results">
      <thead>
        <HeaderRow
          schema={schema}
          selection={selection}
          tabId={tabId}
          setIndex={setIndex}
        />
      </thead>
      <tbody>
        {rows.map((row, ri) => (
          <tr key={ri}>
            <RowCells
              row={row}
              rowIndex={ri}
              selection={selection}
              tabId={tabId}
              setIndex={setIndex}
            />
          </tr>
        ))}
      </tbody>
    </table>
  );
}

// ===== Virtualized data table =====
//
// Wraps the table in its own scroll container with a max height so large
// result sets don't push later content (additional result sets, trace,
// etc.) off-screen behind a 20k-row table. Rows are measured dynamically:
// estimateSize is a starting hint; useVirtualizer's measureElement reads
// each rendered row's actual height and re-anchors. With cell content
// capped at 200px (.cell-content max-height), row heights stay bounded.
//
// Inside <tbody>, top/bottom spacer rows occupy the height of the
// not-yet-rendered ranges so the table's natural scroll height matches
// what the virtualizer reports. Spacer rows are <tr><td colSpan>
// elements rather than absolute-positioned divs because absolute
// positioning inside a <table> is unreliable across browsers.

const ROW_HEIGHT_ESTIMATE = 32;

function VirtualDataTable({
  schema,
  rows,
  tabId,
  setIndex,
  selection,
}: DataTableProps) {
  const scrollRef = useRef<HTMLDivElement>(null);

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT_ESTIMATE,
    // 8 rows on each side gives a smooth scroll without paying for
    // them per-frame.
    overscan: 8,
  });

  const virtualItems = virtualizer.getVirtualItems();
  const totalSize = virtualizer.getTotalSize();
  const topSpacer = virtualItems.length > 0 ? virtualItems[0].start : 0;
  const lastVisible = virtualItems[virtualItems.length - 1];
  const bottomSpacer = lastVisible
    ? Math.max(0, totalSize - (lastVisible.start + lastVisible.size))
    : 0;

  return (
    <div className="results-virtual-scroll" ref={scrollRef}>
      <table className="results">
        <thead>
          <HeaderRow
            schema={schema}
            selection={selection}
            tabId={tabId}
            setIndex={setIndex}
          />
        </thead>
        <tbody>
          {topSpacer > 0 && (
            <tr aria-hidden="true" style={{ height: topSpacer }}>
              <td colSpan={schema.length} />
            </tr>
          )}
          {virtualItems.map((vi) => {
            const ri = vi.index;
            const row = rows[ri];
            if (!row) return null;
            return (
              <tr
                key={ri}
                data-index={ri}
                ref={virtualizer.measureElement}
              >
                <RowCells
                  row={row}
                  rowIndex={ri}
                  selection={selection}
                  tabId={tabId}
                  setIndex={setIndex}
                />
              </tr>
            );
          })}
          {bottomSpacer > 0 && (
            <tr aria-hidden="true" style={{ height: bottomSpacer }}>
              <td colSpan={schema.length} />
            </tr>
          )}
        </tbody>
      </table>
    </div>
  );
}

// ===== Cell switch =====

interface CellSelectionProps {
  row: number;
  col: number;
  tabId: string;
  setIndex: number;
  selected: boolean;
}

function Cell({
  cell,
  ...sel
}: { cell: CellData } & CellSelectionProps) {
  if (cell.kind === 'null') return <NullCell {...sel} />;
  if (
    cell.kind === 'media_array' &&
    Array.isArray((cell as { items?: unknown }).items)
  ) {
    return (
      <MediaArrayCell
        items={(cell as { items: MediaItem[] }).items}
        {...sel}
      />
    );
  }
  if (cell.kind === 'media' && (cell as { mime?: string }).mime) {
    const c = cell as { mime: string; dataB64: string };
    return <MediaCell mime={c.mime} dataB64={c.dataB64} {...sel} />;
  }
  if (cell.kind === 'json') {
    return <JsonCell text={cell.text ?? ''} {...sel} />;
  }
  // Catchall: primitives, Type, Struct, etc. — server formatted them
  // into `text`. Cast away the discriminated-union narrowing since TS
  // can't see that we've ruled out the typed variants above.
  const text = (cell as { text?: string }).text ?? '';
  return <TextCell text={text} {...sel} />;
}

// SelectableTd wraps each variant's <td>: applies the .selected class
// (plus an optional kind-specific class like "null" or "blob") and
// wires the click handler that mutates tab.selection through valtio.
//
// Click handlers inside cell content (CopyButton, MediaCell's image
// click → lightbox) call e.stopPropagation() so they don't also flip
// selection. The default click bubbles up here.
function SelectableTd({
  row,
  col,
  tabId,
  setIndex,
  selected,
  extraClass,
  children,
}: CellSelectionProps & {
  extraClass?: string;
  children: ReactNode;
}) {
  const cls =
    [extraClass, selected ? 'selected' : null].filter(Boolean).join(' ') ||
    undefined;
  return (
    <td
      className={cls}
      onClick={(e) => {
        selectCell(tabId, setIndex, row, col, {
          shift: e.shiftKey,
          meta: e.metaKey || e.ctrlKey,
        });
      }}
    >
      {children}
    </td>
  );
}

// ===== Cell variants =====

function NullCell(sel: CellSelectionProps) {
  return (
    <SelectableTd {...sel} extraClass="null">
      NULL
      <CopyButton getText={() => 'NULL'} />
    </SelectableTd>
  );
}

function TextCell({ text, ...sel }: { text: string } & CellSelectionProps) {
  return (
    <SelectableTd {...sel}>
      <pre>{text}</pre>
      <CopyButton getText={() => text} />
    </SelectableTd>
  );
}

function JsonCell({ text, ...sel }: { text: string } & CellSelectionProps) {
  // Server already decoded CBOR → JSON text. Render as a collapsible
  // tree so deep structures stay inspectable. If the text doesn't parse
  // (shouldn't happen — server only emits this kind on successful
  // decode), fall back to a flat pretty-printed pre.
  const containerRef = useRef<HTMLDivElement>(null);
  const parsed = useMemo<JsonValue | null>(() => {
    try {
      return JSON.parse(text) as JsonValue;
    } catch {
      return null;
    }
  }, [text]);

  // The renderJsonNode helper returns a DOM node (it predates this
  // React rewrite). Mount it via a ref + useEffect rather than rewriting
  // it as JSX — Phase-3 scope is the table shell, not the JSON tree.
  useEffect(() => {
    const host = containerRef.current;
    if (!host || parsed === null) return;
    const node = renderJsonNode(parsed);
    host.appendChild(node);
    return () => {
      if (node.parentNode) node.parentNode.removeChild(node);
    };
  }, [parsed]);

  if (parsed === null) {
    return (
      <SelectableTd {...sel}>
        <pre className="json">{text}</pre>
        <CopyButton getText={() => text} />
      </SelectableTd>
    );
  }
  return (
    <SelectableTd {...sel}>
      <div ref={containerRef} className="json-tree" />
      <CopyButton getText={() => JSON.stringify(parsed, null, 2)} />
    </SelectableTd>
  );
}

// ===== Media =====
//
// `data:` URLs hit Chromium's ~2 MB URL-length cap (which disables
// "Open image in new tab" for large images even when the inline render
// works). Blob URLs avoid that. We create one per (mime, dataB64) pair
// and revoke on unmount via useEffect cleanup — replaces the legacy
// per-group mediaUrlCollector pattern.

function useBlobUrl(dataB64: string, mime: string): string | null {
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    const u = acquireMediaUrl(mime, dataB64);
    setUrl(u);
    return () => releaseMediaUrl(mime, dataB64);
  }, [dataB64, mime]);
  return url;
}

function MediaCell({
  mime,
  dataB64,
  ...sel
}: { mime: string; dataB64: string } & CellSelectionProps) {
  const url = useBlobUrl(dataB64, mime);
  const bytes = Math.floor((dataB64.length * 3) / 4);
  let media: ReactNode = null;
  if (url) {
    if (mime.startsWith('image/')) {
      media = (
        <img
          src={url}
          alt=""
          title="Click to expand"
          onClick={(e) => {
            // Don't bubble — image-click toggles the lightbox, not the
            // selection.
            e.stopPropagation();
            openImageLightbox(url);
          }}
        />
      );
    } else if (mime.startsWith('audio/')) {
      media = <audio controls src={url} onClick={(e) => e.stopPropagation()} />;
    } else if (mime.startsWith('video/')) {
      media = <video controls src={url} onClick={(e) => e.stopPropagation()} />;
    } else {
      media = <span>{`<${mime}>`}</span>;
    }
  }
  return (
    <SelectableTd {...sel}>
      {media}
      <div className="blob">
        {mime} · {bytes.toLocaleString()} bytes
      </div>
    </SelectableTd>
  );
}

function MediaArrayCell({
  items,
  ...sel
}: { items: readonly MediaItem[] } & CellSelectionProps) {
  // Filter to images-only to match legacy behaviour; non-image array
  // members are silently skipped.
  const imageItems = items.filter(
    (item) => item && item.mime && item.mime.startsWith('image/'),
  );
  return (
    <SelectableTd {...sel}>
      <div className="media-array">
        {imageItems.map((item, i) => (
          <MediaArrayThumb key={i} item={item} />
        ))}
      </div>
      <div className="blob">
        {items.length} {items.length === 1 ? 'image' : 'images'}
      </div>
    </SelectableTd>
  );
}

function MediaArrayThumb({ item }: { item: MediaItem }) {
  const url = useBlobUrl(item.dataB64, item.mime);
  if (!url) return null;
  return (
    <img
      src={url}
      alt=""
      title="Click to expand"
      onClick={(e) => {
        e.stopPropagation();
        openImageLightbox(url);
      }}
    />
  );
}

// ===== Copy button =====

const COPY_ICON = (
  <svg
    viewBox="0 0 24 24"
    width="12"
    height="12"
    fill="none"
    stroke="currentColor"
    strokeWidth="1.6"
  >
    <rect x="9" y="9" width="11" height="11" rx="1.5" />
    <path d="M5 15V5a2 2 0 0 1 2-2h10" />
  </svg>
);
const CHECK_ICON = (
  <svg
    viewBox="0 0 24 24"
    width="12"
    height="12"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
  >
    <polyline points="5 12 10 17 19 7" />
  </svg>
);

function CopyButton({ getText }: { getText: () => string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      className={'cell-copy' + (copied ? ' copied' : '')}
      title="Copy"
      aria-label="Copy cell value"
      onClick={async (e) => {
        // Don't bubble into the cell — image cells handle clicks for
        // the lightbox, and we don't want a stray copy click to also
        // expand.
        e.stopPropagation();
        try {
          await navigator.clipboard.writeText(getText());
          setCopied(true);
          setTimeout(() => setCopied(false), 900);
        } catch (err) {
          console.warn('[DatumIngest] clipboard.writeText failed:', err);
        }
      }}
    >
      {copied ? CHECK_ICON : COPY_ICON}
    </button>
  );
}

// Re-export the elapsed-stats text formatter for the imperative toolbar
// renderer (the elapsed slot lives in the editor toolbar, not the
// results pane, and stays imperative for now).
export function formatFinalStats(res: QueryResult): string {
  if (res.error) return '';
  const rowText = `${res.rowCount} ${res.rowCount === 1 ? 'row' : 'rows'}`;
  const timeText =
    typeof res.elapsedMs === 'number' ? `${(res.elapsedMs / 1000).toFixed(2)} s` : '';
  return timeText ? `${rowText} · ${timeText}` : rowText;
}

// Allow main.ts to look up the active tab without re-importing state
// helpers all over the place.
export type { Tab };
