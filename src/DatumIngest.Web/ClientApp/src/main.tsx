import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import './index.css';
// Side-effect imports: i18next must initialise before any component calls
// useTranslation; state/locale wires settingsState → i18next.changeLanguage.
import './i18n';
import './state/locale';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
