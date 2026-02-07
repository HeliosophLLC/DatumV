import { cn } from '@/lib/utils';

// Minimal determinate progress bar. shadcn ships a Radix-backed one; ours
// is a single div with a width-transitioned inner fill, which is plenty
// for the model-download case (smooth, themed, no a11y issues since the
// element has role="progressbar" and aria-valuenow).
//
// `value` is 0-100. Clamp out of bounds so a bad calculation doesn't
// overshoot the bar.
export interface ProgressProps extends React.HTMLAttributes<HTMLDivElement> {
  value: number;
}

export function Progress({ value, className, ...rest }: ProgressProps) {
  const clamped = Math.max(0, Math.min(100, value));
  return (
    <div
      role="progressbar"
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={Math.round(clamped)}
      className={cn('bg-muted relative h-1.5 w-full overflow-hidden rounded-xs', className)}
      {...rest}
    >
      <div
        className="bg-primary h-full transition-[width] duration-150 ease-out"
        style={{ width: `${clamped}%` }}
      />
    </div>
  );
}
