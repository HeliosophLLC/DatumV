import { useTranslation } from 'react-i18next';

export function QueryEditorView() {
  const { t } = useTranslation('query');
  return (
    <div className="flex h-full flex-col overflow-hidden">
      <header className="flex items-center border-b px-6 py-4">
        <h1 className="text-lg font-medium">{t('title')}</h1>
      </header>
      <div className="text-muted-foreground flex flex-1 items-center justify-center text-sm">
        {t('placeholder')}
      </div>
    </div>
  );
}
