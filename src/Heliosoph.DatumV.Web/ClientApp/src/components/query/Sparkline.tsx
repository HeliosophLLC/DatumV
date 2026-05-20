import { useRef } from 'react';

// Tiny SVG sparkline. Takes a values array, renders a line (and optional
// area fill) over a fixed viewBox so it scales cleanly into any container.
// Sized via the parent's width/height — the SVG fills its box.
//
// Two modes:
//   - Default: chrome-less line/area. Used for the status-bar chip.
//   - Interactive: adds a hover crosshair (vertical line + dot at the
//     hovered sample) and reports the hovered index to the parent. The
//     parent can echo `hoveredIndex` back so multiple sparklines share
//     a crosshair (e.g. Row + Arena in the memory popover).
//
// Sparklines stop being sparklines the moment you add lots of chrome —
// keep this component focused. Tooltip rendering lives in the parent.

export interface SparklineProps {
  /** Values in time order. Empty array renders nothing. */
  values: number[];
  /**
   * Max-y override. When provided, the sparkline uses this as the upper
   * bound so multiple sparklines can share a y-scale (e.g. row + arena
   * stacked in the popover). When omitted, scales to the data's own max.
   */
  maxValue?: number;
  /** Stroke colour. Falls back to currentColor so Tailwind text utilities work. */
  stroke?: string;
  /** Fill colour for the area under the line. Omit for a line-only spark. */
  fill?: string;
  /** Stroke width in viewBox units. */
  strokeWidth?: number;
  /**
   * Optional threshold value rendered as a dashed horizontal line. Used by
   * the memory popover to show the spill budget. Ignored when undefined.
   */
  threshold?: number;
  /** Threshold line colour. Defaults to a muted red. */
  thresholdStroke?: string;
  /** ARIA label for screen readers. */
  ariaLabel?: string;
  className?: string;
  /**
   * When true, the SVG captures mouse events and reports the hovered
   * sample index via <see cref="onHover"/>. A vertical crosshair + dot
   * renders at the hovered index. Default false (chrome-less behaviour).
   */
  interactive?: boolean;
  /**
   * Externally-controlled hover index. Set by the parent when syncing
   * crosshairs across sibling sparklines. <c>null</c> hides the
   * crosshair. Ignored when <see cref="interactive"/> is false.
   */
  hoveredIndex?: number | null;
  /**
   * Fires when the hovered index changes. Receives <c>null</c> on
   * pointer leave. Only fires when <see cref="interactive"/> is true.
   */
  onHover?: (index: number | null) => void;
}

const VIEW_W = 100;
const VIEW_H = 24;

export function Sparkline({
  values,
  maxValue,
  stroke = 'currentColor',
  fill,
  strokeWidth = 1.25,
  threshold,
  thresholdStroke = '#dc2626',
  ariaLabel,
  className,
  interactive = false,
  hoveredIndex,
  onHover,
}: SparklineProps) {
  const svgRef = useRef<SVGSVGElement | null>(null);

  if (values.length === 0) {
    return (
      <svg
        className={className}
        viewBox={`0 0 ${VIEW_W} ${VIEW_H}`}
        preserveAspectRatio="none"
        aria-hidden="true"
      />
    );
  }

  // Effective max: caller-supplied OR data max OR 1 (avoid divide-by-zero
  // when all samples are zero — we still want a flat baseline).
  const dataMax = values.reduce((m, v) => (v > m ? v : m), 0);
  const max = Math.max(maxValue ?? dataMax, 1);

  // Single value → centre dot, no line. Cheap visual cue that data exists
  // even when there's only one sample to draw.
  if (values.length === 1) {
    const y = VIEW_H - (values[0] / max) * VIEW_H;
    return (
      <svg
        className={className}
        viewBox={`0 0 ${VIEW_W} ${VIEW_H}`}
        preserveAspectRatio="none"
        aria-label={ariaLabel}
      >
        <circle cx={VIEW_W / 2} cy={y} r={strokeWidth} fill={stroke} />
      </svg>
    );
  }

  const dx = VIEW_W / (values.length - 1);
  let pathD = '';
  let areaD = '';
  for (let i = 0; i < values.length; i++) {
    const x = i * dx;
    const y = VIEW_H - (values[i] / max) * VIEW_H;
    pathD += i === 0 ? `M ${x} ${y}` : ` L ${x} ${y}`;
    if (fill) {
      areaD += i === 0 ? `M ${x} ${VIEW_H} L ${x} ${y}` : ` L ${x} ${y}`;
    }
  }
  if (fill) areaD += ` L ${VIEW_W} ${VIEW_H} Z`;

  const thresholdY =
    threshold !== undefined && threshold > 0
      ? VIEW_H - (threshold / max) * VIEW_H
      : null;

  // Crosshair geometry. Resolves the hovered index → viewBox X coord; the
  // dot picks up the sample's Y value. Skipped when hoveredIndex is out
  // of range so an external hoveredIndex set during a state transition
  // doesn't crash.
  const showCrosshair =
    interactive &&
    hoveredIndex !== null &&
    hoveredIndex !== undefined &&
    hoveredIndex >= 0 &&
    hoveredIndex < values.length;
  const crosshairX = showCrosshair ? hoveredIndex! * dx : 0;
  const crosshairY = showCrosshair
    ? VIEW_H - (values[hoveredIndex!] / max) * VIEW_H
    : 0;

  // Pointer-event handler: convert clientX → sample index. Uses the SVG's
  // bounding-rect width (CSS pixels) and maps it back to viewBox units so
  // the index matches what the crosshair will render. preserveAspectRatio
  // = "none" means viewBox-X and CSS-X scale uniformly along width.
  function handlePointerMove(ev: React.PointerEvent<SVGSVGElement>): void {
    if (!interactive || !onHover) return;
    const rect = svgRef.current?.getBoundingClientRect();
    if (!rect || rect.width <= 0) return;
    const cssX = ev.clientX - rect.left;
    const fraction = Math.max(0, Math.min(1, cssX / rect.width));
    const idx = Math.round(fraction * (values.length - 1));
    onHover(idx);
  }

  function handlePointerLeave(): void {
    if (interactive && onHover) onHover(null);
  }

  return (
    <svg
      ref={svgRef}
      className={className}
      viewBox={`0 0 ${VIEW_W} ${VIEW_H}`}
      preserveAspectRatio="none"
      aria-label={ariaLabel}
      onPointerMove={interactive ? handlePointerMove : undefined}
      onPointerLeave={interactive ? handlePointerLeave : undefined}
      style={interactive ? { cursor: 'crosshair' } : undefined}
    >
      {fill && <path d={areaD} fill={fill} stroke="none" />}
      <path
        d={pathD}
        fill="none"
        stroke={stroke}
        strokeWidth={strokeWidth}
        vectorEffect="non-scaling-stroke"
        strokeLinejoin="round"
        strokeLinecap="round"
      />
      {thresholdY !== null && thresholdY >= 0 && thresholdY <= VIEW_H && (
        <line
          x1={0}
          x2={VIEW_W}
          y1={thresholdY}
          y2={thresholdY}
          stroke={thresholdStroke}
          strokeWidth={0.5}
          strokeDasharray="2 2"
          vectorEffect="non-scaling-stroke"
        />
      )}
      {showCrosshair && (
        <>
          <line
            x1={crosshairX}
            x2={crosshairX}
            y1={0}
            y2={VIEW_H}
            stroke="currentColor"
            strokeWidth={0.5}
            strokeOpacity={0.4}
            vectorEffect="non-scaling-stroke"
          />
          <circle
            cx={crosshairX}
            cy={crosshairY}
            r={strokeWidth * 1.6}
            fill={stroke}
          />
        </>
      )}
    </svg>
  );
}
