/**
 * Selects which trend snapshots a chosen time range should plot.
 *
 * Extracted from TrendsPage so the rule is testable. The rule matters because
 * the page previously fell back to "the last two snapshots" whenever a window
 * held fewer than two — and then still labelled the chart with the requested
 * range. A 7-day chart drawn from data captured outside those 7 days is a chart
 * that lies, so callers get an explicit flag and must say so.
 */
export type WindowSelection<T> = {
  /** Snapshots to plot. */
  items: T[];
  /** True when `items` came from outside the requested range because the range was too sparse. */
  usingFallback: boolean;
};

export function selectTrendWindow<T>(
  snapshots: T[],
  timeRangeDays: number,
  capturedAt: (s: T) => string,
): WindowSelection<T> {
  if (snapshots.length === 0) return { items: [], usingFallback: false };

  const latest = new Date(capturedAt(snapshots[snapshots.length - 1])).getTime();
  const cutoff = latest - timeRangeDays * 24 * 60 * 60 * 1000;
  const inWindow = snapshots.filter(s => new Date(capturedAt(s)).getTime() >= cutoff);

  // One point cannot form a line. Fall back only when there is something to fall
  // back to, and always report that we did.
  if (inWindow.length <= 1 && snapshots.length > 1) {
    return { items: snapshots.slice(-2), usingFallback: true };
  }
  return { items: inWindow, usingFallback: false };
}
