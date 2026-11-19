import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { listReleases } from '../../api/client';
import type { Release } from '../../api/types';
import { formatNumber, useI18n } from '../../i18n';
import { buildHash } from '../../router';
import { EmptyState, ErrorState, LoadingState, RelativeTime } from '../../ui';
import { NoProjectState } from '../issues/components/NoProjectState';

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
      <section className="flex flex-col gap-4" data-testid="releases-page">
        <h2 className="text-xl font-semibold tracking-tight">
          {t('releases.title')}
        </h2>

        <Card>
          <CardContent>
            <NoProjectState />
          </CardContent>
        </Card>
      </section>
    );
  }

  return (
    <section className="flex flex-col gap-4" data-testid="releases-page">
      <h2 className="text-xl font-semibold tracking-tight">
        {t('releases.title')}
      </h2>

      <Card>
        <CardContent className="flex flex-col gap-4">
          <form
            className="flex flex-wrap items-center gap-2"
            onSubmit={applyEnvironment}
          >
            <Input
              type="search"
              aria-label={t('releases.environment')}
              placeholder={t('releases.environmentPlaceholder')}
              value={environmentDraft}
              onChange={(event) => setEnvironmentDraft(event.target.value)}
              className="max-w-sm flex-1"
            />
            <Button type="submit" disabled={loading}>
              {t('releases.apply')}
            </Button>
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
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead scope="col">
                      {t('releases.table.release')}
                    </TableHead>
                    <TableHead scope="col">
                      {t('releases.table.firstSeen')}
                    </TableHead>
                    <TableHead scope="col">
                      {t('releases.table.lastSeen')}
                    </TableHead>
                    <TableHead scope="col" className="text-right">
                      {t('releases.table.events')}
                    </TableHead>
                    <TableHead scope="col" className="text-right">
                      {t('releases.table.issues')}
                    </TableHead>
                    <TableHead scope="col" className="text-right">
                      {t('releases.table.newIssues')}
                    </TableHead>
                    <TableHead scope="col" className="text-right">
                      {t('releases.table.unresolvedIssues')}
                    </TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {items.map((item) => (
                    <TableRow key={item.release}>
                      <TableCell className="font-medium">
                        <a
                          href={issuesHash('release', item.release)}
                          className="font-semibold hover:underline"
                        >
                          {item.release}
                        </a>
                      </TableCell>
                      <TableCell>
                        <RelativeTime value={item.first_seen} />
                      </TableCell>
                      <TableCell>
                        <RelativeTime value={item.last_seen} />
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {formatNumber(item.event_count, locale)}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {formatNumber(item.issue_count, locale)}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        <a href={issuesHash('first_release', item.release)}>
                          {formatNumber(item.new_issue_count, locale)}
                        </a>
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {formatNumber(item.unresolved_issue_count, locale)}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>

              {moreError !== null && (
                <ErrorState error={moreError} prefix={t('releases.loadError')} />
              )}

              {nextCursor !== null && (
                <Button
                  type="button"
                  variant="outline"
                  className="self-start"
                  onClick={loadMore}
                  disabled={loading || loadingMore}
                >
                  {t('releases.loadMore')}
                </Button>
              )}
            </>
          )}
        </CardContent>
      </Card>
    </section>
  );
}
