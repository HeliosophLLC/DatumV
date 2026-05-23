import { useEffect } from 'react';
import { useSnapshot } from 'valtio';
import { settingsState } from '@/state/settings';
import { hostState, type HostOs } from '@/state/host';
import type { ChromeStyle } from '@/api/generated/openapi-client';
import type { WindowChromeKind } from '@/components/window/WindowChrome';
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

// Title-bar pixel heights per platform variant. Must stay in sync with
// the `h-*` class on each variant's <header>. Surfaced via the
// `--app-titlebar-h` CSS variable so portal-rendered modals can leave
// the drag region uncovered without re-detecting host OS themselves.
const TITLEBAR_HEIGHT_PX: Record<ResolvedChrome, number> = {
  windows: 32,
  macos: 36,
  linux: 36,
};

export function TitleBar({
  kind = 'main',
  title,
}: {
  kind?: WindowChromeKind;
  title?: string;
} = {}) {
  const { chromeStyle } = useSnapshot(settingsState);
  const { os } = useSnapshot(hostState);

  // Electron is chromeless on every platform (main.ts sets `frame: false`
  // uniformly), so we always render a custom titlebar. The chromeStyle
  // setting picks which platform-flavored bar to draw — defaults to the
  // host OS via `resolve`, but the user can override (e.g. preview the
  // Mac look on a Windows host) without affecting drag/resize, which is
  // handled by CSS and Chromium.
  const resolved = resolve(chromeStyle, os);

  useEffect(() => {
    const px = TITLEBAR_HEIGHT_PX[resolved];
    document.documentElement.style.setProperty('--app-titlebar-h', `${px}px`);
  }, [resolved]);

  if (resolved === 'macos') return <MacTitleBar kind={kind} title={title} />;
  if (resolved === 'linux') return <LinuxTitleBar kind={kind} title={title} />;
  return <WindowsTitleBar kind={kind} title={title} />;
}
