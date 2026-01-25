import { useEffect } from 'react';
import { useSnapshot } from 'valtio';
import { Moon, Sun, Monitor, AppWindow } from 'lucide-react';
import { refreshHealth } from './state/health';
import {
  settingsState,
  refreshSettings,
  setChromeStyle,
  type ThemePreference,
  type ChromeStyle,
} from './state/settings';
import { themeState, setTheme } from './state/theme';
import { TitleBar } from '@/components/titlebar/TitleBar';
import { ResizeFrame } from '@/components/window/ResizeFrame';
import { HomePage } from '@/components/home/HomePage';
import { Button } from '@/components/ui/button';

const themeCycle: ThemePreference[] = ['system', 'light', 'dark'];
const chromeCycle: ChromeStyle[] = ['auto', 'windows', 'macos', 'linux'];

export default function App() {
  const { theme: themePref, chromeStyle } = useSnapshot(settingsState);
  const { resolved } = useSnapshot(themeState);

  useEffect(() => {
    refreshHealth();
    refreshSettings();
  }, []);

  const ThemeIcon = themePref === 'light' ? Sun : themePref === 'dark' ? Moon : Monitor;
  const nextTheme = themeCycle[(themeCycle.indexOf(themePref) + 1) % themeCycle.length];
  const nextChrome = chromeCycle[(chromeCycle.indexOf(chromeStyle) + 1) % chromeCycle.length];

  return (
    <div className="bg-background text-foreground flex h-screen flex-col">
      <TitleBar />
      <ResizeFrame />
      <main className="flex flex-1 flex-col">
        <HomePage />
      </main>
      {/* Temporary dev controls — replaced by the real settings UI later. */}
      <div className="fixed right-3 bottom-3 z-40 flex gap-1.5">
        <Button variant="ghost" size="sm" onClick={() => setTheme(nextTheme)}>
          <ThemeIcon />
          {themePref} ({resolved})
        </Button>
        <Button variant="ghost" size="sm" onClick={() => setChromeStyle(nextChrome)}>
          <AppWindow />
          {chromeStyle}
        </Button>
      </div>
    </div>
  );
}
