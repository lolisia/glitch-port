import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  bulkUpdateIssues,
  getProject,
  getStats,
  listIssues,
  listLabels,
} from '../../api/client';
import type {
  BulkAction,
  Issue,
  StatsResponse,
} from '../../api/types';
import { navigate, useHashRoute } from '../../router';
import { useI18n } from '../../i18n';
import { EmptyState, ErrorState, LoadingState } from '../../ui';
import { BulkActionBar } from './components/BulkActionBar';
import { IssueFilterBar } from './components/IssueFilterBar';
import { IssueTable } from './components/IssueTable';
import { NoProjectState } from './components/NoProjectState';
import { StatsSummary } from './components/StatsSummary';
import {
  SEARCH_DEBOUNCE_MS,
  buildIssueFilterParams,
  buildIssueListQuery,
  collectFilterOptions,
  readIssueFilters,
} from './components/filters';
import type { IssueFilters } from './components/filters';
import './issues.css';

/**
 * Issue list with URL-driven filters, cursor pagination and bulk actions.
 *
 * All filter state lives in the hash query so a refresh or a shared link keeps
 * the current view. Changing a filter navigates with `replace` and the route
 * query change is what triggers the list request; the cursor never reaches the
 * URL.
 *
 * The project comes from the dashboard's active project selection. Without one
 * the page renders a "No project selected" state instead of calling any
 * project-scoped endpoint.
 */
export function IssuesPage({ projectId }: { projectId: number | null }) {
  const route = useHashRoute();
  const { t } = useI18n();
  // Memoized so array filters keep a stable identity between renders.
  const filters = useMemo(() => readIssueFilters(route.query), [route.query]);
  const { status, level, environment, release, label, platform, q, sort } =
    filters;

  const [items, setItems] = useState<Issue[]>([]);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [listError, setListError] = useState<unknown>(null);
  const [moreError, setMoreError] = useState<unknown>(null);
  const [selection, setSelection] = useState<ReadonlySet<number>>(
    () => new Set<number>(),
  );
  const [labelNames, setLabelNames] = useState<string[]>([]);
  const [platformOptions, setPlatformOptions] = useState<string[]>([]);
  const [search, setSearch] = useState(q);
  const [labelName, setLabelName] = useState('');
  const [bulkError, setBulkError] = useState<unknown>(null);
  const [bulkPending, setBulkPending] = useState(false);
  const [stats, setStats] = useState<StatsResponse | null>(null);

  // The counter discards responses that belong to a superseded filter set.
  const requestIdRef = useRef(0);
  // Event handlers and the debounce timer must merge into the latest filters,
  // not the ones captured when they were created.
  const filtersRef = useRef<IssueFilters>(filters);

  useEffect(() => {
    filtersRef.current = filters;
  });

  useEffect(() => {
    if (projectId === null) {
      setStats(null);
      return;
    }

    let cancelled = false;

    setStats(null);

    getStats(projectId, '24h')
      .then((response) => {
        if (!cancelled) {
          setStats({
            buckets: Array.isArray(response.buckets) ? response.buckets : [],
            by_platform: Array.isArray(response.by_platform)
              ? response.by_platform
              : [],
          });
        }
      })
      .catch(() => {
        // The 24h summary is decorative; a failure must not block the list.
      });

    return () => {
      cancelled = true;
    };
  }, [projectId]);

  useEffect(() => {
    if (projectId === null) {
      setPlatformOptions([]);
      return;
    }

    let cancelled = false;

    setPlatformOptions([]);

    // The platform filter only offers platforms the project has actually seen.
    getProject(projectId)
      .then((project) => {
        if (!cancelled) {
          setPlatformOptions(
            Array.isArray(project.platforms) ? project.platforms : [],
          );
        }
      })
      .catch(() => {
        // Without the platform list the filter stays hidden; the list works.
        if (!cancelled) {
          setPlatformOptions([]);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [projectId]);

  useEffect(() => {
    if (projectId === null) {
      setLabelNames([]);
      return;
    }

    let cancelled = false;

    listLabels(projectId)
      .then((labels) => {
        if (!cancelled) {
          setLabelNames(
            Array.isArray(labels) ? labels.map((item) => item.name) : [],
          );
        }
      })
      .catch(() => {
        // Without the project labels the filter still offers "All labels".
      });

    return () => {
      cancelled = true;
    };
  }, [projectId]);

  const loadFirstPage = useCallback(
    async (active: IssueFilters) => {
      if (projectId === null) {
        return;
      }

      const requestId = ++requestIdRef.current;

      setItems([]);
      setNextCursor(null);
      setSelection(new Set<number>());
      setLoading(true);
      setLoadingMore(false);
      setListError(null);
      setMoreError(null);
      setBulkError(null);

      try {
        const response = await listIssues(
          projectId,
          buildIssueListQuery(active),
        );

        if (requestId !== requestIdRef.current) {
          return;
        }

        setItems(Array.isArray(response.items) ? response.items : []);
        setNextCursor(response.next_cursor ?? null);
      } catch (error) {
        if (requestId !== requestIdRef.current) {
          return;
        }

        setListError(error);
      } finally {
        if (requestId === requestIdRef.current) {
          setLoading(false);
        }
      }
    },
    [projectId],
  );

  useEffect(() => {
    if (projectId === null) {
      return;
    }

    void loadFirstPage({
      status,
      level,
      environment,
      release,
      label,
      platform,
      q,
      sort,
    });
  }, [
    loadFirstPage,
    projectId,
    status,
    level,
    environment,
    release,
    label,
    platform,
    q,
    sort,
  ]);

  const loadMore = useCallback(
    async (active: IssueFilters, cursor: string) => {
      if (projectId === null) {
        return;
      }

      const requestId = requestIdRef.current;

      setLoadingMore(true);
      setMoreError(null);

      try {
        const response = await listIssues(
          projectId,
          buildIssueListQuery(active, cursor),
        );

        if (requestId !== requestIdRef.current) {
          return;
        }

        setItems((previous) => [
          ...previous,
          ...(Array.isArray(response.items) ? response.items : []),
        ]);
        setNextCursor(response.next_cursor ?? null);
      } catch (error) {
        if (requestId === requestIdRef.current) {
          setMoreError(error);
        }
      } finally {
        if (requestId === requestIdRef.current) {
          setLoadingMore(false);
        }
      }
    },
    [projectId],
  );

  const runBulk = useCallback(
    async (action: BulkAction, labels?: string[]) => {
      const ids = Array.from(selection);

      if (ids.length === 0) {
        return;
      }

      const requestId = requestIdRef.current;
      setBulkPending(true);
      setBulkError(null);

      try {
        await bulkUpdateIssues(
          labels === undefined ? { ids, action } : { ids, action, labels },
        );

        setSelection(new Set<number>());
        setLabelName('');

        // A filter change while the request was in flight has already loaded
        // a fresh first page, so only refetch when the view is unchanged.
        if (requestId === requestIdRef.current) {
          await loadFirstPage(filters);
        }
      } catch (error) {
        setBulkError(error);
      } finally {
        setBulkPending(false);
      }
    },
    [filters, loadFirstPage, selection],
  );

  const handleBulkAction = (action: BulkAction) => {
    if (action === 'delete') {
      const confirmed = window.confirm(
        t('issues.bulk.confirmDelete', { count: selection.size }),
      );

      if (!confirmed) {
        return;
      }
    }

    void runBulk(action);
  };

  const handleLabelAction = (action: 'add_labels' | 'remove_labels') => {
    const name = labelName.trim();

    if (name === '') {
      return;
    }

    void runBulk(action, [name]);
  };

  const toggleSelection = (id: number) => {
    setSelection((previous) => {
      const next = new Set(previous);

      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }

      return next;
    });
  };

  const toggleAll = () => {
    setSelection((previous) => {
      if (items.length > 0 && items.every((item) => previous.has(item.id))) {
        return new Set<number>();
      }

      return new Set(items.map((item) => item.id));
    });
  };

  useEffect(() => {
    setSearch(q);
  }, [q]);

  const applyFilterPatch = useCallback((patch: Partial<IssueFilters>) => {
    navigate(
      '/issues',
      buildIssueFilterParams({ ...filtersRef.current, ...patch }),
      { replace: true },
    );
  }, []);

  useEffect(() => {
    const trimmed = search.trim();

    if (trimmed === q) {
      return;
    }

    const timer = window.setTimeout(() => {
      applyFilterPatch({ q: trimmed });
    }, SEARCH_DEBOUNCE_MS);

    return () => window.clearTimeout(timer);
  }, [applyFilterPatch, q, search]);

  const environmentOptions = useMemo(
    () =>
      collectFilterOptions(
        items.map((item) => item.last_environment),
        environment,
      ),
    [environment, items],
  );

  const releaseOptions = useMemo(
    () => collectFilterOptions(items.map((item) => item.last_release), release),
    [items, release],
  );

  if (projectId === null) {
    return (
      <section className="issues-page" data-testid="issues-page">
        <header className="page-header">
          <h2>{t('issues.title')}</h2>
        </header>

        <NoProjectState />
      </section>
    );
  }

  return (
    <section className="issues-page" data-testid="issues-page">
      <header className="page-header">
        <h2>{t('issues.title')}</h2>
      </header>

      {stats !== null && <StatsSummary stats={stats} />}

      <IssueFilterBar
        filters={filters}
        search={search}
        labelNames={labelNames}
        environmentOptions={environmentOptions}
        releaseOptions={releaseOptions}
        platformOptions={platformOptions}
        onFilterChange={applyFilterPatch}
        onSearchChange={setSearch}
      />

      {selection.size > 0 && (
        <BulkActionBar
          count={selection.size}
          labelName={labelName}
          pending={bulkPending}
          error={bulkError}
          onLabelNameChange={setLabelName}
          onAction={handleBulkAction}
          onLabelAction={handleLabelAction}
        />
      )}

      {loading && <LoadingState />}

      {!loading && listError !== null && (
        <ErrorState error={listError} prefix={t('issues.loadError')} />
      )}

      {!loading && listError === null && items.length === 0 && (
        <EmptyState>{t('issues.empty')}</EmptyState>
      )}

      {!loading && listError === null && items.length > 0 && (
        <>
          <IssueTable
            issues={items}
            selection={selection}
            onToggle={toggleSelection}
            onToggleAll={toggleAll}
          />

          {moreError !== null && (
            <ErrorState error={moreError} prefix={t('issues.loadError')} />
          )}

          {nextCursor !== null && (
            <button
              type="button"
              className="issues-load-more"
              onClick={() => void loadMore(filters, nextCursor)}
              disabled={loading || loadingMore}
            >
              {t('issues.loadMore')}
            </button>
          )}
        </>
      )}
    </section>
  );
}
