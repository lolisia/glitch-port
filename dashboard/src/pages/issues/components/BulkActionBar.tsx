import type { BulkAction } from '../../../api/types';
import { Button } from '../../../components/ui/button';
import { Card } from '../../../components/ui/card';
import { Input } from '../../../components/ui/input';
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
    <Card
      role="group"
      aria-label={t('issues.bulk.actions')}
      className="flex-row flex-wrap items-center gap-2 p-3"
    >
      <span className="font-semibold">
        {t('issues.bulk.selected', { count })}
      </span>

      <div className="flex flex-wrap items-center gap-1.5">
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => onAction('resolve')}
          disabled={pending}
        >
          {t('issues.bulk.resolve')}
        </Button>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => onAction('resolve_next_release')}
          disabled={pending}
        >
          {t('issues.bulk.resolveNextRelease')}
        </Button>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => onAction('unresolve')}
          disabled={pending}
        >
          {t('issues.bulk.unresolve')}
        </Button>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => onAction('ignore')}
          disabled={pending}
        >
          {t('issues.bulk.ignore')}
        </Button>

        <Input
          type="text"
          aria-label={t('issues.bulk.labelName')}
          placeholder={t('issues.bulk.labelName')}
          value={labelName}
          onChange={(event) => onLabelNameChange(event.target.value)}
          className="h-8 w-40"
        />
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => onLabelAction('add_labels')}
          disabled={pending || !hasLabelName}
        >
          {t('issues.bulk.addLabel')}
        </Button>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => onLabelAction('remove_labels')}
          disabled={pending || !hasLabelName}
        >
          {t('issues.bulk.removeLabel')}
        </Button>

        <Button
          type="button"
          variant="destructive"
          size="sm"
          onClick={() => onAction('delete')}
          disabled={pending}
        >
          {t('issues.bulk.delete')}
        </Button>
      </div>

      {error !== null && (
        <ErrorState error={error} prefix={t('issues.bulk.error')} />
      )}
    </Card>
  );
}
