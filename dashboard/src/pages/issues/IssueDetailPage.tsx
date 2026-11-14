import { useCallback, useEffect, useRef, useState } from 'react';
import {
  deleteEvent,
  deleteIssue,
  getEvent,
  getIssue,
  listIssueEvents,
  listLabels,
  updateIssue,
} from '../../api/client';
import type {
  EventDetail,
  EventSummary,
  Issue,
  IssueDetail,
  IssueStatus,
  Label,
} from '../../api/types';
import { navigate } from '../../router';
import { ErrorState, LoadingState } from '../../ui';
import { EventList } from './detail/EventList';
import { EventPanel } from './detail/EventPanel';
import { IssueActions } from './detail/IssueActions';
import { IssueHeader } from './detail/IssueHeader';
import { LabelsEditor } from './detail/LabelsEditor';
import './issue-detail.css';

const EVENTS_PAGE_SIZE = 25;

interface LoadedIssue {
  issue: IssueDetail;
  /** Labels available in the project, used for the checkbox list. */
  labels: Label[];
  /** Events for the current page, newest first as returned by the API. */
  events: EventSummary[];
  nextCursor: string | null;
}

type IssueState =
  | { status: 'loading' }
  | { status: 'error'; error: unknown }
  | { status: 'ready'; loaded: LoadedIssue };

interface InlineError {
  prefix: string;
  error: unknown;
}

/**
 * Copies an Issue returned by a mutation into the loaded detail while keeping
 * latest_event, which only GET /issues/{id} returns.
 */
function mergeIssue(
  loaded: LoadedIssue,
  updated: Issue,
  labels: Label[] = loaded.labels,
): LoadedIssue {
  return {
    ...loaded,
    labels,
    issue: {
      ...loaded.issue,
      ...updated,
      latest_event: loaded.issue.latest_event,
    },
  };
}

/**
 * PATCH /issues/{id} creates project labels for names that do not exist yet and
 * returns every label attached to the issue. Merging that response into the
 * checkbox source keeps those labels selectable instead of dropping them on the
 * next save.
 */
function mergeLabels(base: Label[], extra: Label[]): Label[] {
  const merged = [...base];
  const seen = new Set(base.map((label) => label.name));

  for (const label of extra) {
    if (!seen.has(label.name)) {
      seen.add(label.name);
      merged.push(label);
    }
  }

  return merged;
}

export function IssueDetailPage({ issueId }: { issueId: number }) {
  const [state, setState] = useState<IssueState>({ status: 'loading' });
  const [selected, setSelected] = useState<EventDetail | null>(null);
  const [checkedNames, setCheckedNames] = useState<string[]>([]);
  const [newNames, setNewNames] = useState('');
  const [savingLabels, setSavingLabels] = useState(false);
  const [actionError, setActionError] = useState<unknown>(null);
  const [eventError, setEventError] = useState<InlineError | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);
  const [deletingEvent, setDeletingEvent] = useState(false);
  // Selecting events quickly must not let an earlier response win.
  const selectedRequest = useRef(0);
  // Mutations capture this token so a response for the previous issue cannot
  // be merged into the newly loaded issue.
  const issueToken = useRef(0);

  useEffect(() => {
    const token = issueToken.current + 1;
    issueToken.current = token;
    selectedRequest.current += 1;

    setState({ status: 'loading' });
    setSelected(null);
    setCheckedNames([]);
    setNewNames('');
    setSavingLabels(false);
    setActionError(null);
    setEventError(null);
    setLoadingMore(false);
    setDeletingEvent(false);

    async function load() {
      try {
        const [issue, page, labels] = await Promise.all([
          getIssue(issueId),
          listIssueEvents(issueId, { limit: EVENTS_PAGE_SIZE }),
          listLabels(),
        ]);

        if (issueToken.current !== token) {
          return;
        }

        setState({
          status: 'ready',
          loaded: {
            issue,
            labels,
            events: page.items,
            nextCursor: page.next_cursor,
          },
        });
        setSelected(issue.latest_event);
        setCheckedNames(issue.labels.map((label) => label.name));
      } catch (error) {
        if (issueToken.current !== token) {
          return;
        }

        setState({ status: 'error', error });
      }
    }

    void load();

    return () => {
      // Invalidate in-flight loads and mutations for this issue.
      if (issueToken.current === token) {
        issueToken.current = token + 1;
      }
    };
  }, [issueId]);

  const changeStatus = useCallback(
    async (status: IssueStatus) => {
      const token = issueToken.current;
      setActionError(null);

      try {
        const updated = await updateIssue(issueId, { status });

        if (issueToken.current !== token) {
          return;
        }

        setState((current) =>
          current.status === 'ready'
            ? {
                status: 'ready',
                loaded: mergeIssue(current.loaded, updated),
              }
            : current,
        );
      } catch (error) {
        if (issueToken.current !== token) {
          return;
        }

        setActionError(error);
      }
    },
    [issueId],
  );

  const handleDeleteIssue = useCallback(async () => {
    if (!window.confirm('Delete this issue and all of its events?')) {
      return;
    }

    setActionError(null);

    try {
      await deleteIssue(issueId);
      navigate('/issues');
    } catch (error) {
      setActionError(error);
    }
  }, [issueId]);

  const toggleLabel = useCallback((name: string, checked: boolean) => {
    setCheckedNames((current) => {
      if (checked) {
        return current.includes(name) ? current : [...current, name];
      }

      return current.filter((item) => item !== name);
    });
  }, []);

  const saveLabels = useCallback(async () => {
    if (state.status !== 'ready') {
      return;
    }

    const token = issueToken.current;
    const names: string[] = [];
    const seen = new Set<string>();

    function addName(raw: string) {
      const name = raw.trim();
      if (name === '' || seen.has(name)) {
        return;
      }

      seen.add(name);
      names.push(name);
    }

    // Project labels keep their configured order. Current issue labels missing
    // from that list are preserved next so a full-list PATCH never silently
    // drops a label the UI has no checkbox for.
    for (const label of state.loaded.labels) {
      if (checkedNames.includes(label.name)) {
        addName(label.name);
      }
    }

    for (const label of state.loaded.issue.labels) {
      if (checkedNames.includes(label.name)) {
        addName(label.name);
      }
    }

    // Typed names are created by the API and follow the checked labels.
    for (const raw of newNames.split(',')) {
      addName(raw);
    }

    setSavingLabels(true);
    setActionError(null);

    try {
      const updated = await updateIssue(issueId, { labels: names });

      if (issueToken.current !== token) {
        return;
      }

      setState((current) =>
        current.status === 'ready'
          ? {
              status: 'ready',
              loaded: mergeIssue(
                current.loaded,
                updated,
                mergeLabels(current.loaded.labels, updated.labels),
              ),
            }
          : current,
      );
      setCheckedNames(updated.labels.map((label) => label.name));
      setNewNames('');
    } catch (error) {
      if (issueToken.current !== token) {
        return;
      }

      setActionError(error);
    } finally {
      if (issueToken.current === token) {
        setSavingLabels(false);
      }
    }
  }, [checkedNames, issueId, newNames, state]);

  const loadMoreEvents = useCallback(async () => {
    if (state.status !== 'ready' || state.loaded.nextCursor === null) {
      return;
    }

    const token = issueToken.current;
    const cursor = state.loaded.nextCursor;
    setLoadingMore(true);
    setEventError(null);

    try {
      const page = await listIssueEvents(issueId, {
        limit: EVENTS_PAGE_SIZE,
        cursor,
      });

      if (issueToken.current !== token) {
        return;
      }

      setState((current) =>
        current.status === 'ready'
          ? {
              status: 'ready',
              loaded: {
                ...current.loaded,
                events: [...current.loaded.events, ...page.items],
                nextCursor: page.next_cursor,
              },
            }
          : current,
      );
    } catch (error) {
      if (issueToken.current !== token) {
        return;
      }

      setEventError({ prefix: 'Failed to load events', error });
    } finally {
      if (issueToken.current === token) {
        setLoadingMore(false);
      }
    }
  }, [issueId, state]);

  const selectEvent = useCallback(async (item: EventSummary) => {
    const token = issueToken.current;
    const requestId = selectedRequest.current + 1;
    selectedRequest.current = requestId;
    setEventError(null);

    try {
      const event = await getEvent(item.id);

      if (
        issueToken.current !== token ||
        selectedRequest.current !== requestId
      ) {
        return;
      }

      setSelected(event);
    } catch (error) {
      if (
        issueToken.current !== token ||
        selectedRequest.current !== requestId
      ) {
        return;
      }

      setEventError({ prefix: 'Failed to load event', error });
    }
  }, []);

  const deleteSelectedEvent = useCallback(async () => {
    if (selected === null) {
      return;
    }

    if (!window.confirm('Delete this event?')) {
      return;
    }

    const token = issueToken.current;
    setDeletingEvent(true);
    setEventError(null);

    try {
      await deleteEvent(selected.id);
    } catch (error) {
      if (issueToken.current !== token) {
        return;
      }

      setEventError({ prefix: 'Failed to delete event', error });
      setDeletingEvent(false);
      return;
    }

    if (issueToken.current !== token) {
      return;
    }

    try {
      // The deleted event may have been the latest one, so reload both the
      // issue summary and the first page before choosing the new selection.
      const [issue, page] = await Promise.all([
        getIssue(issueId),
        listIssueEvents(issueId, { limit: EVENTS_PAGE_SIZE }),
      ]);

      if (issueToken.current !== token) {
        return;
      }

      setState((current) =>
        current.status === 'ready'
          ? {
              status: 'ready',
              loaded: {
                ...current.loaded,
                issue,
                events: page.items,
                nextCursor: page.next_cursor,
              },
            }
          : current,
      );
      setSelected(issue.latest_event);
      setCheckedNames(issue.labels.map((label) => label.name));
    } catch (error) {
      if (issueToken.current !== token) {
        return;
      }

      setEventError({ prefix: 'Failed to load event', error });
    } finally {
      if (issueToken.current === token) {
        setDeletingEvent(false);
      }
    }
  }, [issueId, selected]);

  const loaded = state.status === 'ready' ? state.loaded : null;
  const events = loaded?.events ?? [];
  const selectedIndex =
    selected === null
      ? -1
      : events.findIndex((event) => event.id === selected.id);
  const newerDisabled = selectedIndex <= 0;
  const olderDisabled =
    selectedIndex === -1 || selectedIndex >= events.length - 1;

  function moveSelection(offset: number) {
    if (selectedIndex === -1) {
      return;
    }

    const target = events[selectedIndex + offset];
    if (target !== undefined) {
      void selectEvent(target);
    }
  }

  return (
    <section className="issue-detail-page" data-testid="issue-detail-page">
      {state.status === 'loading' && <LoadingState />}

      {state.status === 'error' && (
        <ErrorState error={state.error} prefix="Failed to load issue" />
      )}

      {loaded !== null && (
        <>
          <IssueHeader issue={loaded.issue} />

          {actionError !== null && (
            <ErrorState error={actionError} prefix="Failed to update issue" />
          )}

          <IssueActions
            onChangeStatus={(status) => void changeStatus(status)}
            onDelete={() => void handleDeleteIssue()}
          />

          <LabelsEditor
            projectLabels={loaded.labels}
            issueLabels={loaded.issue.labels}
            checkedNames={checkedNames}
            newNames={newNames}
            saving={savingLabels}
            onToggle={toggleLabel}
            onNewNamesChange={setNewNames}
            onSave={() => void saveLabels()}
          />

          <div className="issue-events">
            {eventError !== null && (
              <ErrorState
                error={eventError.error}
                prefix={eventError.prefix}
              />
            )}

            <div className="issue-events-grid">
              <EventList
                events={events}
                selectedId={selected?.id ?? null}
                nextCursor={loaded.nextCursor}
                loadingMore={loadingMore}
                newerDisabled={newerDisabled}
                olderDisabled={olderDisabled}
                onSelect={(event) => void selectEvent(event)}
                onNewer={() => moveSelection(-1)}
                onOlder={() => moveSelection(1)}
                onLoadMore={() => void loadMoreEvents()}
              />

              <EventPanel
                event={selected}
                deleting={deletingEvent}
                onDelete={() => void deleteSelectedEvent()}
              />
            </div>
          </div>
        </>
      )}
    </section>
  );
}
