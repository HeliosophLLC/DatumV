import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { Plus, X } from 'lucide-react';
import {
  tabsState,
  openTab,
  closeTab,
  selectTab,
  renameTab,
} from '@/state/tabs';
import { cn } from '@/lib/utils';

// VSCode-style tab strip. Click to select, × to close, double-click to
// rename (prompt dialog for now — replace with an inline editable
// element when we have a styled component for it). The strip is a
// horizontal flex row with overflow-x-auto; tab order is the same as
// `tabsState.tabs`.

export function TabStrip() {
  const { t } = useTranslation('query');
  const { tabs, activeTabId } = useSnapshot(tabsState);

  return (
    <div className="border-border bg-background flex h-9 shrink-0 items-stretch border-b">
      <div className="flex flex-1 items-stretch overflow-x-auto">
        {tabs.map((tab) => (
          <TabChip
            key={tab.id}
            id={tab.id}
            title={tab.title}
            dirty={tab.dirty}
            active={tab.id === activeTabId}
            renameLabel={t('renameTab')}
            renamePromptLabel={t('renamePrompt')}
            closeLabel={t('closeTab')}
          />
        ))}
        <button
          type="button"
          onClick={() => openTab()}
          aria-label={t('newTab')}
          title={t('newTab')}
          className={cn(
            'text-muted-foreground hover:bg-muted hover:text-foreground',
            'flex shrink-0 cursor-pointer items-center justify-center px-3 transition-colors',
          )}
        >
          <Plus className="size-4" />
        </button>
      </div>
    </div>
  );
}

function TabChip({
  id,
  title,
  dirty,
  active,
  renameLabel,
  renamePromptLabel,
  closeLabel,
}: {
  id: string;
  title: string;
  dirty: boolean;
  active: boolean;
  renameLabel: string;
  renamePromptLabel: string;
  closeLabel: string;
}) {
  function onSelect() {
    selectTab(id);
  }

  function onRename() {
    // Native prompt is intentional for v1 — keeps the surface tiny.
    // Replace with an inline-editable component when we have one in
    // the design system.
    const next = window.prompt(renamePromptLabel, title);
    if (next === null) return;
    renameTab(id, next);
  }

  function onClose(e: React.MouseEvent) {
    e.stopPropagation();
    closeTab(id);
  }

  return (
    <div
      role="tab"
      aria-selected={active}
      onClick={onSelect}
      onDoubleClick={onRename}
      title={renameLabel}
      className={cn(
        'group border-border relative flex shrink-0 cursor-pointer items-center gap-2 border-r px-3 py-1.5 text-sm transition-colors',
        // Active indicator is a pseudo-element so it doesn't add to the
        // tab's box height — otherwise the strip's overflow-x:auto gets
        // auto-promoted to overflow-y:auto (CSS spec) and we get a 1 px
        // vertical scrollbar from the height delta.
        active
          ? 'bg-background text-foreground after:bg-primary after:absolute after:inset-x-0 after:bottom-0 after:h-0.5'
          : 'text-muted-foreground hover:bg-muted/40 hover:text-foreground',
      )}
    >
      <span className="max-w-[160px] truncate">
        {title}
        {dirty ? ' •' : ''}
      </span>
      <button
        type="button"
        onClick={onClose}
        aria-label={closeLabel}
        title={closeLabel}
        className={cn(
          'cursor-pointer text-muted-foreground hover:bg-muted hover:text-foreground rounded-xs p-0.5 opacity-0 transition-opacity',
          'group-hover:opacity-100',
          active && 'opacity-100',
        )}
      >
        <X className="size-3" />
      </button>
    </div>
  );
}
