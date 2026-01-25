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

  // The browser owns its own chrome (tab + URL bar + controls); rendering
  // ours on top would be redundant and visually awful. Photino is where
  // our custom chrome belongs.
  if (runtime !== 'photino') return null;

  const resolved = resolve(chromeStyle, os);
  if (resolved === 'macos') return <MacTitleBar />;
  if (resolved === 'linux') return <LinuxTitleBar />;
  return <WindowsTitleBar />;
}
