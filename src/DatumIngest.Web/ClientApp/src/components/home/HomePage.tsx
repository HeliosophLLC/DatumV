import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { ArrowUp } from 'lucide-react';
import { healthState } from '@/state/health';
import { Button } from '@/components/ui/button';

// Claude/ChatGPT-style landing: greeting + centered prompt box. Submit is
// a no-op for now — the wiring goes:
//   prompt → LLM → SQL → run against catalog → open in a panel
// Lands incrementally as catalog init, chat tables, and the inference
// orchestration story come online (see project_generative_ui_vision +
// project_inference_integration_approach memories).

type TimeOfDay = 'morning' | 'afternoon' | 'evening';

function timeOfDay(date: Date): TimeOfDay {
  const hour = date.getHours();
  if (hour < 12) return 'morning';
  if (hour < 18) return 'afternoon';
  return 'evening';
}

export function HomePage() {
  const { t } = useTranslation('home');
  const { data } = useSnapshot(healthState);
  const [value, setValue] = useState('');

  const name = data?.displayName?.trim();
  const tod = timeOfDay(new Date());
  const greeting = name
    ? t('greeting', { tod, name })
    : t('greetingGuest', { tod });

  function handleSubmit() {
    const trimmed = value.trim();
    if (!trimmed) return;
    // TODO: route to LLM → SQL → panel.
    console.log('[home] submit', trimmed);
    setValue('');
  }

  function onKeyDown(event: React.KeyboardEvent<HTMLTextAreaElement>) {
    // Enter submits; Shift+Enter inserts a newline. IME composition is
    // honored so CJK candidate-selection keystrokes don't submit prematurely.
    if (event.key !== 'Enter' || event.shiftKey || event.nativeEvent.isComposing) return;
    event.preventDefault();
    handleSubmit();
  }

  return (
    <div className="flex flex-1 flex-col items-center justify-center px-6">
      <div className="flex w-full max-w-2xl flex-col gap-6">
        <div className="text-center">
          <h1 className="text-2xl font-medium">{greeting}</h1>
          <p className="text-muted-foreground mt-2 text-sm">{t('tagline')}</p>
        </div>
        <div className="relative">
          <textarea
            value={value}
            onChange={(e) => setValue(e.target.value)}
            onKeyDown={onKeyDown}
            rows={3}
            placeholder={t('placeholder')}
            aria-label={t('placeholder')}
            className="bg-background focus-visible:ring-ring w-full resize-none rounded-xs border px-4 py-3 pr-12 text-sm shadow-sm focus-visible:ring-2 focus-visible:outline-none"
          />
          <Button
            type="button"
            size="icon-sm"
            onClick={handleSubmit}
            disabled={!value.trim()}
            aria-label={t('submit')}
            className="absolute right-2 bottom-2"
          >
            <ArrowUp />
          </Button>
        </div>
        <p className="text-muted-foreground text-center text-xs">{t('hint')}</p>
      </div>
    </div>
  );
}
