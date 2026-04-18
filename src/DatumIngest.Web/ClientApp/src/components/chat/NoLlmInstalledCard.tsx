import { useTranslation } from 'react-i18next';
import { CircleAlert } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { MODELS_TAB_ID, selectTab } from '@/state/tabs';

// Rendered in the chat surface when GET /api/llm/available comes back
// empty. Mirrors the Settings page's disabled-picker CTA: the user can
// jump straight to the Models tab to install one. Once an install
// completes, llmState refreshes from the hub event and this card
// disappears.
export function NoLlmInstalledCard() {
  const { t } = useTranslation('chat');
  return (
    <div className="flex h-full flex-col items-center justify-center px-6">
      <div className="flex max-w-md flex-col items-center gap-4 text-center">
        <CircleAlert className="text-muted-foreground size-8" />
        <h2 className="text-lg font-medium">{t('noLlmTitle')}</h2>
        <p className="text-muted-foreground text-sm">{t('noLlmDescription')}</p>
        <Button type="button" onClick={() => selectTab(MODELS_TAB_ID)}>
          {t('noLlmOpenModels')}
        </Button>
      </div>
    </div>
  );
}
