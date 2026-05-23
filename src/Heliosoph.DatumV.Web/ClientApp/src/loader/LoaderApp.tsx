import { WindowChrome } from '@/components/window/WindowChrome';
import { Splash } from './Splash';
import { Welcome } from './Welcome';
// Atmospheric backdrop behind splash / welcome content. Vite hashes
// and emits this under wwwroot/assets/; `base: './'` in vite.config
// keeps the resolved URL relative so file:// load in prod resolves
// to <appPath>/wwwroot/assets/heliosoph-<hash>.png.
import heliosophBg from './heliosoph.png';

export type LoaderMode = 'splash' | 'welcome';

interface Props {
  mode: LoaderMode;
}

// Single React root, two screens. The Electron main process owns
// which mode renders by setting `?mode=…` on the URL when it
// navigates this window. We never transition between modes within a
// single page life — main.ts re-loads the window when it wants the
// other screen, which keeps state simple (no router) and matches the
// existing splash → SPA / welcome → SPA flow.
//
// `kind="loader"` suppresses the SPA's MenuBar (no menu to service
// during loader) and the GlobalStatusBar (downloads / calibration /
// residency chips don't apply) while keeping the favicon and
// min/max controls — this is still the main app window, and the
// welcome screen in particular can be up for a while.
export function LoaderApp({ mode }: Props): React.JSX.Element {
  return (
    <WindowChrome kind="loader">
      {/* `relative` + `overflow-hidden` so the absolutely-positioned
          backdrop image is clipped to this region; content sits on
          top via its own `relative`. The image itself sets opacity
          (not the wrapper) so foreground text/buttons stay full
          strength. `pointer-events-none` keeps it out of click and
          drag interactions. */}
      <div className="relative flex flex-1 items-center justify-center overflow-hidden px-12">
        <img
          src={heliosophBg}
          alt=""
          aria-hidden
          className="pointer-events-none absolute top-1/2 left-1/2 h-3/4 w-1/2 -translate-x-1/2 -translate-y-1/2 object-contain opacity-15 select-none"
        />
        <div className="relative">
          {mode === 'splash' ? <Splash /> : <Welcome />}
        </div>
      </div>
    </WindowChrome>
  );
}
