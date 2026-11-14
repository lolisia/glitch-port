import type { EventDetail } from '../../../api/types';
import { EventView } from '../../../event/EventView';
import { EmptyState } from '../../../ui';

interface EventPanelProps {
  event: EventDetail | null;
  deleting: boolean;
  onDelete: () => void;
}

/** The selected event body, or an empty state when the issue has no events. */
export function EventPanel({ event, deleting, onDelete }: EventPanelProps) {
  return (
    <section
      className="issue-event-panel"
      data-testid="issue-event-panel"
      aria-labelledby="selected-event-heading"
    >
      <div className="event-panel-header">
        <h3 id="selected-event-heading">Selected event</h3>
        <button
          type="button"
          className="button-danger"
          onClick={onDelete}
          disabled={event === null || deleting}
        >
          Delete event
        </button>
      </div>

      {event === null ? (
        <EmptyState>No events recorded.</EmptyState>
      ) : (
        <EventView event={event} />
      )}
    </section>
  );
}
