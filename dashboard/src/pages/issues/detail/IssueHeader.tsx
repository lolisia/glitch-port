import type { IssueDetail } from '../../../api/types';
import { formatNumber, useI18n } from '../../../i18n';
import { LevelBadge, RelativeTime, StatusBadge } from '../../../ui';
import { PlatformChip } from '../../platform/PlatformChip';

/**
 * Summary of the issue above its actions: title, culprit, and the metadata
 * users need to orient themselves before changing anything.
 */
export function IssueHeader({ issue }: { issue: IssueDetail }) {
  const { locale, t } = useI18n();

  return (
    <header className="issue-detail-header" data-testid="issue-detail-header">
      <div className="issue-detail-title-row">
        <h2>{issue.title}</h2>
        <a className="issue-back" href="#/issues">
          {t('issues.detail.back')}
        </a>
      </div>

      {issue.culprit !== null && issue.culprit !== '' && (
        <p className="issue-culprit">{issue.culprit}</p>
      )}

      <dl className="issue-meta">
        <div>
          <dt>{t('issues.detail.level')}</dt>
          <dd>
            <LevelBadge level={issue.level} />
          </dd>
        </div>
        <div>
          <dt>{t('issues.detail.platform')}</dt>
          <dd>
            <PlatformChip platform={issue.platform} />
          </dd>
        </div>
        <div>
          <dt>{t('issues.detail.status')}</dt>
          <dd>
            <StatusBadge status={issue.status} />
          </dd>
        </div>
        <div>
          <dt>{t('issues.detail.firstSeen')}</dt>
          <dd>
            <RelativeTime value={issue.first_seen} />
          </dd>
        </div>
        <div>
          <dt>{t('issues.detail.lastSeen')}</dt>
          <dd>
            <RelativeTime value={issue.last_seen} />
          </dd>
        </div>
        <div>
          <dt>{t('issues.detail.events')}</dt>
          <dd>{formatNumber(issue.event_count, locale)}</dd>
        </div>
        <div>
          <dt>{t('issues.detail.release')}</dt>
          <dd>{issue.last_release ?? '-'}</dd>
        </div>
        <div>
          <dt>{t('issues.detail.environment')}</dt>
          <dd>{issue.last_environment ?? '-'}</dd>
        </div>
      </dl>
    </header>
  );
}
