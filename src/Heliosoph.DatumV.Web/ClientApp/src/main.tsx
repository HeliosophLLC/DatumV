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

createRoot(document.getElementById('root')!).render(
  <StrictMode>{isDialogWindow ? <DialogShell /> : <App />}</StrictMode>,
);
