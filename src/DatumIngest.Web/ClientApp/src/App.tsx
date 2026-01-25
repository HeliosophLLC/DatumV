import { useEffect } from 'react';
import { useSnapshot } from 'valtio';
import { Moon, Sun, Monitor, AppWindow } from 'lucide-react';
import { healthState, refreshHealth } from './state/health';
import {
  settingsState,
  refreshSettings,
  setChromeStyle,
  type ThemePreference,
  type ChromeStyle,
} from './state/settings';
import { themeState, setTheme } from './state/theme';
import { hostState } from './state/host';
import { TitleBar } from '@/components/titlebar/TitleBar';
import { Button } from '@/components/ui/button';

const themeCycle: ThemePreference[] = ['system', 'light', 'dark'];
const chromeCycle: ChromeStyle[] = ['auto', 'windows', 'macos', 'linux'];

export default function App() {
  const { data } = useSnapshot(healthState);
  const { theme: themePref, chromeStyle } = useSnapshot(settingsState);
  const { resolved } = useSnapshot(themeState);
  const { os } = useSnapshot(hostState);

  useEffect(() => {
    refreshHealth();
    refreshSettings();
  }, []);

  const ThemeIcon = themePref === 'light' ? Sun : themePref === 'dark' ? Moon : Monitor;
  const nextTheme = themeCycle[(themeCycle.indexOf(themePref) + 1) % themeCycle.length];
  const nextChrome = chromeCycle[(chromeCycle.indexOf(chromeStyle) + 1) % chromeCycle.length];

  return (
    <div className="flex h-screen flex-col bg-background text-foreground">
      <TitleBar />
      <main className="flex flex-1 flex-col items-center justify-center gap-6">
        <div className="flex flex-col items-center gap-2">
          <h1 className="text-3xl font-semibold">
            DatumIngest {data ? `(${data.status} ${data.version})` : '(loading…)'}
          </h1>
          {data && (
            <p className="text-muted-foreground text-sm">
              {data.displayName} ({data.userId}) · node:{data.nodeId} · {data.catalogPath}
            </p>
          )}
        </div>
        <div className="flex gap-2">
          <Button variant="outline" onClick={() => setTheme(nextTheme)}>
            <ThemeIcon />
            {themePref} ({resolved})
          </Button>
          <Button variant="outline" onClick={() => setChromeStyle(nextChrome)}>
            <AppWindow />
            chrome: {chromeStyle} (os: {os})
          </Button>
        </div>
      </main>
    </div>
  );
}
