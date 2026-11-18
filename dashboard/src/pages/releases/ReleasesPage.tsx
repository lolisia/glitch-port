import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { listReleases } from '../../api/client';
import type { Release } from '../../api/types';
import { formatNumber, useI18n } from '../../i18n';
import { buildHash } from '../../router';
import { EmptyState, ErrorState, LoadingState, RelativeTime } from '../../ui';
import { NoProjectState } from '../issues/components/NoProjectState';
import './releases.css';

const RELEASES_PAGE_SIZE = 50;

/**
 * Hash of the issues list filtered to one release. The cell links use
 * `status=all` so a resolved or ignored issue of the release is not hidden by
 * the list's default unresolved filter.
 */
function issuesHash(
  param: 'release' | 'first_release',
  release: string,
): string {
  return buildHash(
    '/issues',
    new URLSearchParams({ [param]: release, status: 'all' }),
  );
}

/**
 * `#/releases`: the releases of the active project aggregated from stored
 * events.
 *
 * The environment filter is applied on submit so typing does not issue one
 * request per keystroke. The next page is loaded from the cursor returned with
 * the current rows.
 */
export function ReleasesPage({ projectId }: { projectId: number | null }) {
  const { locale, t } = useI18n();
  const [items, setItems] = useState<Release[]>([]);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [environment, setEnvironment] = useState('');
  const [environmentDraft, setEnvironmentDraft] = useState('');
  const [loading, setLoading] = useState(projectId !== null);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [moreError, setMoreError] = useState<unknown>(null);

  // A filter change invalidates the response of the previous request.
  const requestIdRef = useRef(0);

  useEffect(() => {
    if (projectId === null) {
      requestIdRef.current += 1;
      setItems([]);
      setNextCursor(null);
      setLoading(false);
      setLoadingMore(false);
      setError(null);
      setMoreError(null);
      return;
    }

    const requestId = ++requestIdRef.current;

    setItems([]);
    setNextCursor(null);
    setLoading(true);
    setLoadingMore(false);
    setError(null);
    setMoreError(null);

    listReleases(projectId, {
      environment: environment === '' ? undefined : environment,
      limit: RELEASES_PAGE_SIZE,
    })
      .then((page) => {
        if (requestId !== requestIdRef.current) {
          return;
        }

        setItems(Array.isArray(page.items) ? page.items : []);
        setNextCursor(page.next_cursor ?? null);
      })
      .catch((loadError) => {
        if (requestId === requestIdRef.current) {
          setError(loadError);
        }
      })
      .finally(() => {
        if (requestId === requestIdRef.current) {
          setLoading(false);
        }
      });
  }, [environment, projectId]);

  const loadMore = () => {
    if (projectId === null || nextCursor === null || loadingMore) {
      return;
    }

    const requestId = requestIdRef.current;

    setLoadingMore(true);
    setMoreError(null);

    listReleases(projectId, {
      environment: environment === '' ? undefined : environment,
      limit: RELEASES_PAGE_SIZE,
      cursor: nextCursor,
    })
      .then((page) => {
        if (requestId !== requestIdRef.current) {
          return;
        }

        setItems((previous) => [
          ...previous,
          ...(Array.isArray(page.items) ? page.items : []),
        ]);
        setNextCursor(page.next_cursor ?? null);
      })
      .catch((loadError) => {
        if (requestId === requestIdRef.current) {
          setMoreError(loadError);
        }
      })
      .finally(() => {
        if (requestId === requestIdRef.current) {
          setLoadingMore(false);
        }
      });
  };

  const applyEnvironment = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setEnvironment(environmentDraft.trim());
  };

  if (projectId === null) {
    return (
      <section className="releases-page" data-testid="releases-page">
        <header className="page-header">
          <h2>{t('releases.title')}</h2>
        </header>

        <NoProjectState />
      </section>
    );
  }

  return (
    <section className="releases-page" data-testid="releases-page">
      <header className="page-header">
        <h2>{t('releases.title')}</h2>
      </header>

      <form className="releases-filter" onSubmit={applyEnvironment}>
        <input
          type="search"
          aria-label={t('releases.environment')}
          placeholder={t('releases.environmentPlaceholder')}
          value={environmentDraft}
          onChange={(event) => setEnvironmentDraft(event.target.value)}
        />
        <button type="submit" disabled={loading}>
          {t('releases.apply')}
        </button>
      </form>

      {loading && <LoadingState />}

      {!loading && error !== null && (
        <ErrorState error={error} prefix={t('releases.loadError')} />
      )}

      {!loading && error === null && items.length === 0 && (
        <EmptyState>{t('releases.empty')}</EmptyState>
      )}

      {!loading && error === null && items.length > 0 && (
        <>
          <table className="releases-table">
            <thead>
              <tr>
                <th scope="col">{t('releases.table.release')}</th>
                <th scope="col">{t('releases.table.firstSeen')}</th>
                <th scope="col">{t('releases.table.lastSeen')}</th>
                <th scope="col">{t('releases.table.events')}</th>
                <th scope="col">{t('releases.table.issues')}</th>
                <th scope="col">{t('releases.table.newIssues')}</th>
                <th scope="col">{t('releases.table.unresolvedIssues')}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item) => (
                <tr key={item.release}>
                  <td className="releases-name">
                    <a href={issuesHash('release', item.release)}>
                      {item.release}
                    </a>
                  </td>
                  <td>
                    <RelativeTime value={item.first_seen} />
                  </td>
                  <td>
                    <RelativeTime value={item.last_seen} />
                  </td>
                  <td className="releases-number">
                    {formatNumber(item.event_count, locale)}
                  </td>
                  <td className="releases-number">
                    {formatNumber(item.issue_count, locale)}
                  </td>
                  <td className="releases-number">
                    <a href={issuesHash('first_release', item.release)}>
                      {formatNumber(item.new_issue_count, locale)}
                    </a>
                  </td>
                  <td className="releases-number">
                    {formatNumber(item.unresolved_issue_count, locale)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          {moreError !== null && (
            <ErrorState error={moreError} prefix={t('releases.loadError')} />
          )}

          {nextCursor !== null && (
            <button
              type="button"
              className="releases-load-more"
              onClick={loadMore}
              disabled={loading || loadingMore}
            >
              {t('releases.loadMore')}
            </button>
          )}
        </>
      )}
    </section>
  );
}
