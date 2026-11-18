import type { IssueStatus } from '../../../api/types';
import { useI18n } from '../../../i18n';

const STATUS_ACTIONS: ReadonlyArray<{ labelKey: string; status: IssueStatus }> =
  [
    { labelKey: 'issues.detail.resolve', status: 'resolved' },
    { labelKey: 'issues.detail.unresolve', status: 'unresolved' },
    { labelKey: 'issues.detail.ignore', status: 'ignored' },
  ];

interface IssueActionsProps {
  /** Value of the issue's last_release; next-release resolution needs one. */
  lastRelease: string | null;
  onChangeStatus: (status: IssueStatus) => void;
  onResolveNextRelease: () => void;
  onDelete: () => void;
}

/**
 * Status changes stay available regardless of the current status so a stale
 * StatusBadge never traps the user in one state. Resolving in the next release
 * pins the resolution to the release of the latest event, so it is disabled
 * while the issue has no recorded release.
 */
export function IssueActions({
  lastRelease,
  onChangeStatus,
  onResolveNextRelease,
  onDelete,
}: IssueActionsProps) {
  const { t } = useI18n();

  return (
    <div
      className="issue-actions"
      role="group"
      aria-label={t('issues.detail.actions')}
    >
      {STATUS_ACTIONS.map((action) => (
        <button
          key={action.status}
          type="button"
          onClick={() => onChangeStatus(action.status)}
        >
          {t(action.labelKey)}
        </button>
      ))}
      <button
        type="button"
        onClick={onResolveNextRelease}
        disabled={lastRelease === null}
        title={
          lastRelease === null
            ? t('issues.detail.resolveNextReleaseDisabled')
            : undefined
        }
      >
        {t('issues.detail.resolveNextRelease')}
      </button>
      <button type="button" className="button-danger" onClick={onDelete}>
        {t('issues.detail.deleteIssue')}
      </button>
    </div>
  );
}
