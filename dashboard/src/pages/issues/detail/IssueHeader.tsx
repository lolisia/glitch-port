import type { IssueDetail } from '../../../api/types';
import { LevelBadge, RelativeTime, StatusBadge } from '../../../ui';
import { PlatformChip } from '../../platform/PlatformChip';

/**
 * Summary of the issue above its actions: title, culprit, and the metadata
 * users need to orient themselves before changing anything.
 */
export function IssueHeader({ issue }: { issue: IssueDetail }) {
  return (
    <header className="issue-detail-header" data-testid="issue-detail-header">
      <div className="issue-detail-title-row">
        <h2>{issue.title}</h2>
        <a className="issue-back" href="#/issues">
          Back to issues
        </a>
      </div>

      {issue.culprit !== null && issue.culprit !== '' && (
        <p className="issue-culprit">{issue.culprit}</p>
      )}

      <dl className="issue-meta">
        <div>
          <dt>Level</dt>
          <dd>
            <LevelBadge level={issue.level} />
          </dd>
        </div>
        <div>
          <dt>Platform</dt>
          <dd>
            <PlatformChip platform={issue.platform} />
          </dd>
        </div>
        <div>
          <dt>Status</dt>
          <dd>
            <StatusBadge status={issue.status} />
          </dd>
        </div>
        <div>
          <dt>First seen</dt>
          <dd>
            <RelativeTime value={issue.first_seen} />
          </dd>
        </div>
        <div>
          <dt>Last seen</dt>
          <dd>
            <RelativeTime value={issue.last_seen} />
          </dd>
        </div>
        <div>
          <dt>Events</dt>
          <dd>{issue.event_count}</dd>
        </div>
        <div>
          <dt>Release</dt>
          <dd>{issue.last_release ?? '-'}</dd>
        </div>
        <div>
          <dt>Environment</dt>
          <dd>{issue.last_environment ?? '-'}</dd>
        </div>
      </dl>
    </header>
  );
}
