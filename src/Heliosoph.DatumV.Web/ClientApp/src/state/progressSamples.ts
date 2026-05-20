// Shared progress-sample bookkeeping for the download-style UIs (models
// and datasets). The sliding window drives the instantaneous-rate /
// ETA display in <DownloadProgressBar>; both state modules push samples
// on every progress event so the bar's smoothing buffer always has
// fresh data.

export interface ProgressSample {
  // performance.now() timestamp in ms.
  t: number;
  bytes: number;
}

const SAMPLE_CAP = 12;
// Drop samples older than this so a long pause (e.g. file open / hash
// verify between files) doesn't pollute the rate window with stale data.
const SAMPLE_MAX_AGE_MS = 8_000;

export function pushSample(
  prev: readonly ProgressSample[] | undefined,
  bytes: number,
): ProgressSample[] {
  const now = performance.now();
  const fresh = (prev ?? []).filter((s) => now - s.t < SAMPLE_MAX_AGE_MS);
  fresh.push({ t: now, bytes });
  if (fresh.length > SAMPLE_CAP) fresh.splice(0, fresh.length - SAMPLE_CAP);
  return fresh;
}

// Instantaneous transfer rate in bytes/sec, derived from the sliding
// progress sample window. Returns null when there isn't enough data
// (one sample, all samples in the same ~tick, or no forward motion).
export function computeRateBytesPerSec(samples: readonly ProgressSample[]): number | null {
  if (samples.length < 2) return null;
  const first = samples[0];
  const last = samples[samples.length - 1];
  const dtSec = (last.t - first.t) / 1000;
  if (dtSec <= 0) return null;
  const dBytes = last.bytes - first.bytes;
  if (dBytes <= 0) return null;
  return dBytes / dtSec;
}

// Seconds remaining at the current rate, or null when unknown (no total,
// no rate, or already complete).
export function computeEtaSeconds(
  bytesRead: number,
  bytesTotal: number,
  nowRate: number | null,
): number | null {
  if (nowRate == null || nowRate <= 0) return null;
  const remaining = bytesTotal - bytesRead;
  if (remaining <= 0) return null;
  return remaining / nowRate;
}
