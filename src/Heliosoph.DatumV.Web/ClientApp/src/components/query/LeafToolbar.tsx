import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { Lightbulb, Play, Square } from 'lucide-react';
import { panesState, findLeaf } from '@/state/tabs';
import { cancelTab, executionsState, runTab } from '@/state/execution';
import { runFunctionTab } from '@/state/functionForm';
import { runExplain, explainState } from '@/state/explain';
import { resolveRunSql } from '@/state/activeEditor';
import { cn } from '@/lib/utils';

// Vertical toolbar that flanks the editor + results column inside a
// leaf pane. Holds actions that target the leaf's currently active tab
// — today that's just Play/Stop; future entries (e.g. Explain) sit
// alongside it.
//
// Disabled with a muted appearance when the active tab kind has nothing
// to run (Models / Settings / Docs).
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

  const runLabel = t('run');
  const cancelLabel = t('cancel');
  const explainLabel = t('explain');
  const explainingLabel = t('explaining');

  function onPlayOrStop() {
    if (!activeTab || !runnable) return;
    if (isStreaming) {
      cancelTab(activeTab.id);
      return;
    }
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

  return (
    <div className="bg-background border-border flex w-9 shrink-0 flex-col items-center gap-1 border-r py-1">
      <button
        type="button"
        onClick={onPlayOrStop}
        disabled={!runnable}
        aria-label={isStreaming ? cancelLabel : runLabel}
        title={isStreaming ? cancelLabel : runLabel}
        className={cn(
          'flex size-7 items-center justify-center rounded-xs transition-colors',
          !runnable && 'cursor-default text-muted-foreground/40',
          runnable && isStreaming && 'cursor-pointer text-destructive hover:bg-destructive/15',
          runnable && !isStreaming && 'cursor-pointer text-primary hover:bg-primary/15',
        )}
      >
        {isStreaming ? <Square className="size-4" /> : <Play className="size-4" />}
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
    </div>
  );
}
