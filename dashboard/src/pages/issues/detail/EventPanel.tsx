import type { EventDetail } from '../../../api/types';
import { EventView } from '../../../event/EventView';
import { useI18n } from '../../../i18n';
import { EmptyState } from '../../../ui';
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
    <section
      className="issue-event-panel"
      data-testid="issue-event-panel"
      aria-labelledby="selected-event-heading"
    >
      <div className="event-panel-header">
        <h3 id="selected-event-heading">
          {t('issues.detail.eventPanel.heading')}
        </h3>
        <button
          type="button"
          className="button-danger"
          onClick={onDelete}
          disabled={event === null || deleting}
        >
          {t('issues.detail.eventPanel.delete')}
        </button>
      </div>

      {event === null ? (
        <EmptyState>{t('issues.detail.eventPanel.empty')}</EmptyState>
      ) : (
        <>
          <dl className="event-panel-meta">
            <div>
              <dt>{t('issues.detail.eventPanel.platform')}</dt>
              <dd>
                <PlatformChip platform={event.platform} />
              </dd>
            </div>
          </dl>
          <EventView event={event} />
        </>
      )}
    </section>
  );
}
