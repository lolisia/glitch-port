import type { StatsResponse } from '../../../api/types';
import { useI18n } from '../../../i18n';
import { PlatformChip } from '../../platform/PlatformChip';

/**
 * A lightweight bar chart of the 24h event buckets. Bar height is relative to
 * the busiest bucket so the whole window stays visible. The platform breakdown
 * shows the all-time issue count per observed platform.
 */
export function StatsSummary({ stats }: { stats: StatsResponse }) {
  const { t } = useI18n();
  const { buckets, by_platform: byPlatform } = stats;
  const total = buckets.reduce((sum, bucket) => sum + bucket.count, 0);
  const maxCount = buckets.reduce(
    (maximum, bucket) => Math.max(maximum, bucket.count),
    0,
  );

  return (
    <section className="issues-stats">
      <h3>{t('issues.stats.heading')}</h3>
      <div className="stats-chart" data-testid="stats-chart">
        {buckets.map((bucket, index) => (
          <div
            key={`${bucket.bucket}-${index}`}
            className="stats-bar"
            data-testid="stats-bar"
            title={t('issues.stats.bucketTitle', {
              bucket: bucket.bucket,
              count: bucket.count,
            })}
            style={{
              height: maxCount === 0 ? '0%' : `${(bucket.count / maxCount) * 100}%`,
            }}
          />
        ))}
      </div>
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
