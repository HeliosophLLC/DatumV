import { TitleBar } from '@/components/titlebar/TitleBar';

// Outer chrome shared across every Electron window in the app:
//
//   - Main window: <WindowChrome>{ SideNav + active view }</WindowChrome>
//   - Dialog windows: each spawned BrowserWindow mounts its own React
//     tree rooted in WindowChrome, so the chromeless titlebar + theme-
//     aware border stay consistent with the main window.
//
// Border uses the design-token `border-border` (resolves from --border
// via Tailwind's shadcn-style theme config), so light/dark switch
// without a hard-coded slate. The whole element renders bg-background +
// text-foreground for the same reason.
//
// Resize and drag are handled at the Chromium/OS layer — CSS
// `-webkit-app-region: drag` on the titlebar starts the OS move, and
// Electron's `frame: false` keeps the Windows thickFrame border for
// native edge resize. No JS gesture wiring required.
//
// What we DELIBERATELY don't include:
//   - SideNav (main-window-only — dialogs don't have nav)
//   - Any content-aware routing (children decide what to render)
export function WindowChrome({ children }: { children: React.ReactNode }) {
  return (
    <div className="bg-background text-foreground border-border flex h-screen flex-col border">
      <TitleBar />
      <div className="flex flex-1 overflow-hidden">{children}</div>
    </div>
  );
}
