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
  UpdateIssueRequest,
} from '../../api/types';
import { navigate } from '../../router';
import { useI18n } from '../../i18n';
import { ErrorState, LoadingState } from '../../ui';
import { EventList } from './detail/EventList';
import { EventPanel } from './detail/EventPanel';
import { IssueActions } from './detail/IssueActions';
import { IssueHeader } from './detail/IssueHeader';
import { LabelsEditor } from './detail/LabelsEditor';
import { TagFacets } from './detail/TagFacets';
import { NoProjectState } from './components/NoProjectState';
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
  /** Dotted message key for the failed action; resolved at render time. */
  prefixKey: string;
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

export function IssueDetailPage({
  issueId,
  projectId,
}: {
  issueId: number;
  projectId: number | null;
}) {
  const [state, setState] = useState<IssueState>({ status: 'loading' });
  const { t } = useI18n();
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

    // The issue itself is not project scoped, but the label list is: without
    // an active project the page waits instead of guessing one.
    const activeProjectId = projectId;

    if (activeProjectId === null) {
      return () => {
        if (issueToken.current === token) {
          issueToken.current = token + 1;
        }
      };
    }

    async function load(activeProjectIdForLabels: number) {
      try {
        const [issue, page, labels] = await Promise.all([
          getIssue(issueId),
          listIssueEvents(issueId, { limit: EVENTS_PAGE_SIZE }),
          listLabels(activeProjectIdForLabels),
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

    void load(activeProjectId);

    return () => {
      // Invalidate in-flight loads and mutations for this issue.
      if (issueToken.current === token) {
        issueToken.current = token + 1;
      }
    };
  }, [issueId, projectId]);

  const changeStatus = useCallback(
    async (
      status: IssueStatus,
      resolveIn?: UpdateIssueRequest['resolve_in'],
    ) => {
      const token = issueToken.current;
      setActionError(null);

      try {
        const patch: UpdateIssueRequest =
          resolveIn === undefined
            ? { status }
            : { status, resolve_in: resolveIn };
        const updated = await updateIssue(issueId, patch);

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
    if (!window.confirm(t('issues.detail.confirmDelete'))) {
      return;
    }

    setActionError(null);

    try {
      await deleteIssue(issueId);
      navigate('/issues');
    } catch (error) {
      setActionError(error);
    }
  }, [issueId, t]);

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

      setEventError({ prefixKey: 'issues.detail.errors.events', error });
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

      setEventError({ prefixKey: 'issues.detail.errors.event', error });
    }
  }, []);

  const deleteSelectedEvent = useCallback(async () => {
    if (selected === null) {
      return;
    }

    if (!window.confirm(t('issues.detail.eventPanel.confirmDelete'))) {
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

      setEventError({ prefixKey: 'issues.detail.errors.deleteEvent', error });
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

      setEventError({ prefixKey: 'issues.detail.errors.event', error });
    } finally {
      if (issueToken.current === token) {
        setDeletingEvent(false);
      }
    }
  }, [issueId, selected, t]);

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
      {projectId === null && <NoProjectState />}

      {projectId !== null && state.status === 'loading' && <LoadingState />}

      {projectId !== null && state.status === 'error' && (
        <ErrorState error={state.error} prefix={t('issues.detail.loadError')} />
      )}

      {projectId !== null && loaded !== null && (
        <>
          <IssueHeader issue={loaded.issue} />

          {actionError !== null && (
            <ErrorState
              error={actionError}
              prefix={t('issues.detail.updateError')}
            />
          )}

          <IssueActions
            lastRelease={loaded.issue.last_release}
            onChangeStatus={(status) => void changeStatus(status)}
            onResolveNextRelease={() =>
              void changeStatus('resolved', 'next_release')
            }
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

          <TagFacets issueId={issueId} />

          <div className="issue-events">
            {eventError !== null && (
              <ErrorState
                error={eventError.error}
                prefix={t(eventError.prefixKey)}
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
