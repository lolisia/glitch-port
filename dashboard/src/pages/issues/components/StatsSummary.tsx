import type { StatsBucket } from '../../../api/types';

/**
 * A lightweight bar chart of the 24h event buckets. Bar height is relative to
 * the busiest bucket so the whole window stays visible.
 */
export function StatsSummary({ buckets }: { buckets: StatsBucket[] }) {
  const total = buckets.reduce((sum, bucket) => sum + bucket.count, 0);
  const maxCount = buckets.reduce(
    (maximum, bucket) => Math.max(maximum, bucket.count),
    0,
  );

  return (
    <section className="issues-stats">
      <h3>Events (last 24 hours)</h3>
      <div className="stats-chart" data-testid="stats-chart">
        {buckets.map((bucket, index) => (
          <div
            key={`${bucket.bucket}-${index}`}
            className="stats-bar"
            data-testid="stats-bar"
            title={`${bucket.bucket}: ${bucket.count}`}
            style={{
              height: maxCount === 0 ? '0%' : `${(bucket.count / maxCount) * 100}%`,
            }}
          />
        ))}
      </div>
      <p className="stats-total">{total} events</p>
    </section>
  );
}
