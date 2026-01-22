import { useEffect } from 'react';
import { useSnapshot } from 'valtio';
import { Moon, Sun, Monitor } from 'lucide-react';
import { healthState, refreshHealth } from './state/health';
import { themeState, setTheme, type ThemePreference } from './state/theme';
import { Button } from '@/components/ui/button';

const cycleOrder: ThemePreference[] = ['system', 'light', 'dark'];

export default function App() {
  const { data } = useSnapshot(healthState);
  const { preference, resolved } = useSnapshot(themeState);

  useEffect(() => {
    refreshHealth();
  }, []);

  const ThemeIcon = preference === 'light' ? Sun : preference === 'dark' ? Moon : Monitor;
  const nextPreference = cycleOrder[(cycleOrder.indexOf(preference) + 1) % cycleOrder.length];

  return (
    <main className="flex min-h-screen flex-col items-center justify-center gap-6 bg-background text-foreground">
      <h1 className="text-3xl font-semibold">
        DatumIngest {data ? `(${data.status} ${data.version})` : '(loading…)'}
      </h1>
      <Button variant="outline" onClick={() => setTheme(nextPreference)}>
        <ThemeIcon />
        {preference} ({resolved})
      </Button>
    </main>
  );
}
