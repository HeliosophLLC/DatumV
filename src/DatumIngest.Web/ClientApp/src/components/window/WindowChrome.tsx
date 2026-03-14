import { TitleBar } from '@/components/titlebar/TitleBar';
import { AppDock } from '@/components/nav/AppDock';

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
// AppDock (the VS Code-style activity bar) self-gates on `dialog` and
// `isTornOutWindow`, so it renders nothing in modal/torn-out windows
// even though it's mounted unconditionally here.
//
// `dialog` hides minimize/maximize on the titlebar — modal child windows
// shouldn't be minimizable (Electron minimizes them to the taskbar where
// they can't be restored because the parent owns focus), and maximize on a
// fixed-size modal doesn't make sense either. Close stays.
export function WindowChrome({
  children,
  dialog = false,
}: {
  children: React.ReactNode;
  dialog?: boolean;
}) {
  return (
    <div className="bg-background text-foreground border-border flex h-screen flex-col border">
      <TitleBar dialog={dialog} />
      <div className="flex flex-1 overflow-hidden">
        <AppDock dialog={dialog} />
        {children}
      </div>
    </div>
  );
}
