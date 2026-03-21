import { TitleBar } from '@/components/titlebar/TitleBar';

// Outer chrome shared across every Electron window in the app:
//
//   - Main window: <WindowChrome>{ left dock + workspace + (right dock) }</WindowChrome>
//   - Dialog windows: <WindowChrome dialog>{ dialog body }</WindowChrome>
//
// The dock affordances are owned by the children — main App.tsx renders
// AppDock siblings as part of its layout, dialog shells render nothing
// of the kind. This keeps the chrome responsibility narrow: titlebar +
// theme-aware border + a flex-row content slot.
//
// Resize and drag are handled at the Chromium/OS layer — CSS
// `-webkit-app-region: drag` on the titlebar starts the OS move, and
// Electron's `frame: false` keeps the Windows thickFrame border for
// native edge resize. No JS gesture wiring required.
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
      <div className="flex flex-1 overflow-hidden">{children}</div>
    </div>
  );
}
