import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { Loader2 } from 'lucide-react';
import { cancelMessage, conversationState, sendMessage } from '@/state/conversation';
import { ChatInput } from './ChatInput';
import { cn } from '@/lib/utils';

// Claude/ChatGPT-style stream view. Scrolls to the bottom on every render
// while the assistant is generating; manual scroll back up to read earlier
// turns is supported (we only auto-scroll while at the bottom).
export function ConversationView() {
  const { t } = useTranslation('chat');
  const { messages, streaming, status, error } = useSnapshot(conversationState);
  const scrollRef = useRef<HTMLDivElement>(null);
  const stickyRef = useRef<boolean>(true);

  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    // Re-snap to bottom if the user hasn't scrolled away.
    if (stickyRef.current) {
      el.scrollTop = el.scrollHeight;
    }
  }, [messages.length, streaming, status]);

  function onScroll(event: React.UIEvent<HTMLDivElement>) {
    const el = event.currentTarget;
    const distanceFromBottom = el.scrollHeight - (el.scrollTop + el.clientHeight);
    stickyRef.current = distanceFromBottom < 32;
  }

  const showStreamingBubble = streaming.length > 0 || status === 'awaiting';
  // The input stays *enabled* during streaming so the user can keep typing
  // their next message; only the send button swaps for a stop button. From
  // 'error' the input is fully enabled so the user can retry.
  const isStreaming = status === 'awaiting' || status === 'streaming';

  return (
    <div className="flex h-full flex-col">
      <div ref={scrollRef} onScroll={onScroll} className="flex-1 overflow-y-auto px-6">
        <div className="mx-auto flex w-full max-w-2xl flex-col gap-4 py-6">
          {messages.map((msg) =>
            msg.kind === 'checkpoint' ? (
              <CheckpointDivider key={msg.id} content={msg.content} />
            ) : (
              <MessageBubble
                key={msg.id}
                id={msg.id}
                role={msg.role as 'user' | 'assistant'}
                content={msg.content}
              />
            ),
          )}
          {showStreamingBubble && (
            <MessageBubble
              id="__streaming__"
              role="assistant"
              content={streaming.length > 0 ? streaming : t('thinking')}
              pending={streaming.length === 0}
            />
          )}
          {status === 'error' && error && (
            <p className="text-destructive text-sm" role="alert">
              {t('errorPrefix', { message: error })}
            </p>
          )}
        </div>
      </div>
      <div className="border-t px-6 py-4">
        <div className="mx-auto w-full max-w-2xl">
          <ChatInput
            onSubmit={sendMessage}
            onCancel={cancelMessage}
            isStreaming={isStreaming}
          />
        </div>
      </div>
    </div>
  );
}

// Renders a compacted-here divider with the summary text underneath. The
// LLM sees this summary as a synthetic system message; the divider is
// purely a UX affordance so the user can tell where the in-context tail
// starts.
function CheckpointDivider({ content }: { content: string }) {
  const { t } = useTranslation('chat');
  return (
    <div className="flex flex-col gap-2 py-2">
      <div className="text-muted-foreground flex items-center gap-3 text-xs uppercase tracking-wide">
        <span className="bg-border h-px flex-1" />
        <span>{t('checkpointLabel')}</span>
        <span className="bg-border h-px flex-1" />
      </div>
      <div className="text-muted-foreground text-xs whitespace-pre-wrap px-1">
        {content}
      </div>
    </div>
  );
}

// Per-render-pass set of message ids that have already played their type-out
// animation. Module-scope so a re-render of the parent doesn't replay them,
// while still resetting cleanly on a hard reload. Kept out of conversationState
// because it's purely a render concern — the persisted graph doesn't care
// whether a message was typed or pasted in.
const typedOutIds = new Set<string>();

function MessageBubble({
  id,
  role,
  content,
  pending = false,
}: {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  pending?: boolean;
}) {
  const isUser = role === 'user';
  return (
    <div className={cn('flex', isUser ? 'justify-end' : 'justify-start')}>
      <div
        className={cn(
          'max-w-[80%] rounded-xs px-3 py-2 text-sm whitespace-pre-wrap',
          isUser ? 'bg-primary text-primary-foreground' : 'bg-muted text-foreground',
          pending && 'text-muted-foreground',
        )}
      >
        {pending ? (
          <span className="inline-flex items-center gap-2">
            <Loader2 className="size-3.5 animate-spin" />
            {content}
          </span>
        ) : isUser ? (
          <TypedOut id={id} text={content} />
        ) : (
          content
        )}
      </div>
    </div>
  );
}

// Reveals `text` character-by-character without changing the bubble's layout —
// the full text is rendered as an invisible span so the height/width are
// committed up front and nothing reflows mid-animation. The visible span
// sits over it, sliced as the animation progresses.
//
// First mount of an id animates; subsequent mounts (parent re-renders) render
// the full text immediately via the typedOutIds cache.
function TypedOut({ id, text }: { id: string; text: string }) {
  // 4 chars per tick * 12ms ≈ ~330 char/s. A 200-char message types in ~600ms.
  const CHARS_PER_TICK = 4;
  const TICK_MS = 12;

  const [shown, setShown] = useState(() => (typedOutIds.has(id) ? text.length : 0));

  useEffect(() => {
    if (shown >= text.length) {
      typedOutIds.add(id);
      return;
    }
    const handle = window.setTimeout(() => {
      setShown((prev) => Math.min(prev + CHARS_PER_TICK, text.length));
    }, TICK_MS);
    return () => window.clearTimeout(handle);
  }, [id, shown, text.length]);

  if (shown >= text.length) {
    return <>{text}</>;
  }
  return (
    <span className="relative inline-block">
      {/* Reserve layout. aria-hidden so screen readers see only the final string via the live span below. */}
      <span aria-hidden className="invisible whitespace-pre-wrap">{text}</span>
      <span className="absolute inset-0 whitespace-pre-wrap">{text.slice(0, shown)}</span>
    </span>
  );
}
