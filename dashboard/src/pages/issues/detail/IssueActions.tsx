import type { IssueStatus } from '../../../api/types';

const STATUS_ACTIONS: ReadonlyArray<{ label: string; status: IssueStatus }> = [
  { label: 'Resolve', status: 'resolved' },
  { label: 'Unresolve', status: 'unresolved' },
  { label: 'Ignore', status: 'ignored' },
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
  return (
    <div className="issue-actions" role="group" aria-label="Issue actions">
      {STATUS_ACTIONS.map((action) => (
        <button
          key={action.status}
          type="button"
          onClick={() => onChangeStatus(action.status)}
        >
          {action.label}
        </button>
      ))}
      <button type="button" className="button-danger" onClick={onDelete}>
        Delete issue
      </button>
    </div>
  );
}
