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
          <dt>{t('issues.detail.firstRelease')}</dt>
          <dd>{issue.first_release ?? '-'}</dd>
        </div>
        <div>
          <dt>{t('issues.detail.lastRelease')}</dt>
          <dd>{issue.last_release ?? '-'}</dd>
        </div>
        <div>
          <dt>{t('issues.detail.resolvedAt')}</dt>
          <dd>
            {issue.resolved_at === null ? (
              '-'
            ) : (
              <RelativeTime value={issue.resolved_at} />
            )}
          </dd>
        </div>
        <div>
          <dt>{t('issues.detail.resolvedInRelease')}</dt>
          <dd>{issue.resolved_in_release ?? '-'}</dd>
        </div>
        <div>
          <dt>{t('issues.detail.regressionCount')}</dt>
          <dd>{formatNumber(issue.regression_count, locale)}</dd>
        </div>
        <div>
          <dt>{t('issues.detail.regressedAt')}</dt>
          <dd>
            {issue.regressed_at === null ? (
              '-'
            ) : (
              <RelativeTime value={issue.regressed_at} />
            )}
          </dd>
        </div>
        <div>
          <dt>{t('issues.detail.environment')}</dt>
          <dd>{issue.last_environment ?? '-'}</dd>
        </div>
      </dl>

      {issue.resolved_in_release !== null &&
        issue.resolved_in_release !== '' && (
          <p className="issue-reopen-note" data-testid="issue-reopen-note">
            {t('issues.detail.reopenNote', {
              release: issue.resolved_in_release,
            })}
          </p>
        )}
    </header>
  );
}
