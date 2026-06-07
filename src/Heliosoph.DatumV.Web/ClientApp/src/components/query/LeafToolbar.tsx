import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { Download, Lightbulb, Play, Square } from 'lucide-react';
import { panesState, findLeaf } from '@/state/tabs';
import { cancelTab, executionsState, runTab } from '@/state/execution';
import { runFunctionTab } from '@/state/functionForm';
import { runExplain, explainState } from '@/state/explain';
import { resolveRunSql } from '@/state/activeEditor';
import { beginExport } from '@/state/export';
import { cn } from '@/lib/utils';

// Vertical toolbar that flanks the editor + results column inside a
// leaf pane. Holds actions that target the leaf's currently active tab
// — Play/Stop, Explain, and Export.
//
// The Run and Export buttons share the underlying execution stream (a
// COPY statement is just a statement), so only one can be streaming at
// a time. The toolbar reads `exec.origin` to decide which button shows
// the Stop affordance — Run while streaming a normal query, Export
// while streaming an export — and disables the other one until the
// stream terminates. Cancellation routes through whichever button is
// currently in Stop mode.
//
// Disabled with a muted appearance when the active tab kind has
// nothing to run (Models / Settings / Docs / function tabs for
// Explain/Export).
export function LeafToolbar({ leafId }: { leafId: string }) {
  const { t } = useTranslation('query');
  useSnapshot(panesState);
  useSnapshot(executionsState);
  useSnapshot(explainState);

  const leaf = findLeaf(panesState.root, leafId);
  const activeTab =
    leaf && leaf.activeTabId !== null
      ? leaf.tabs.find((tab) => tab.id === leaf.activeTabId) ?? null
      : null;
  const exec = activeTab ? executionsState.byTabId[activeTab.id] : null;
  const isStreaming = exec?.status === 'streaming';
  const isExportStreaming = isStreaming && exec?.origin === 'export';
  const isRunStreaming = isStreaming && exec?.origin !== 'export';
  const explain = activeTab ? explainState.byTabId[activeTab.id] : null;
  const isExplaining = explain?.status === 'running';

  // EXPLAIN is only meaningful for SQL-text tabs. Function tabs synthesise
  // their script from form state and don't currently have a single-string
  // SQL surface to ship to /api/query/explain. Pinned tabs (Models /
  // Settings / Docs) have no SQL at all.
  const runnable =
    activeTab !== null &&
    activeTab.kind !== 'models' &&
    activeTab.kind !== 'settings' &&
    activeTab.kind !== 'docs';
  const explainable = activeTab !== null && activeTab.kind === 'sql';
  // Export is scoped to SQL tabs: function tabs would need a separate
  // export shape (no single SQL surface), and pinned tabs have no SQL.
  const exportable = activeTab !== null && activeTab.kind === 'sql';

  const runLabel = t('run');
  const cancelLabel = t('cancel');
  const explainLabel = t('explain');
  const explainingLabel = t('explaining');
  const exportLabel = t('export');
  const exportingLabel = t('exporting');
  const cancelExportLabel = t('cancelExport');

  function onPlayOrStop() {
    if (!activeTab || !runnable) return;
    if (isRunStreaming) {
      cancelTab(activeTab.id);
      return;
    }
    // Don't start a new run while an export is in flight — the Run
    // button is disabled in that state, but guard anyway in case the
    // accelerator fires via the menu.
    if (isExportStreaming) return;
    if (activeTab.kind === 'function') {
      void runFunctionTab(activeTab.id);
      return;
    }
    void runTab(activeTab.id, resolveRunSql(activeTab.sql, leafId));
  }

  function onExplain() {
    if (!activeTab || !explainable) return;
    void runExplain(activeTab.id, resolveRunSql(activeTab.sql, leafId));
  }

  function onExportOrStop() {
    if (!activeTab || !exportable) return;
    if (isExportStreaming) {
      cancelTab(activeTab.id);
      return;
    }
    // Guard against starting an export while a run is in flight — the
    // button is disabled, but the menu accelerator could still fire.
    if (isRunStreaming) return;
    void beginExport(leafId);
  }

  return (
    <div className="bg-background border-border flex w-9 shrink-0 flex-col items-center gap-1 border-r py-1">
      <button
        type="button"
        onClick={onPlayOrStop}
        disabled={!runnable || isExportStreaming}
        aria-label={isRunStreaming ? cancelLabel : runLabel}
        title={isRunStreaming ? cancelLabel : runLabel}
        className={cn(
          'flex size-7 items-center justify-center rounded-xs transition-colors',
          (!runnable || isExportStreaming) && 'cursor-default text-muted-foreground/40',
          runnable && !isExportStreaming && isRunStreaming && 'cursor-pointer text-destructive hover:bg-destructive/15',
          runnable && !isExportStreaming && !isRunStreaming && 'cursor-pointer text-primary hover:bg-primary/15',
        )}
      >
        {isRunStreaming ? <Square className="size-4" /> : <Play className="size-4" />}
      </button>
      <button
        type="button"
        onClick={onExplain}
        disabled={!explainable || isExplaining || isStreaming}
        aria-label={isExplaining ? explainingLabel : explainLabel}
        title={isExplaining ? explainingLabel : explainLabel}
        className={cn(
          'flex size-7 items-center justify-center rounded-xs transition-colors',
          (!explainable || isStreaming) && 'cursor-default text-muted-foreground/40',
          explainable && !isStreaming && !isExplaining && 'cursor-pointer text-muted-foreground hover:bg-muted hover:text-foreground',
          explainable && !isStreaming && isExplaining && 'cursor-wait text-muted-foreground animate-pulse',
        )}
      >
        <Lightbulb className="size-4" />
      </button>
      <button
        type="button"
        onClick={onExportOrStop}
        disabled={!exportable || isRunStreaming}
        aria-label={isExportStreaming ? cancelExportLabel : exportLabel}
        title={isExportStreaming ? exportingLabel : exportLabel}
        className={cn(
          'flex size-7 items-center justify-center rounded-xs transition-colors',
          (!exportable || isRunStreaming) && 'cursor-default text-muted-foreground/40',
          exportable && !isRunStreaming && isExportStreaming && 'cursor-pointer text-destructive hover:bg-destructive/15',
          exportable && !isRunStreaming && !isExportStreaming && 'cursor-pointer text-muted-foreground hover:bg-muted hover:text-foreground',
        )}
      >
        {isExportStreaming ? <Square className="size-4" /> : <Download className="size-4" />}
      </button>
    </div>
  );
}
