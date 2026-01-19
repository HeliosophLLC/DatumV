// Right-docked AI assistant panel. Mounted once into
// #assistant-drawer; visibility is gated by assistantState.open.
//
// Per-turn flow (all server-side now):
//   1. User types into the textarea (and/or attaches an image).
//   2. Send button hits POST /api/assistant/conversations/:id/turn
//      with the text + optional file as multipart/form-data.
//   3. Server INSERTs the upload (if any), INSERTs the user message,
//      streams chunks, INSERTs the assistant response. Each step
//      lands as a TurnEvent on the NDJSON wire; we update the
//      local mirror per event.
//
// The assistant content renders as Markdown via marked. While
// streaming we render plain text (re-parsing markdown per token
// tanks frame-rate); the final assistant_message_inserted event
// replaces the streaming bubble with the persisted MessageDto.

import { useEffect, useRef, useState } from 'react';
import { useSnapshot } from 'valtio';
import { marked } from 'marked';
import {
  assistantState,
  setMessages,
  setStreamingAbortController,
} from './assistant-state.js';
import {
  ensureConversation,
  fetchHistory,
  streamTurn,
  type MessageDto,
} from './assistant-api.js';

marked.setOptions({ gfm: true, breaks: true });

export function AssistantPanel() {
  const snap = useSnapshot(assistantState);
  if (!snap.open) return null;

  return (
    <div className="assistant-panel">
      <Header />
      <ConversationLog />
      <Composer />
    </div>
  );
}

// ===== Header =====

function Header() {
  return (
    <div className="assistant-header">
      <span className="assistant-title">Assistant</span>
      <button
        type="button"
        className="assistant-close"
        title="Close"
        onClick={() => {
          assistantState.open = false;
        }}
      >
        ×
      </button>
    </div>
  );
}

// ===== Log =====

function ConversationLog() {
  const snap = useSnapshot(assistantState);
  const scrollRef = useRef<HTMLDivElement>(null);

  // Auto-scroll: keep the tail in view when the user is already
  // near the bottom; don't yank them down if they've scrolled up.
  const lastTail = useRef({ count: 0, len: 0 });
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const grew =
      snap.messages.length !== lastTail.current.count ||
      snap.streamingText.length !== lastTail.current.len;
    if (!grew) return;
    const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
    if (distanceFromBottom < 80) {
      el.scrollTop = el.scrollHeight;
    }
    lastTail.current = {
      count: snap.messages.length,
      len: snap.streamingText.length,
    };
  });

  // Hydrate on first open. ensureConversation returns the most-
  // recent conversation for the workspace (or creates one); the
  // server is idempotent so re-opening the panel is cheap.
  useEffect(() => {
    if (!snap.open) return;
    let cancelled = false;
    (async () => {
      assistantState.loading = true;
      try {
        const conv = await ensureConversation('default');
        if (cancelled) return;
        assistantState.conversation = conv;
        const msgs = await fetchHistory(conv.id);
        if (cancelled) return;
        setMessages(msgs);
      } catch (err) {
        if (cancelled) return;
        const e = err as { message?: string };
        assistantState.error = e.message ?? String(err);
      } finally {
        if (!cancelled) assistantState.loading = false;
      }
    })();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [snap.open]);

  if (snap.loading && snap.messages.length === 0) {
    return (
      <div className="assistant-log" ref={scrollRef}>
        <div className="assistant-empty">Loading…</div>
      </div>
    );
  }

  if (snap.messages.length === 0 && !snap.streaming) {
    return (
      <div className="assistant-log" ref={scrollRef}>
        <div className="assistant-empty">
          No messages yet. Type below to start a conversation.
        </div>
        {snap.error && (
          <div className="assistant-error">⚠ {snap.error}</div>
        )}
      </div>
    );
  }

  return (
    <div className="assistant-log" ref={scrollRef}>
      {snap.messages.map((m) => (
        <Bubble key={m.id} message={m} />
      ))}
      {snap.streaming && (
        <StreamingBubble text={snap.streamingText} />
      )}
      {snap.error && (
        <div className="assistant-error">⚠ {snap.error}</div>
      )}
    </div>
  );
}

function Bubble({ message }: { message: MessageDto }) {
  const isUser = message.role === 'user';
  const isAssistant = message.role === 'assistant';
  const className = isUser
    ? 'assistant-bubble user'
    : isAssistant
      ? 'assistant-bubble assistant'
      : 'assistant-bubble system';

  if (isAssistant) {
    const html = marked.parse(message.content) as string;
    return (
      <div className={className}>
        <div
          className="assistant-bubble-md"
          dangerouslySetInnerHTML={{ __html: html }}
        />
      </div>
    );
  }

  return (
    <div className={className}>
      <div className="assistant-bubble-text">{message.content}</div>
      {message.uploadId !== null && (
        <div className="assistant-bubble-attachment">
          📎 upload #{message.uploadId}
        </div>
      )}
    </div>
  );
}

function StreamingBubble({ text }: { text: string }) {
  return (
    <div className="assistant-bubble assistant streaming">
      <div className="assistant-bubble-text">
        {text || '…'}
        <span className="assistant-cursor">▍</span>
      </div>
    </div>
  );
}

// ===== Composer =====

function Composer() {
  const snap = useSnapshot(assistantState);
  const [pendingFile, setPendingFile] = useState<File | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  const onSend = async () => {
    const text = assistantState.inputDraft.trim();
    if (!text && !pendingFile) return;
    if (snap.streaming) return;
    const conv = assistantState.conversation;
    if (!conv) {
      assistantState.error = 'No active conversation.';
      return;
    }

    assistantState.error = null;
    assistantState.inputDraft = '';
    const fileSnapshot = pendingFile;
    setPendingFile(null);

    const ac = new AbortController();
    setStreamingAbortController(ac);
    assistantState.streaming = true;
    assistantState.streamingText = '';

    try {
      await streamTurn(
        conv.id,
        text,
        fileSnapshot,
        assistantState.modelName,
        (ev) => {
          switch (ev.type) {
            case 'user_message_inserted':
              // Append the persisted user bubble immediately so the
              // user sees their message land before the model
              // starts streaming.
              assistantState.messages = [...assistantState.messages, ev.message];
              break;
            case 'chunk':
              assistantState.streamingText += ev.text;
              break;
            case 'assistant_message_inserted':
              assistantState.messages = [...assistantState.messages, ev.message];
              assistantState.streamingText = '';
              break;
            case 'complete':
              // No state change — `complete` is a marker; finally
              // block clears the streaming flag.
              break;
            case 'error':
              assistantState.error = ev.message;
              break;
          }
        },
        ac.signal,
      );
    } catch (err) {
      const e = err as { name?: string; message?: string };
      if (e.name !== 'AbortError') {
        assistantState.error = e.message ?? String(err);
      }
    } finally {
      assistantState.streaming = false;
      assistantState.streamingText = '';
      setStreamingAbortController(null);
    }
  };

  const onCancel = () => {
    assistantState.abortController?.abort();
  };

  return (
    <div className="assistant-composer">
      {pendingFile && (
        <div className="assistant-pending-file">
          <span>📎 {pendingFile.name}</span>
          <button
            type="button"
            className="assistant-pending-file-remove"
            onClick={() => setPendingFile(null)}
            title="Remove attachment"
          >
            ×
          </button>
        </div>
      )}
      <textarea
        ref={textareaRef}
        className="assistant-input"
        placeholder="Ask the model… (Enter to send, Shift+Enter for newline)"
        value={snap.inputDraft}
        rows={3}
        disabled={snap.streaming}
        onChange={(e) => {
          assistantState.inputDraft = e.target.value;
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            void onSend();
          }
        }}
      />
      <div className="assistant-composer-row">
        <input
          ref={fileInputRef}
          type="file"
          accept="image/*"
          style={{ display: 'none' }}
          onChange={(e) => {
            const file = e.target.files?.[0] ?? null;
            setPendingFile(file);
            e.target.value = '';
          }}
        />
        <button
          type="button"
          className="assistant-attach-btn"
          title="Attach image"
          onClick={() => fileInputRef.current?.click()}
          disabled={snap.streaming}
        >
          📎
        </button>
        <span className="assistant-model-name" title="Model in use">
          {snap.modelName}
        </span>
        <span className="assistant-spacer" />
        {snap.streaming ? (
          <button
            type="button"
            className="assistant-cancel-btn"
            onClick={onCancel}
          >
            Stop
          </button>
        ) : (
          <button
            type="button"
            className="assistant-send-btn"
            onClick={() => void onSend()}
            disabled={!snap.inputDraft.trim() && !pendingFile}
          >
            Send
          </button>
        )}
      </div>
    </div>
  );
}
