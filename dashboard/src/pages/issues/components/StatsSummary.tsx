import { useState } from 'react';
import type { StatsBucket, StatsResponse } from '../../../api/types';
import { formatNumber, useI18n } from '../../../i18n';
import { formatAbsoluteTime, formatClockTime } from '../../../ui/time';
import { PlatformChip } from '../../platform/PlatformChip';

const HOUR_MS = 60 * 60 * 1000;
/** X-axis labels are printed on local hours divisible by this step. */
const X_LABEL_HOUR_STEP = 3;
/** The y-axis never shows more than this many intervals. */
const MAX_Y_INTERVALS = 4;

/**
 * Rounds the busiest bucket up to a clean axis maximum (1/2/5 × 10^n steps)
 * so the tick labels stay readable. An empty window keeps a 0–1 axis.
 */
export function niceAxis(maxCount: number): { max: number; ticks: number[] } {
  if (maxCount <= 0) {
    return { max: 1, ticks: [0, 1] };
  }

  let magnitude = 1;
  for (;;) {
    for (const factor of [1, 2, 5]) {
      const step = factor * magnitude;
      const intervals = Math.ceil(maxCount / step);
      if (intervals <= MAX_Y_INTERVALS) {
        const ticks = Array.from({ length: intervals + 1 }, (_, i) => i * step);
        return { max: intervals * step, ticks };
      }
    }
    magnitude *= 10;
  }
}

/** `YYYY-MM-DD HH:mm` without the seconds of the absolute timestamp. */
function minutePrecision(value: string): string {
  return value.replace(/:\d{2}$/, '');
}

function bucketEnd(bucket: StatsBucket): string | null {
  const start = Date.parse(bucket.bucket);
  return Number.isNaN(start) ? null : new Date(start + HOUR_MS).toISOString();
}

/**
 * A bar chart of the 24h hourly event buckets with a count axis, local-hour
 * labels and a tooltip (hover or keyboard focus) giving each bucket's time
 * range and event count. The platform breakdown shows the all-time issue
 * count per observed platform.
 */
export function StatsSummary({ stats }: { stats: StatsResponse }) {
  const { t, locale } = useI18n();
  const [active, setActive] = useState<number | null>(null);
  const { buckets, by_platform: byPlatform } = stats;
  const total = buckets.reduce((sum, bucket) => sum + bucket.count, 0);
  const maxCount = buckets.reduce(
    (maximum, bucket) => Math.max(maximum, bucket.count),
    0,
  );
  const axis = niceAxis(maxCount);

  const rangeLabel = (bucket: StatsBucket): string => {
    const start = minutePrecision(formatAbsoluteTime(bucket.bucket, locale));
    const end = bucketEnd(bucket);
    return end === null
      ? start
      : `${start} – ${minutePrecision(formatClockTime(end, locale))}`;
  };

  const xLabel = (bucket: StatsBucket): string | null => {
    const date = new Date(bucket.bucket);
    if (Number.isNaN(date.getTime()) || date.getHours() % X_LABEL_HOUR_STEP !== 0) {
      return null;
    }
    return minutePrecision(formatClockTime(bucket.bucket, locale));
  };

  const activeBucket = active === null ? undefined : buckets[active];
  // Edge bars anchor the tooltip to their own side so it stays inside the chart.
  const tooltipAlign =
    active === null
      ? 'center'
      : active < 2
        ? 'start'
        : active > buckets.length - 3
          ? 'end'
          : 'center';

  return (
    <section className="issues-stats">
      <h3>{t('issues.stats.heading')}</h3>
      {buckets.length > 0 && (
        <div className="stats-chart" data-testid="stats-chart">
          <div className="stats-y-axis" aria-hidden="true">
            {axis.ticks.map((tick) => (
              <span
                key={tick}
                className="stats-y-tick"
                style={{ bottom: `${(tick / axis.max) * 100}%` }}
              >
                {formatNumber(tick, locale)}
              </span>
            ))}
          </div>

          <div className="stats-plot" onMouseLeave={() => setActive(null)}>
            {axis.ticks.map((tick) => (
              <div
                key={tick}
                className="stats-gridline"
                aria-hidden="true"
                style={{ bottom: `${(tick / axis.max) * 100}%` }}
              />
            ))}

            <div className="stats-bars">
              {buckets.map((bucket, index) => {
                const label = t('issues.stats.bucketTitle', {
                  bucket: rangeLabel(bucket),
                  count: t('issues.stats.bucketCount', { count: bucket.count }),
                });
                return (
                  <div
                    key={`${bucket.bucket}-${index}`}
                    className={
                      index === active ? 'stats-slot stats-slot-active' : 'stats-slot'
                    }
                    tabIndex={0}
                    role="img"
                    aria-label={label}
                    onMouseEnter={() => setActive(index)}
                    onFocus={() => setActive(index)}
                    onBlur={() => setActive(null)}
                  >
                    <div
                      className="stats-bar"
                      data-testid="stats-bar"
                      style={{ height: `${(bucket.count / axis.max) * 100}%` }}
                    />
                  </div>
                );
              })}
            </div>

            {activeBucket !== undefined && active !== null && (
              <div
                className={`stats-tooltip stats-tooltip-${tooltipAlign}`}
                role="tooltip"
                data-testid="stats-tooltip"
                style={{
                  left: `${((active + 0.5) / buckets.length) * 100}%`,
                  bottom: `${(activeBucket.count / axis.max) * 100}%`,
                }}
              >
                <strong className="stats-tooltip-value">
                  {t('issues.stats.bucketCount', { count: activeBucket.count })}
                </strong>
                <span className="stats-tooltip-label">{rangeLabel(activeBucket)}</span>
              </div>
            )}
          </div>

          <div className="stats-x-axis" aria-hidden="true">
            {buckets.map((bucket, index) => (
              <span key={`${bucket.bucket}-${index}`} className="stats-x-tick">
                {xLabel(bucket)}
              </span>
            ))}
          </div>
        </div>
      )}
      <p className="stats-total">{t('issues.stats.total', { count: total })}</p>

      {byPlatform.length > 0 && (
        <div className="stats-platforms" data-testid="stats-platforms">
          <h4>{t('issues.stats.byPlatform')}</h4>
          <ul className="stats-platform-list">
            {byPlatform.map((entry) => (
              <li key={entry.platform} className="stats-platform-row">
                <PlatformChip platform={entry.platform} />
                <span className="stats-platform-count">
                  {t('issues.stats.issueCount', { count: entry.issue_count })}
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  );
}
