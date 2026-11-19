import type { EventDetail } from '../../../api/types';
import { EventView } from '../../../event/EventView';
import { useI18n } from '../../../i18n';
import { EmptyState } from '../../../ui';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { PlatformChip } from '../../platform/PlatformChip';

interface EventPanelProps {
  event: EventDetail | null;
  deleting: boolean;
  onDelete: () => void;
}

/** The selected event body, or an empty state when the issue has no events. */
export function EventPanel({ event, deleting, onDelete }: EventPanelProps) {
  const { t } = useI18n();

  return (
    <Card
      className="min-w-0 gap-3 p-4"
      data-testid="issue-event-panel"
      aria-labelledby="selected-event-heading"
    >
      <div className="flex items-center justify-between gap-2">
        <h3 id="selected-event-heading" className="m-0 text-base font-semibold">
          {t('issues.detail.eventPanel.heading')}
        </h3>
        <Button
          type="button"
          variant="destructive"
          size="sm"
          onClick={onDelete}
          disabled={event === null || deleting}
        >
          {t('issues.detail.eventPanel.delete')}
        </Button>
      </div>

      {event === null ? (
        <EmptyState>{t('issues.detail.eventPanel.empty')}</EmptyState>
      ) : (
        <>
          <dl className="m-0 flex flex-wrap gap-x-4 gap-y-1">
            <div className="flex flex-col gap-0.5">
              <dt className="text-xs font-semibold tracking-wide text-muted-foreground uppercase">
                {t('issues.detail.eventPanel.platform')}
              </dt>
              <dd className="m-0">
                <PlatformChip platform={event.platform} />
              </dd>
            </div>
          </dl>
          <EventView event={event} />
        </>
      )}
    </Card>
  );
}
