import { TitleBar } from '@/components/titlebar/TitleBar';
import { CalibrationChip } from '@/components/status/CalibrationChip';
import { DownloadsChip } from '@/components/status/DownloadsChip';
import { GlobalStatusBar } from '@/components/status/GlobalStatusBar';
import { ResidencyChip } from '@/components/status/ResidencyChip';
import { ZoomChip } from '@/components/status/ZoomChip';

// Discriminated chrome kinds. The titlebar and surrounding shell
// derive every visibility flag (favicon, MenuBar, min/max, status
// bar) from this one prop, so callers state intent once instead of
// composing a handful of booleans.
//
//   main   = SPA host window: everything on
//   loader = pre-SPA splash / welcome: menu + status bar off,
//            favicon + min/max on (it's still the app window)
//   dialog = modal child window: only the close button + favicon-less
//            titlebar; min/max are disabled at the BrowserWindow
//            level too (electron/main.ts), so even OS shortcuts
//            can't trigger them.
export type WindowChromeKind = 'main' | 'loader' | 'dialog';

// Outer chrome shared across every Electron window in the app:
//
//   - Main window: <WindowChrome>{ left dock + workspace + (right dock) }</WindowChrome>
//   - Loader window: <WindowChrome kind="loader">{ splash / welcome body }</WindowChrome>
//   - Dialog windows: <WindowChrome kind="dialog">{ dialog body }</WindowChrome>
//
// The dock affordances are owned by the children — main App.tsx renders
// AppDock siblings as part of its layout, dialog shells render nothing
// of the kind. This keeps the chrome responsibility narrow: titlebar +
// theme-aware border + a flex-row content slot + bottom status bar.
//
// Resize and drag are handled at the Chromium/OS layer — CSS
// `-webkit-app-region: drag` on the titlebar starts the OS move, and
// Electron's `frame: false` keeps the Windows thickFrame border for
// native edge resize. No JS gesture wiring required.
export function WindowChrome({
  children,
  kind = 'main',
  title,
}: {
  children: React.ReactNode;
  kind?: WindowChromeKind;
  /**
   * Context shown in the titlebar — catalog basename for the main
   * SPA, dialog kind for modals, etc. Undefined = empty title (the
   * convention on Mac / modern GNOME, and increasingly Win11 too:
   * the favicon + OS taskbar already identify the app, so the title
   * is reserved for *document* context, not the app name).
   */
  title?: string;
}) {
  return (
    <div className="bg-background text-foreground border-border flex h-screen flex-col border">
      <TitleBar kind={kind} title={title} />
      <div className="flex flex-1 overflow-hidden">{children}</div>
      {kind === 'main' && (
        <GlobalStatusBar
          leftChips={<DownloadsChip />}
          rightChips={
            <>
              <CalibrationChip />
              <ResidencyChip />
              <ZoomChip />
            </>
          }
        />
      )}
    </div>
  );
}
