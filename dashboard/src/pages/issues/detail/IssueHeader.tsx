import type { ReactNode } from 'react';
import type { IssueDetail } from '../../../api/types';
import { formatNumber, useI18n, type Translate } from '../../../i18n';
import { translatedLabel } from '../../../ui/labels';
import { RelativeTime } from '../../../ui/RelativeTime';
import { Badge } from '@/components/ui/badge';
import { Card } from '@/components/ui/card';
import { Separator } from '@/components/ui/separator';
import { PlatformChip } from '../../platform/PlatformChip';

/** Token-based tones so levels keep their meaning without raw colours. */
const LEVEL_TONES: Record<string, string> = {
  fatal: 'border-destructive/40 bg-destructive/10 text-destructive',
  error: 'border-destructive/40 bg-destructive/10 text-destructive',
  warning: 'border-warning/50 bg-warning/15 text-foreground',
  info: 'border-info/40 bg-info/10 text-foreground',
};

const NEUTRAL_LEVEL_TONE = 'border-border bg-muted text-muted-foreground';

const STATUS_TONES: Record<string, string> = {
  unresolved: 'border-destructive/40 bg-destructive/10 text-destructive',
  resolved: 'border-success/50 bg-success/15 text-foreground',
};

const NEUTRAL_STATUS_TONE = 'border-border bg-muted text-muted-foreground';

/** Mirrors LevelBadge so the header label keeps its previous text. */
function levelLabel(level: string, t: Translate): string {
  return translatedLabel(t, `ui.level.${level.toLowerCase()}`, level);
}

/** Mirrors StatusBadge so the header label keeps its previous text. */
function statusLabel(status: string, t: Translate): string {
  const fallback =
    status === '' ? status : status.charAt(0).toUpperCase() + status.slice(1);

  return translatedLabel(t, `ui.status.${status.toLowerCase()}`, fallback);
}

/**
 * One labelled metadata row. The dl/dt/dd structure is part of the detail
 * screen's contract, so the definition list stays even though each row is a
 * grid cell.
 */
function MetaField({
  label,
  children,
  valueClassName,
}: {
  label: string;
  children: ReactNode;
  valueClassName?: string;
}) {
  return (
    <div className="flex min-w-0 flex-col gap-1">
      <dt className="text-xs font-semibold tracking-wide text-muted-foreground uppercase">
        {label}
      </dt>
      <dd className={`m-0 text-sm ${valueClassName ?? ''}`}>{children}</dd>
    </div>
  );
}

/**
 * Summary of the issue above its actions: title, culprit, and the metadata
 * users need to orient themselves before changing anything.
 */
export function IssueHeader({ issue }: { issue: IssueDetail }) {
  const { locale, t } = useI18n();

  return (
    <Card className="gap-4 p-6" data-testid="issue-detail-header">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <h2 className="m-0 text-xl font-semibold break-words">
          {issue.title}
        </h2>
        <a
          className="shrink-0 text-sm font-medium text-primary hover:underline"
          href="#/issues"
        >
          {t('issues.detail.back')}
        </a>
      </div>

      {issue.culprit !== null && issue.culprit !== '' && (
        <p className="m-0 font-mono text-sm break-words text-muted-foreground">
          {issue.culprit}
        </p>
      )}

      <Separator />

      <dl className="m-0 grid grid-cols-2 gap-x-6 gap-y-3 sm:grid-cols-3 lg:grid-cols-4">
        <MetaField label={t('issues.detail.level')}>
          <Badge
            variant="outline"
            className={
              LEVEL_TONES[issue.level.toLowerCase()] ?? NEUTRAL_LEVEL_TONE
            }
          >
            {levelLabel(issue.level, t)}
          </Badge>
        </MetaField>
        <MetaField label={t('issues.detail.platform')}>
          <PlatformChip platform={issue.platform} />
        </MetaField>
        <MetaField label={t('issues.detail.status')}>
          <Badge
            variant="outline"
            className={
              STATUS_TONES[issue.status.toLowerCase()] ?? NEUTRAL_STATUS_TONE
            }
          >
            {statusLabel(issue.status, t)}
          </Badge>
        </MetaField>
        <MetaField label={t('issues.detail.firstSeen')}>
          <RelativeTime value={issue.first_seen} />
        </MetaField>
        <MetaField label={t('issues.detail.lastSeen')}>
          <RelativeTime value={issue.last_seen} />
        </MetaField>
        <MetaField
          label={t('issues.detail.events')}
          valueClassName="tabular-nums"
        >
          {formatNumber(issue.event_count, locale)}
        </MetaField>
        <MetaField label={t('issues.detail.firstRelease')}>
          {issue.first_release ?? '-'}
        </MetaField>
        <MetaField label={t('issues.detail.lastRelease')}>
          {issue.last_release ?? '-'}
        </MetaField>
        <MetaField label={t('issues.detail.resolvedAt')}>
          {issue.resolved_at === null ? (
            '-'
          ) : (
            <RelativeTime value={issue.resolved_at} />
          )}
        </MetaField>
        <MetaField label={t('issues.detail.resolvedInRelease')}>
          {issue.resolved_in_release ?? '-'}
        </MetaField>
        <MetaField
          label={t('issues.detail.regressionCount')}
          valueClassName="tabular-nums"
        >
          {formatNumber(issue.regression_count, locale)}
        </MetaField>
        <MetaField label={t('issues.detail.regressedAt')}>
          {issue.regressed_at === null ? (
            '-'
          ) : (
            <RelativeTime value={issue.regressed_at} />
          )}
        </MetaField>
        <MetaField label={t('issues.detail.environment')}>
          {issue.last_environment ?? '-'}
        </MetaField>
      </dl>

      {issue.resolved_in_release !== null &&
        issue.resolved_in_release !== '' && (
          <p
            className="m-0 rounded-md border border-primary/30 bg-primary/5 px-3 py-2 text-sm"
            data-testid="issue-reopen-note"
          >
            {t('issues.detail.reopenNote', {
              release: issue.resolved_in_release,
            })}
          </p>
        )}
    </Card>
  );
}
