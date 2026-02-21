import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { Play, Square } from 'lucide-react';
import { executionsState, runTab, cancelTab } from '@/state/execution';
import { tabsState } from '@/state/tabs';
import { resolveRunSql } from '@/state/activeEditor';
import { cn } from '@/lib/utils';

// Compact Run/Cancel + status strip rendered between the tab strip and
// the editor. Single button toggles Run ↔ Cancel based on execution
// status; status text on the right shows row count / elapsed / error
// for the active tab.

export function QueryToolbar() {
  const { t } = useTranslation('query');
  const { activeTabId } = useSnapshot(tabsState);
  const { byTabId } = useSnapshot(executionsState);

  if (activeTabId === null) {
    return <div className="border-border bg-background h-9 shrink-0 border-b" />;
  }

  const tab = tabsState.tabs.find((x) => x.id === activeTabId);
  if (!tab) {
    return <div className="border-border bg-background h-9 shrink-0 border-b" />;
  }

  const exec = byTabId[activeTabId];
  const status = exec?.status ?? 'idle';
  const isStreaming = status === 'streaming';

  function onPrimary() {
    if (!activeTabId) return;
    if (isStreaming) {
      cancelTab(activeTabId);
    } else {
      // Run highlighted SQL when the editor has a non-empty selection;
      // fall back to the whole tab otherwise. SSMS / DataGrip pattern.
      void runTab(activeTabId, resolveRunSql(tab!.sql));
    }
  }

  const rowCount = exec
    ? exec.cells.reduce((sum, c) => sum + c.rowCount, 0)
    : 0;
  const elapsed = exec?.elapsedMs;

  // Error message belongs in the results pane; toolbar just flags the
  // outcome so users know to look there. Keeps long parse-error
  // strings from blowing out the toolbar's single-line strip.
  let statusLine = '';
  if (status === 'streaming') statusLine = t('statusRunning', { rows: rowCount });
  else if (status === 'cancelled') statusLine = t('statusCancelled');
  else if (status === 'error') statusLine = t('statusError');
  else if (status === 'done') {
    const ms = elapsed !== null && elapsed !== undefined ? Math.round(elapsed) : 0;
    statusLine = t('statusDone', { rows: rowCount, ms });
  }

  return (
    <div className="border-border bg-background flex h-9 shrink-0 items-center gap-2 border-b px-2">
      <button
        type="button"
        onClick={onPrimary}
        aria-label={isStreaming ? t('cancel') : t('run')}
        title={isStreaming ? t('cancel') : t('run')}
        className={cn(
          'flex h-6 cursor-pointer items-center gap-1.5 rounded-xs px-2 text-xs font-medium transition-colors',
          isStreaming
            ? 'bg-destructive text-destructive-foreground hover:opacity-90'
            : 'bg-primary text-primary-foreground hover:opacity-90',
        )}
      >
        {isStreaming ? <Square className="size-3" /> : <Play className="size-3" />}
        {isStreaming ? t('cancel') : t('run')}
      </button>
      <span
        className={cn(
          'text-muted-foreground truncate text-xs',
          status === 'error' && 'text-destructive',
        )}
      >
        {statusLine}
      </span>
    </div>
  );
}

