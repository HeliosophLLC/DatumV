// Shared formatters for the download progress UI. Used by both the model
// and dataset surfaces — the strings render identically across the two
// install pipelines and live here so the surfaces don't drift.

export function formatBytesPerSec(bps: number): string {
  const KB = 1024;
  const MB = KB * 1024;
  const GB = MB * 1024;
  if (bps >= GB) return `${(bps / GB).toFixed(2)} GB/s`;
  if (bps >= MB) return `${(bps / MB).toFixed(1)} MB/s`;
  if (bps >= KB) return `${(bps / KB).toFixed(0)} KB/s`;
  return `${Math.round(bps)} B/s`;
}

const durationFormatterCache = new Map<string, Intl.DurationFormat>();

function getDurationFormatter(locale: string): Intl.DurationFormat {
  let f = durationFormatterCache.get(locale);
  if (!f) {
    f = new Intl.DurationFormat(locale, { style: 'narrow' });
    durationFormatterCache.set(locale, f);
  }
  return f;
}

export function formatDuration(totalSec: number, locale: string): string {
  const h = Math.floor(totalSec / 3600);
  const m = Math.floor((totalSec % 3600) / 60);
  const s = totalSec % 60;

  const duration: Intl.DurationInput = {};
  if (h > 0) duration.hours = h;
  if (h > 0 || m > 0) duration.minutes = m;
  if (h === 0) duration.seconds = s;

  return getDurationFormatter(locale).format(duration);
}

// Truncates long source paths so the in-progress status line doesn't
// blow out the bar's width. Keeps the trailing filename intact when
// there's a directory prefix to drop.
export function shortenPath(path: string): string {
  if (path.length <= 48) return path;
  const slash = path.lastIndexOf('/');
  if (slash === -1) return path.slice(0, 24) + '…' + path.slice(-24);
  const tail = path.slice(slash + 1);
  return '…/' + tail;
}
