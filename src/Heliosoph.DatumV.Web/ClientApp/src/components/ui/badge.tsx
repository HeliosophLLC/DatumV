import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '@/lib/utils';

// Minimal Badge variant in the shadcn shape. Used by the Models view's
// card metadata strip (size, hardware, license). Single-line, rounded-xs
// per the global rule, no hover transition — these are info, not buttons.
const badgeVariants = cva(
  'inline-flex items-center gap-1 rounded-xs border px-1.5 py-0.5 text-xs font-medium whitespace-nowrap',
  {
    variants: {
      variant: {
        default: 'border-transparent bg-primary/10 text-primary',
        outline: 'border-border bg-transparent text-foreground',
        secondary: 'border-transparent bg-secondary text-secondary-foreground',
        muted: 'border-transparent bg-muted text-muted-foreground',
        destructive: 'border-transparent bg-destructive/10 text-destructive',
      },
    },
    defaultVariants: {
      variant: 'default',
    },
  },
);

export interface BadgeProps
  extends React.HTMLAttributes<HTMLSpanElement>,
    VariantProps<typeof badgeVariants> {}

export function Badge({ className, variant, ...props }: BadgeProps) {
  return <span className={cn(badgeVariants({ variant, className }))} {...props} />;
}

export { badgeVariants };
