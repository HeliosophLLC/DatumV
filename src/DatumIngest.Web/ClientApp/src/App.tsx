import { useEffect } from 'react';
import { useSnapshot } from 'valtio';
import { Moon, Sun, Monitor } from 'lucide-react';
import { healthState, refreshHealth } from './state/health';
import { settingsState, refreshSettings, type ThemePreference } from './state/settings';
import { themeState, setTheme } from './state/theme';
import { Button } from '@/components/ui/button';

const cycleOrder: ThemePreference[] = ['system', 'light', 'dark'];

export default function App() {
  const { data } = useSnapshot(healthState);
  const { theme: preference } = useSnapshot(settingsState);
  const { resolved } = useSnapshot(themeState);

  useEffect(() => {
    refreshHealth();
    refreshSettings();
  }, []);

  const ThemeIcon = preference === 'light' ? Sun : preference === 'dark' ? Moon : Monitor;
  const next = cycleOrder[(cycleOrder.indexOf(preference) + 1) % cycleOrder.length];

  return (
    <main className="flex min-h-screen flex-col items-center justify-center gap-6 bg-background text-foreground">
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
      <Button variant="outline" onClick={() => setTheme(next)}>
        <ThemeIcon />
        {preference} ({resolved})
      </Button>
    </main>
  );
}
