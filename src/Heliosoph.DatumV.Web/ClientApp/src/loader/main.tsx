import { createRoot } from 'react-dom/client';
import { LoaderApp, type LoaderMode } from './LoaderApp';
// Import the SPA's main stylesheet so the loader picks up the same
// theme tokens (shadcn variables, geist font, dark-variant). Tailwind
// v4 scans content per-entry, so the loader only ships classes it
// actually uses.
import '../index.css';
// i18next + locale wiring so the WindowChrome titlebar resolves
// `app.name`, `window.close`, etc. instead of rendering raw keys.
// state/locale subscribes to settingsState (which has sensible
// defaults — no backend fetch on import) and pushes the resolved
// language into i18next. The SPA's full menu setup is NOT imported
// here: state/menu.ts is no longer a side-effect module, so the
// loader doesn't install a menu it can't service.
import '../i18n';
import '../state/locale';

// Splash vs welcome is selected by main.ts via a `?mode=…` query
// param when it navigates this window. Default to splash — the most
// common state — so a typo or missing param lands somewhere sane.
function parseMode(): LoaderMode {
  const param = new URLSearchParams(window.location.search).get('mode');
  return param === 'welcome' ? 'welcome' : 'splash';
}

// The SPA's full theme system (state/theme.ts) lives at a different
// origin from this loader (file:// in prod, http://localhost:5173 in
// dev) so localStorage handoff isn't available. Main forwards the
// last-resolved theme as a `?theme=` URL param (persisted across
// launches in a small file under globalDataPath); we fall back to
// the OS preference only on a truly cold first launch.
function applyInitialTheme(): void {
  const param = new URLSearchParams(window.location.search).get('theme');
  const resolved =
    param === 'light' || param === 'dark'
      ? param
      : window.matchMedia('(prefers-color-scheme: dark)').matches
        ? 'dark'
        : 'light';
  document.documentElement.classList.toggle('dark', resolved === 'dark');
}

applyInitialTheme();

createRoot(document.getElementById('root')!).render(<LoaderApp mode={parseMode()} />);
