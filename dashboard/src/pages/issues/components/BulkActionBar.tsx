import type { BulkAction } from '../../../api/types';
import { ErrorState } from '../../../ui';

export interface BulkActionBarProps {
  count: number;
  labelName: string;
  pending: boolean;
  error: unknown;
  onLabelNameChange: (value: string) => void;
  onAction: (action: BulkAction) => void;
  onLabelAction: (action: 'add_labels' | 'remove_labels') => void;
}

/**
 * Actions for the current row selection. Destructive confirmation, API calls
 * and refetching are handled by the page; this bar only reports intent.
 */
export function BulkActionBar({
  count,
  labelName,
  pending,
  error,
  onLabelNameChange,
  onAction,
  onLabelAction,
}: BulkActionBarProps) {
  const hasLabelName = labelName.trim() !== '';

  return (
    <div className="bulk-bar" role="group" aria-label="Bulk actions">
      <span className="bulk-count">{count} selected</span>

      <div className="bulk-actions">
        <button
          type="button"
          onClick={() => onAction('resolve')}
          disabled={pending}
        >
          Resolve
        </button>
        <button
          type="button"
          onClick={() => onAction('unresolve')}
          disabled={pending}
        >
          Unresolve
        </button>
        <button
          type="button"
          onClick={() => onAction('ignore')}
          disabled={pending}
        >
          Ignore
        </button>

        <input
          type="text"
          aria-label="Label name"
          placeholder="Label name"
          value={labelName}
          onChange={(event) => onLabelNameChange(event.target.value)}
        />
        <button
          type="button"
          onClick={() => onLabelAction('add_labels')}
          disabled={pending || !hasLabelName}
        >
          Add label
        </button>
        <button
          type="button"
          onClick={() => onLabelAction('remove_labels')}
          disabled={pending || !hasLabelName}
        >
          Remove label
        </button>

        <button
          type="button"
          className="bulk-delete"
          onClick={() => onAction('delete')}
          disabled={pending}
        >
          Delete
        </button>
      </div>

      {error !== null && <ErrorState error={error} prefix="Bulk action failed" />}
    </div>
  );
}
