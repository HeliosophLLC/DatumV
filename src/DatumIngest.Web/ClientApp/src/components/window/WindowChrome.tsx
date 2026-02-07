import { TitleBar } from '@/components/titlebar/TitleBar';
import { ResizeFrame } from '@/components/window/ResizeFrame';

// Outer chrome shared across every Photino window in the app:
//
//   - Main window: <WindowChrome>{ SideNav + active view }</WindowChrome>
//   - Dialog windows (when the dialog IPC bridge lands): each spawned
//     window mounts its own React tree rooted in WindowChrome, so the
//     chromeless titlebar + resize-grab zones + theme-aware border are
//     consistent with the main window.
//
// Border uses the design-token `border-border` (resolves from --border
// via Tailwind's shadcn-style theme config), so light/dark switch
// without a hard-coded slate. The whole element renders bg-background +
// text-foreground for the same reason.
//
// What we DELIBERATELY don't include:
//   - SideNav (main-window-only — dialogs don't have nav)
//   - Any content-aware routing (children decide what to render)
//
// Layout shape: column flex (TitleBar at top, then a horizontal flex
// row container that fills the remaining height — children land in
// that row container).
export function WindowChrome({ children }: { children: React.ReactNode }) {
  return (
    <div className="bg-background text-foreground border-border flex h-screen flex-col border">
      <TitleBar />
      <ResizeFrame />
      <div className="flex flex-1 overflow-hidden">{children}</div>
    </div>
  );
}
