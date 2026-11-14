import type { EventSummary } from '../../../api/types';
import { LevelBadge, RelativeTime } from '../../../ui';

const NO_MESSAGE = '<no message>';

interface EventListProps {
  /** Newest first, matching the Management API ordering. */
  events: EventSummary[];
  selectedId: string | null;
  nextCursor: string | null;
  loadingMore: boolean;
  newerDisabled: boolean;
  olderDisabled: boolean;
  onSelect: (event: EventSummary) => void;
  onNewer: () => void;
  onOlder: () => void;
  onLoadMore: () => void;
}

function messageText(message: string | null | undefined): string {
  return message === null || message === undefined || message.trim() === ''
    ? NO_MESSAGE
    : message;
}

function displayedTime(event: EventSummary): string {
  return event.timestamp ? event.timestamp : event.received_at;
}

export function EventList({
  events,
  selectedId,
  nextCursor,
  loadingMore,
  newerDisabled,
  olderDisabled,
  onSelect,
  onNewer,
  onOlder,
  onLoadMore,
}: EventListProps) {
  return (
    <section
      className="issue-event-list"
      data-testid="issue-event-list"
      aria-labelledby="issue-events-heading"
    >
      <div className="event-list-header">
        <h3 id="issue-events-heading">Event list</h3>
        <div className="event-nav">
          <button type="button" onClick={onNewer} disabled={newerDisabled}>
            Newer
          </button>
          <button type="button" onClick={onOlder} disabled={olderDisabled}>
            Older
          </button>
        </div>
      </div>

      {events.length > 0 && (
        <ul className="event-items">
          {events.map((event) => (
            <li key={event.id}>
              <button
                type="button"
                className="event-item"
                aria-current={selectedId === event.id ? 'true' : undefined}
                onClick={() => onSelect(event)}
              >
                <span className="event-item-message">
                  {messageText(event.message)}
                </span>
                <span className="event-item-meta">
                  <RelativeTime value={displayedTime(event)} />
                  <LevelBadge level={event.level} />
                  {event.environment ? (
                    <span className="event-item-context">
                      {event.environment}
                    </span>
                  ) : null}
                  {event.release ? (
                    <span className="event-item-context">{event.release}</span>
                  ) : null}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}

      {nextCursor !== null && (
        <button
          type="button"
          className="event-more"
          onClick={onLoadMore}
          disabled={loadingMore}
        >
          More events
        </button>
      )}
    </section>
  );
}
