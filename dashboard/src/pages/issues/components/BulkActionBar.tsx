import type { BulkAction } from '../../../api/types';
import { useI18n } from '../../../i18n';
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
  const { t } = useI18n();
  const hasLabelName = labelName.trim() !== '';

  return (
    <div className="bulk-bar" role="group" aria-label={t('issues.bulk.actions')}>
      <span className="bulk-count">
        {t('issues.bulk.selected', { count })}
      </span>

      <div className="bulk-actions">
        <button
          type="button"
          onClick={() => onAction('resolve')}
          disabled={pending}
        >
          {t('issues.bulk.resolve')}
        </button>
        <button
          type="button"
          onClick={() => onAction('resolve_next_release')}
          disabled={pending}
        >
          {t('issues.bulk.resolveNextRelease')}
        </button>
        <button
          type="button"
          onClick={() => onAction('unresolve')}
          disabled={pending}
        >
          {t('issues.bulk.unresolve')}
        </button>
        <button
          type="button"
          onClick={() => onAction('ignore')}
          disabled={pending}
        >
          {t('issues.bulk.ignore')}
        </button>

        <input
          type="text"
          aria-label={t('issues.bulk.labelName')}
          placeholder={t('issues.bulk.labelName')}
          value={labelName}
          onChange={(event) => onLabelNameChange(event.target.value)}
        />
        <button
          type="button"
          onClick={() => onLabelAction('add_labels')}
          disabled={pending || !hasLabelName}
        >
          {t('issues.bulk.addLabel')}
        </button>
        <button
          type="button"
          onClick={() => onLabelAction('remove_labels')}
          disabled={pending || !hasLabelName}
        >
          {t('issues.bulk.removeLabel')}
        </button>

        <button
          type="button"
          className="bulk-delete"
          onClick={() => onAction('delete')}
          disabled={pending}
        >
          {t('issues.bulk.delete')}
        </button>
      </div>

      {error !== null && (
        <ErrorState error={error} prefix={t('issues.bulk.error')} />
      )}
    </div>
  );
}
