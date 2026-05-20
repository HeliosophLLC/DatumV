import { Badge } from '@/components/ui/badge';
import { cn } from '@/lib/utils';
import {
  familyAccentClass,
  modalityIcon,
  modalityIconColorClass,
  taskIcon,
} from '@/components/shared/taskStyles';

// Task chip primitives shared between the Models and Datasets surfaces.
// Both lean on the same family-accent + icon vocabulary so a "Suitable
// for: LabeledObjectDetector" chip on a dataset row carries the same
// visual identity as the "tasks: LabeledObjectDetector" chip on a
// model row.
//
// Two variants:
//   - <TaskChipIcon>  — compact icon-only chip with the family-accent
//                       left border, used in list rows where space is
//                       tight. Icon carries the contract identity; the
//                       localized label moves to the tooltip.
//   - <TaskChipLabel> — labeled badge with the family-accent left
//                       border, used in detail-card chip strips. Label
//                       is localized at the call site so the chip
//                       itself stays i18n-agnostic.

interface ChipBaseProps {
  /** Task contract name (e.g. "LabeledObjectDetector"). */
  task: string;
  /** Family identifier (e.g. "ComputerVision") driving the left-border
   *  accent. Empty / unknown families render with a transparent border
   *  so the layout stays stable. */
  family: string;
  /** Localized label resolved by the caller. Drives the tooltip on the
   *  icon-only variant and the visible text on the labeled variant. */
  label: string;
}

export function TaskChipIcon({ task, family, label }: ChipBaseProps) {
  const Icon = taskIcon(task);
  return (
    <span
      title={label}
      aria-label={label}
      className={cn(
        'flex shrink-0 items-center justify-center rounded-xs border border-l-4 px-1 py-0.5 text-muted-foreground',
        familyAccentClass(family),
      )}
    >
      <Icon className="size-3" />
    </span>
  );
}

export function TaskChipLabel({ task, family, label }: ChipBaseProps) {
  void task; // identity tracked by the family accent + caller-supplied label
  return (
    <Badge variant="outline" className={cn('border-l-6', familyAccentClass(family))}>
      {label}
    </Badge>
  );
}

interface ModalityChipProps {
  /** Modality identifier (e.g. "Image", "Text"). Drives the icon + hue. */
  modality: string;
  /** Localized label resolved by the caller. */
  label: string;
}

export function ModalityChipLabel({ modality, label }: ModalityChipProps) {
  const Icon = modalityIcon(modality);
  return (
    <Badge variant="outline" className="gap-1">
      <Icon className={cn('size-3', modalityIconColorClass(modality))} />
      {label}
    </Badge>
  );
}
