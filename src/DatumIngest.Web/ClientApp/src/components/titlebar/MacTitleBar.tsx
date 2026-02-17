import { useTranslation } from 'react-i18next';
import { minimize, toggleMaximize, close } from '@/state/window';

// macOS-flavored: 28px tall, three circular "traffic lights" left
// (close/minimize/zoom), title centered to the full bar (not the remaining
// space after traffic lights — matches real macOS). Circles are the one
// place we break the rounded-xs rule; at 12px the *shape* is what people
// identify as Mac.
//
// Drag is CSS-only: the header carries `app-drag` (-webkit-app-region:
// drag) which Chromium honors at the compositor layer.
export function MacTitleBar({ dialog = false }: { dialog?: boolean } = {}) {
  const { t } = useTranslation();
  return (
    <header className="app-drag relative flex h-7 items-center border-b bg-background px-3 select-none">
      <div className="app-no-drag relative z-10 flex items-center gap-2">
        <button
          type="button"
          onClick={close}
          aria-label={t('window.close')}
          className="size-3 rounded-full bg-[#ff5f57] hover:brightness-90"
        />
        {!dialog && (
          <>
            <button
              type="button"
              onClick={minimize}
              aria-label={t('window.minimize')}
              className="size-3 rounded-full bg-[#febc2e] hover:brightness-90"
            />
            <button
              type="button"
              onClick={toggleMaximize}
              aria-label={t('window.zoom')}
              className="size-3 rounded-full bg-[#28c840] hover:brightness-90"
            />
          </>
        )}
      </div>
      <div className="pointer-events-none absolute inset-0 flex items-center justify-center text-xs text-muted-foreground">
        {t('app.name')}
      </div>
    </header>
  );
}
