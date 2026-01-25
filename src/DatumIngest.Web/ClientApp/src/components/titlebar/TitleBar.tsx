import { useSnapshot } from 'valtio';
import { settingsState } from '@/state/settings';
import { hostState, type HostOs } from '@/state/host';
import type { ChromeStyle } from '@/api/generated/openapi-client';
import { WindowsTitleBar } from './WindowsTitleBar';
import { MacTitleBar } from './MacTitleBar';
import { LinuxTitleBar } from './LinuxTitleBar';

type ResolvedChrome = Exclude<ChromeStyle, 'auto'>;

function resolve(chromeStyle: ChromeStyle | undefined, os: HostOs): ResolvedChrome {
  if (chromeStyle && chromeStyle !== 'auto') return chromeStyle;
  if (os === 'macos') return 'macos';
  if (os === 'linux') return 'linux';
  return 'windows'; // also catches 'unknown'
}

export function TitleBar() {
  const { chromeStyle } = useSnapshot(settingsState);
  const { os, runtime } = useSnapshot(hostState);

  // Browser owns its own chrome. Mac/Linux use OS chrome until those
  // platforms get the same polish (native drag/resize integration);
  // chromeless is Windows-only today. The component code for all three
  // styles stays in the repo so cycling on a Windows host previews how
  // Mac/Linux *will* look once we wire them up.
  if (runtime !== 'photino' || os !== 'windows') return null;

  const resolved = resolve(chromeStyle, os);
  if (resolved === 'macos') return <MacTitleBar />;
  if (resolved === 'linux') return <LinuxTitleBar />;
  return <WindowsTitleBar />;
}
