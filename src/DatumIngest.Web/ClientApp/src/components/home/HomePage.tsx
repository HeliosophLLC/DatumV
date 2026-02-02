import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import { healthState } from '@/state/health';
import { sendMessage } from '@/state/conversation';
import { ChatInput } from '@/components/chat/ChatInput';

// Claude/ChatGPT-style landing: greeting + centered prompt box. Submitting
// pushes the user message into conversationState and the App swaps to
// ConversationView, where the same input lives anchored at the bottom.

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

  const name = data?.displayName?.trim();
  const tod = timeOfDay(new Date());
  const greeting = name
    ? t('greeting', { tod, name })
    : t('greetingGuest', { tod });

  return (
    <div className="flex flex-1 flex-col items-center justify-center px-6">
      <div className="flex w-full max-w-2xl flex-col gap-6">
        <div className="text-center">
          <h1 className="text-2xl font-medium">{greeting}</h1>
          <p className="text-muted-foreground mt-2 text-sm">{t('tagline')}</p>
        </div>
        <ChatInput onSubmit={sendMessage} />
        <p className="text-muted-foreground text-center text-xs">{t('hint')}</p>
      </div>
    </div>
  );
}
