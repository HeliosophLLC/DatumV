import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ArrowUp, Square } from 'lucide-react';
import { Button } from '@/components/ui/button';

// Shared composer for both the centered HomePage layout and the
// bottom-anchored ConversationView layout. Caller controls width / margins
// via the wrapping element; this component owns the textarea + send/stop
// affordances and the Enter / Shift+Enter behaviour.
//
// When `isStreaming` is true the send button swaps for a stop button that
// calls `onCancel`. The textarea stays interactive so the user can start
// typing their next message while the cancel propagates — `onSubmit` is
// only invoked once the next idle state is observed (caller's job).
export interface ChatInputProps {
  onSubmit: (content: string) => void | Promise<void>;
  onCancel?: () => void | Promise<void>;
  disabled?: boolean;
  isStreaming?: boolean;
}

export function ChatInput({ onSubmit, onCancel, disabled, isStreaming }: ChatInputProps) {
  const { t } = useTranslation('chat');
  const [value, setValue] = useState('');

  async function handleSubmit() {
    const trimmed = value.trim();
    if (!trimmed || disabled || isStreaming) return;
    setValue('');
    await onSubmit(trimmed);
  }

  function onKeyDown(event: React.KeyboardEvent<HTMLTextAreaElement>) {
    // Enter submits; Shift+Enter inserts a newline. IME composition is
    // honored so CJK candidate-selection doesn't submit prematurely.
    if (event.key !== 'Enter' || event.shiftKey || event.nativeEvent.isComposing) return;
    if (isStreaming) return;
    event.preventDefault();
    void handleSubmit();
  }

  return (
    <div className="relative">
      <textarea
        value={value}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={onKeyDown}
        rows={3}
        disabled={disabled}
        placeholder={t('placeholder')}
        aria-label={t('placeholder')}
        className="bg-background focus-visible:ring-ring w-full resize-none rounded-xs border px-4 py-3 pr-12 text-sm shadow-sm focus-visible:ring-2 focus-visible:outline-none disabled:opacity-60"
      />
      {isStreaming ? (
        <Button
          type="button"
          size="icon-sm"
          variant="destructive"
          onClick={() => void onCancel?.()}
          aria-label={t('stop')}
          className="absolute right-2 bottom-2"
        >
          <Square />
        </Button>
      ) : (
        <Button
          type="button"
          size="icon-sm"
          onClick={() => void handleSubmit()}
          disabled={!value.trim() || disabled}
          aria-label={t('submit')}
          className="absolute right-2 bottom-2"
        >
          <ArrowUp />
        </Button>
      )}
    </div>
  );
}
