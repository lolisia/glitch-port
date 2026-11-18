import type { IssueStatus } from '../../../api/types';
import { useI18n } from '../../../i18n';

const STATUS_ACTIONS: ReadonlyArray<{ labelKey: string; status: IssueStatus }> =
  [
    { labelKey: 'issues.detail.resolve', status: 'resolved' },
    { labelKey: 'issues.detail.unresolve', status: 'unresolved' },
    { labelKey: 'issues.detail.ignore', status: 'ignored' },
  ];

interface IssueActionsProps {
  onChangeStatus: (status: IssueStatus) => void;
  onDelete: () => void;
}

/**
 * Status changes stay available regardless of the current status so a stale
 * StatusBadge never traps the user in one state.
 */
export function IssueActions({ onChangeStatus, onDelete }: IssueActionsProps) {
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
      <button type="button" className="button-danger" onClick={onDelete}>
        {t('issues.detail.deleteIssue')}
      </button>
    </div>
  );
}
