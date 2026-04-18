import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { History, Plus, RotateCw, Scissors } from 'lucide-react';
import {
  compactConversation,
  conversationState,
  newConversation,
  reloadConversation,
  switchConversation,
  type ConversationSummary,
} from '@/state/conversation';
import { cn } from '@/lib/utils';

// Header bar above the chat surface. Three actions:
//   * History  — opens a dropdown listing prior conversations; click one
//                to switch.
//   * New      — creates a fresh conversation and switches to it.
//   * Reload   — drops the server's accumulator for the active
//                conversation and re-reads messages from the database.
//
// Reload + History + New are disabled while a turn is mid-flight (the
// server reload would clobber the in-flight prompt; conversation
// switching during a stream is ambiguous).
export function ChatToolbar() {
  const { t } = useTranslation('chat');
  const { conversations, conversationId, messages, status, compacting } =
    useSnapshot(conversationState);
  const [historyOpen, setHistoryOpen] = useState(false);
  const isStreaming = status === 'awaiting' || status === 'streaming';
  const busy = isStreaming || compacting;
  // Compaction only makes sense when there's something to summarise —
  // disable the button on empty conversations.
  const hasTurns = messages.some((m) => m.kind === 'turn');
  const popoverRef = useRef<HTMLDivElement>(null);

  // Close the popover on outside click. Mouse-down beats click so the
  // dismissal happens before any item-click would resolve.
  useEffect(() => {
    if (!historyOpen) return;
    function onMouseDown(e: MouseEvent) {
      if (popoverRef.current && !popoverRef.current.contains(e.target as Node)) {
        setHistoryOpen(false);
      }
    }
    document.addEventListener('mousedown', onMouseDown);
    return () => document.removeEventListener('mousedown', onMouseDown);
  }, [historyOpen]);

  async function onSelect(id: number) {
    setHistoryOpen(false);
    await switchConversation(id);
  }

  async function onNew() {
    setHistoryOpen(false);
    await newConversation();
  }

  return (
    <div className="flex items-center justify-end gap-1 border-b px-3 py-1">
      <div ref={popoverRef} className="relative">
        <ToolbarButton
          icon={<History className="size-3.5" />}
          label={t('history')}
          onClick={() => setHistoryOpen((v) => !v)}
          disabled={busy}
          ariaExpanded={historyOpen}
        />
        {historyOpen && (
          <HistoryPopover
            conversations={conversations}
            activeId={conversationId}
            onSelect={onSelect}
          />
        )}
      </div>
      <ToolbarButton
        icon={<Plus className="size-3.5" />}
        label={t('newConversation')}
        onClick={onNew}
        disabled={busy}
      />
      <ToolbarButton
        icon={<Scissors className="size-3.5" />}
        label={t('compact')}
        title={t('compactHint')}
        onClick={() => void compactConversation()}
        disabled={busy || !hasTurns}
      />
      <ToolbarButton
        icon={<RotateCw className="size-3.5" />}
        label={t('reload')}
        title={t('reloadHint')}
        onClick={reloadConversation}
        disabled={busy}
      />
    </div>
  );
}

function ToolbarButton({
  icon,
  label,
  title,
  onClick,
  disabled,
  ariaExpanded,
}: {
  icon: React.ReactNode;
  label: string;
  title?: string;
  onClick: () => void;
  disabled?: boolean;
  ariaExpanded?: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      title={title ?? label}
      aria-label={label}
      aria-expanded={ariaExpanded}
      className={cn(
        'inline-flex items-center gap-1.5 rounded-xs px-2 py-1 text-xs',
        'text-muted-foreground hover:text-foreground hover:bg-muted',
        'disabled:cursor-not-allowed disabled:opacity-40',
        'transition-colors cursor-pointer',
      )}
    >
      {icon}
      {label}
    </button>
  );
}

function HistoryPopover({
  conversations,
  activeId,
  onSelect,
}: {
  conversations: readonly ConversationSummary[];
  activeId: number | null;
  onSelect: (id: number) => void;
}) {
  const { t } = useTranslation('chat');
  return (
    <div
      role="listbox"
      className={cn(
        'bg-popover text-popover-foreground absolute right-0 top-full z-50 mt-1',
        'min-w-[14rem] max-w-[20rem] max-h-[24rem] overflow-y-auto',
        'rounded-xs border shadow-md',
      )}
    >
      {conversations.length === 0 ? (
        <div className="text-muted-foreground px-3 py-2 text-xs">
          {t('historyEmpty')}
        </div>
      ) : (
        <ul className="py-1">
          {conversations.map((c) => (
            <li key={c.id}>
              <button
                type="button"
                onClick={() => onSelect(c.id)}
                aria-selected={c.id === activeId}
                className={cn(
                  'w-full px-3 py-1.5 text-left text-xs',
                  'hover:bg-muted',
                  c.id === activeId && 'bg-muted/70 font-medium',
                )}
              >
                <div className="truncate">{c.title ?? t('historyUntitled')}</div>
                <div className="text-muted-foreground text-[10px]">
                  {formatTimestamp(c.updatedAt)}
                </div>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function formatTimestamp(iso: string): string {
  try {
    const d = new Date(iso);
    return d.toLocaleString(undefined, {
      month: 'short',
      day: 'numeric',
      hour: 'numeric',
      minute: '2-digit',
    });
  } catch {
    return iso;
  }
}
