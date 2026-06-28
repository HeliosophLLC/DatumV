import { proxy } from 'valtio';

import { api } from '@/api';
import {
  acquireCatalogHub,
  onCalibrationRampCompleted,
  onCalibrationRampHalted,
  onCalibrationRampStarted,
  onCalibrationRampStep,
} from '@/api/catalogHub';
import { CalibrationHaltReason } from '@/api/generated/hubs/Heliosoph.DatumV.Web.Hubs';

// Live calibration state for the status-bar chip. Three concerns:
//
//   1. activeRamp — the model currently being calibrated, if any.
//      Drives the chip's blue/flashing visual state. Null when idle.
//
//   2. curves — every model's measured curve, keyed by model name.
//      The popover renders this as a table. Seeded from
//      /api/model-runtime/calibration on first use; mutated in place
//      by hub-pushed ramp-step events as new entries arrive.
//
//   3. recentCompletions — short ring of finished ramps (success,
//      halt, spill) so the chip can flash briefly post-completion
//      rather than snap back to neutral the instant the active
//      ramp ends. Keeps the UI signal visible for a few seconds.

export interface CalibrationCurvePoint {
  batchSize: number;
  totalVramBytes: number;
}

export interface CalibrationCurve {
  modelName: string;
  weightCostBytes: number;
  // Mirror of ModelCalibration.State as a lowercase string —
  // 'uncalibrated' | 'calibrated' | 'stale'. The chip and popover
  // present these with their own copy; we keep the string opaque
  // here.
  status: string;
  entries: CalibrationCurvePoint[];
}

export interface ActiveRamp {
  modelName: string;
  fingerprint: string;
  // Live curve as it builds. Each OnRampStep event appends; the
  // popover can render the partial curve in real time. Cleared on
  // ramp completion / halt.
  stepsSoFar: CalibrationCurvePoint[];
  startedAt: number;
}

export interface RecentCompletion {
  modelName: string;
  // 'completed' | 'halted-projection' | 'halted-spill' | 'halted-error'.
  // Distinct strings rather than nested enum because the chip's only
  // consumer is a colour decision; an enum would force a
  // Tapper-generated import for a handful of values.
  outcome: 'completed' | 'halted-projection' | 'halted-spill' | 'halted-error';
  finishedAt: number;
}

interface CalibrationState {
  activeRamp: ActiveRamp | null;
  curves: Record<string, CalibrationCurve>;
  recentCompletions: RecentCompletion[];
  revision: number;
  seeded: boolean;
  // One-time coach-mark: true while the "first run must be
  // calibrated" bubble should pop out of the status-bar chip. Raised
  // the first time a ramp starts in this install; cleared (and
  // persisted) once the user acknowledges it.
  coachMarkVisible: boolean;
}

// localStorage flag recording that the user has dismissed the
// first-run calibration explainer. Once set, the coach-mark never
// shows again on this machine. Best-effort: private mode / quota
// failures just mean the hint can reappear, which is harmless.
const COACH_MARK_ACK_KEY = 'datumv.calibrationCoachMarkAck';

function coachMarkAcknowledged(): boolean {
  try {
    return localStorage.getItem(COACH_MARK_ACK_KEY) === '1';
  } catch {
    return false;
  }
}

// Display window for the "just finished" chip flash. After this many
// milliseconds the chip drops back to neutral. Long enough for the
// user to notice; short enough to not clutter the bar after a quiet
// burst of ramps.
const RECENT_RAMP_WINDOW_MS = 4_000;
const RECENT_RAMP_KEEP = 5;

export const calibrationState = proxy<CalibrationState>({
  activeRamp: null,
  curves: {},
  recentCompletions: [],
  revision: 0,
  seeded: false,
  coachMarkVisible: false,
});

/**
 * Marks the first-run calibration explainer as acknowledged. Hides
 * the chip's coach-mark immediately and persists the choice so it
 * never reappears on this machine.
 */
export function dismissCalibrationCoachMark(): void {
  calibrationState.coachMarkVisible = false;
  try {
    localStorage.setItem(COACH_MARK_ACK_KEY, '1');
  } catch {
    /* private mode / quota — best-effort */
  }
}

function bump(): void {
  calibrationState.revision = (calibrationState.revision + 1) | 0;
}

/**
 * Seeds `curves` from /api/model-runtime/calibration. Cheap (a few
 * dozen entries at most); callers that open the popover trigger this
 * to ensure the table reflects server truth, then the hub events
 * keep it current.
 */
export async function refreshCalibration(): Promise<void> {
  try {
    const list = await api.modelRuntime.getCalibration();
    const next: Record<string, CalibrationCurve> = {};
    for (const c of list) {
      // NSwag-generated DTOs make every field optional. Skip rows
      // with no name (server never omits it in practice); coerce
      // the rest with safe defaults.
      if (!c.modelName) continue;
      next[c.modelName] = {
        modelName: c.modelName,
        weightCostBytes: c.weightCostBytes ?? 0,
        status: c.status ?? 'uncalibrated',
        entries: (c.entries ?? []).map((e) => ({
          batchSize: e.batchSize ?? 0,
          totalVramBytes: e.totalVramBytes ?? 0,
        })),
      };
    }
    calibrationState.curves = next;
    calibrationState.seeded = true;
    bump();
  } catch (err) {
    console.error('[calibration] seed failed', err);
    calibrationState.seeded = true;
  }
}

function pushRecent(rc: RecentCompletion): void {
  const fresh = calibrationState.recentCompletions.filter(
    (r) => Date.now() - r.finishedAt < RECENT_RAMP_WINDOW_MS,
  );
  fresh.unshift(rc);
  if (fresh.length > RECENT_RAMP_KEEP) fresh.length = RECENT_RAMP_KEEP;
  calibrationState.recentCompletions = fresh;
}

void acquireCatalogHub().catch(() => {
  // Connection failure surfaces later when something tries to act.
});

onCalibrationRampStarted((ev) => {
  calibrationState.activeRamp = {
    modelName: ev.modelName,
    fingerprint: ev.fingerprint,
    stepsSoFar: [],
    startedAt: Date.now(),
  };
  // First time a model calibrates on this machine, pop the explainer
  // out of the chip so the user understands the unexpected pause. The
  // ack flag is persisted, so this only ever fires once.
  if (!calibrationState.coachMarkVisible && !coachMarkAcknowledged()) {
    calibrationState.coachMarkVisible = true;
  }
  bump();
});

onCalibrationRampStep((ev) => {
  const active = calibrationState.activeRamp;
  if (active && active.modelName === ev.modelName) {
    active.stepsSoFar.push({
      batchSize: ev.batchSize,
      totalVramBytes: ev.totalVramBytes,
    });
  }
  // Also mutate the visible curve so the popover reflects each step
  // as it lands, even if the user opened the popover mid-ramp.
  const existing = calibrationState.curves[ev.modelName];
  if (existing) {
    // Replace any existing entry at this batch size; Record's
    // contract is "overwrite per batch" so this matches.
    const idx = existing.entries.findIndex((e) => e.batchSize === ev.batchSize);
    const next: CalibrationCurvePoint = {
      batchSize: ev.batchSize,
      totalVramBytes: ev.totalVramBytes,
    };
    if (idx >= 0) existing.entries[idx] = next;
    else existing.entries.push(next);
    existing.entries.sort((a, b) => a.batchSize - b.batchSize);
  } else {
    // First sighting — synthesise a curve entry. weight_cost is
    // unknown here (the ramp doesn't ship it via the step event
    // because it's already known to anyone reading system.models).
    // The next refreshCalibration() call will populate it; until
    // then the popover shows total only.
    calibrationState.curves[ev.modelName] = {
      modelName: ev.modelName,
      weightCostBytes: 0,
      status: 'uncalibrated',
      entries: [
        { batchSize: ev.batchSize, totalVramBytes: ev.totalVramBytes },
      ],
    };
  }
  bump();
});

onCalibrationRampHalted((ev) => {
  pushRecent({
    modelName: ev.modelName,
    outcome:
      ev.reason === CalibrationHaltReason.DurationSpill
        ? 'halted-spill'
        : ev.reason === CalibrationHaltReason.DispatchError
          ? 'halted-error'
          : 'halted-projection',
    finishedAt: Date.now(),
  });
  if (calibrationState.activeRamp?.modelName === ev.modelName) {
    calibrationState.activeRamp = null;
  }
  // The curve already reflects whatever steps recorded before the
  // halt — RecordSpill on the engine side drops the offending entry
  // and everything above. Re-seeding catches that; we defer to the
  // user opening the popover (which triggers refreshCalibration) so
  // we don't burn a fetch on every halt.
  bump();
});

onCalibrationRampCompleted((ev) => {
  pushRecent({
    modelName: ev.modelName,
    outcome: 'completed',
    finishedAt: Date.now(),
  });
  if (calibrationState.activeRamp?.modelName === ev.modelName) {
    calibrationState.activeRamp = null;
  }
  // Mark the curve as calibrated locally so the popover doesn't
  // flicker between 'uncalibrated' (synthesised from step events)
  // and 'calibrated' (after the next refresh). Server is the source
  // of truth; this is the same state in advance.
  const curve = calibrationState.curves[ev.modelName];
  if (curve) {
    curve.status = 'calibrated';
  }
  bump();
});
