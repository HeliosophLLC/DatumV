import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { DialogShell } from './components/dialogs/DialogShell';
import './index.css';
// Side-effect imports: i18next must initialise before any component calls
// useTranslation; state/locale wires settingsState → i18next.changeLanguage.
import './i18n';
import './state/locale';
// Theme is a side-effect module: it subscribes to settingsState and toggles
// the <html>.dark class. Must be imported in both roots so dialog windows
// pick up the resolved theme the same way the main window does.
import './state/theme';
// Loads the dialog-message subscriber so openDialog promises resolve.
// Side-effect only; safe to import in both roots.
import './state/dialogs';
// Publishes the application menu (native + in-titlebar) and subscribes
// to native-menu click delivery. Invoked only for the SPA root — the
// loader page (splash/welcome) skips it so the welcome screen doesn't
// show "New Query / Close Tab" in a menu that can't service them, and
// dialog windows skip it because the menu is set globally at the
// Electron level and the SPA root already installed it.
import { initMenu } from './state/menu';
// Publishes translated strings the Electron main process renders
// (folder-picker titles, splash status during catalog swap). Same
// publish-on-init + republish-on-locale-change shape as state/menu.
import './state/hostStrings';
// Wires Monaco's worker + loader to the bundled instance. Idempotent;
// safe to import in both roots (dialog windows never mount Monaco
// today, but the cost of the early init is a single function call).
import { initMonaco } from './monaco/setup';

initMonaco();

// Dual-root mount: the same SPA bundle serves both the main app and
// dialog windows. The coordinator (server-side) loads dialog windows at
// URLs whose hash starts with '#/dialog/'; we branch on that here to
// pick which root to mount. Plain string check — no router needed.
const isDialogWindow = window.location.hash.startsWith('#/dialog/');

if (!isDialogWindow) initMenu();

createRoot(document.getElementById('root')!).render(
  <StrictMode>{isDialogWindow ? <DialogShell /> : <App />}</StrictMode>,
);
