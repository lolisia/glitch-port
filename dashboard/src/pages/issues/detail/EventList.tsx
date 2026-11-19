import type { EventSummary } from '../../../api/types';
import { useI18n } from '../../../i18n';
import { LevelBadge, RelativeTime } from '../../../ui';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import {
  Table,
  TableBody,
  TableCell,
  TableRow,
} from '@/components/ui/table';
import { cn } from '@/lib/utils';

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

function messageText(
  message: string | null | undefined,
  fallback: string,
): string {
  return message === null || message === undefined || message.trim() === ''
    ? fallback
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
  const { t } = useI18n();

  return (
    <Card
      className="gap-3 p-4"
      data-testid="issue-event-list"
      aria-labelledby="issue-events-heading"
    >
      <div className="flex items-center justify-between gap-2">
        <h3 id="issue-events-heading" className="m-0 text-base font-semibold">
          {t('issues.detail.eventList.heading')}
        </h3>
        <div className="flex gap-1">
          <Button
            type="button"
            variant="outline"
            size="xs"
            onClick={onNewer}
            disabled={newerDisabled}
          >
            {t('issues.detail.eventList.newer')}
          </Button>
          <Button
            type="button"
            variant="outline"
            size="xs"
            onClick={onOlder}
            disabled={olderDisabled}
          >
            {t('issues.detail.eventList.older')}
          </Button>
        </div>
      </div>

      {events.length > 0 && (
        <Table aria-labelledby="issue-events-heading">
          <TableBody>
            {events.map((event) => {
              const selected = selectedId === event.id;

              return (
                <TableRow
                  key={event.id}
                  data-state={selected ? 'selected' : undefined}
                  className="border-border/60 hover:bg-muted/50 data-[state=selected]:bg-muted"
                >
                  <TableCell className="p-0 align-top">
                    <Button
                      type="button"
                      variant="ghost"
                      aria-current={selected ? 'true' : undefined}
                      onClick={() => onSelect(event)}
                      className={cn(
                        'h-auto w-full flex-col items-start gap-1 rounded-md px-2 py-1.5 text-left whitespace-normal',
                        selected && 'bg-muted',
                      )}
                    >
                      <span className="text-sm font-semibold break-words">
                        {messageText(
                          event.message,
                          t('issues.detail.eventList.noMessage'),
                        )}
                      </span>
                      <span className="flex flex-wrap items-center gap-1.5 text-xs font-normal text-muted-foreground">
                        <RelativeTime value={displayedTime(event)} />
                        <LevelBadge level={event.level} />
                        {event.environment ? (
                          <span className="rounded-full bg-muted px-1.5 py-0.5">
                            {event.environment}
                          </span>
                        ) : null}
                        {event.release ? (
                          <span className="rounded-full bg-muted px-1.5 py-0.5">
                            {event.release}
                          </span>
                        ) : null}
                      </span>
                    </Button>
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      )}

      {nextCursor !== null && (
        <Button
          type="button"
          variant="outline"
          size="sm"
          className="self-start"
          onClick={onLoadMore}
          disabled={loadingMore}
        >
          {t('issues.detail.eventList.more')}
        </Button>
      )}
    </Card>
  );
}
