import * as ResizablePrimitive from 'react-resizable-panels';
import { cn } from '@/lib/utils';

// Shadcn-style wrapper over react-resizable-panels (v4 API: Group +
// Panel + Separator). Re-themed to repo design tokens so light/dark
// switch automatically.
//
// The handle is a 1 px line with a 4 px hit zone (the `after`
// pseudo-element extends the draggable area without widening the
// visual divider). `withHandle` paints a small grip — opt-in for
// places where the bare line is hard to find.

function ResizablePanelGroup({
  className,
  ...props
}: React.ComponentProps<typeof ResizablePrimitive.Group>) {
  return (
    <ResizablePrimitive.Group
      className={cn('flex h-full w-full', className)}
      {...props}
    />
  );
}

const ResizablePanel = ResizablePrimitive.Panel;

function ResizableHandle({
  withHandle,
  className,
  ...props
}: React.ComponentProps<typeof ResizablePrimitive.Separator> & {
  withHandle?: boolean;
}) {
  // Note on orientation: the library sets `aria-orientation` to the
  // separator's *visual* orientation, which is the opposite of the panel
  // group's. A horizontal panel group (panels side-by-side) gets a
  // separator with aria-orientation="vertical". The base classes here
  // assume that (vertical line, 1 px wide, full height). The
  // `aria-[orientation=horizontal]:` overrides handle the inverse case
  // for vertical panel groups when those land.
  return (
    <ResizablePrimitive.Separator
      className={cn(
        // 1px line, always visible at the border colour. The `after`
        // pseudo-element is a 4px hit zone (also doubles as the thicker
        // visible bar on hover) — it sits absolutely over the 1px line so
        // widening it doesn't disturb the flex row.
        'bg-border focus-visible:ring-ring relative flex w-px items-center justify-center transition-colors duration-150',
        'hover:bg-primary data-[separator=active]:bg-primary',
        'after:absolute after:inset-y-0 after:left-1/2 after:w-1 after:-translate-x-1/2 after:bg-transparent after:transition-colors after:duration-150',
        'hover:after:bg-primary data-[separator=active]:after:bg-primary',
        // Inverse layout for vertical panel groups (when those land).
        'aria-[orientation=horizontal]:h-px aria-[orientation=horizontal]:w-full',
        'aria-[orientation=horizontal]:after:left-0 aria-[orientation=horizontal]:after:h-1',
        'aria-[orientation=horizontal]:after:w-full aria-[orientation=horizontal]:after:-translate-y-1/2',
        'focus-visible:ring-1 focus-visible:outline-none',
        className,
      )}
      {...props}
    >
      {withHandle && (
        <div className="bg-border z-10 flex h-4 w-3 items-center justify-center rounded-xs border">
          <span className="bg-foreground/40 block h-2 w-px" />
        </div>
      )}
    </ResizablePrimitive.Separator>
  );
}

export { ResizablePanelGroup, ResizablePanel, ResizableHandle };
