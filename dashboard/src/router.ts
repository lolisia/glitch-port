import { useMemo, useSyncExternalStore } from 'react';

export interface AppRoute {
  name: 'issues' | 'issue' | 'messages' | 'unknown';
  issueId: number | null;
  query: URLSearchParams;
}

/** Dispatched after replaceState navigation, which does not fire hashchange. */
export const ROUTE_CHANGE_EVENT = 'gp-route-change';

const ISSUE_PATH = /^issues\/(\d+)$/;

function splitHash(hash: string): { path: string; query: URLSearchParams } {
  const withoutHash = hash.startsWith('#') ? hash.slice(1) : hash;
  const queryIndex = withoutHash.indexOf('?');
  const rawPath =
    queryIndex === -1 ? withoutHash : withoutHash.slice(0, queryIndex);
  const rawQuery = queryIndex === -1 ? '' : withoutHash.slice(queryIndex + 1);

  return {
    path: rawPath.replace(/^\/+/, ''),
    query: new URLSearchParams(rawQuery),
  };
}

export function parseHash(hash: string): AppRoute {
  const { path, query } = splitHash(hash);

  if (path === '') {
    return { name: 'issues', issueId: null, query };
  }

  if (path === 'messages') {
    return { name: 'messages', issueId: null, query };
  }

  if (path === 'issues') {
    return { name: 'issues', issueId: null, query };
  }

  const issueMatch = ISSUE_PATH.exec(path);
  if (issueMatch !== null) {
    return { name: 'issue', issueId: Number(issueMatch[1]), query };
  }

  return { name: 'unknown', issueId: null, query };
}

export function buildHash(path: string, query?: URLSearchParams): string {
  const normalizedPath = path.replace(/^\/+/, '');
  const search = query?.toString() ?? '';

  return `#/${normalizedPath}${search === '' ? '' : `?${search}`}`;
}

export function navigate(
  path: string,
  query?: URLSearchParams,
  options: { replace?: boolean } = {},
): void {
  const hash = buildHash(path, query);

  if (options.replace) {
    window.history.replaceState(null, '', hash);
    window.dispatchEvent(new Event(ROUTE_CHANGE_EVENT));
    return;
  }

  // Assigning the hash lets the browser record a history entry and fire a
  // hashchange event, which keeps back/forward navigation working.
  window.location.hash = hash;
}

function subscribe(onStoreChange: () => void): () => void {
  window.addEventListener('hashchange', onStoreChange);
  window.addEventListener(ROUTE_CHANGE_EVENT, onStoreChange);

  return () => {
    window.removeEventListener('hashchange', onStoreChange);
    window.removeEventListener(ROUTE_CHANGE_EVENT, onStoreChange);
  };
}

function getSnapshot(): string {
  return window.location.hash;
}

export function useHashRoute(): AppRoute {
  const hash = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
  return useMemo(() => parseHash(hash), [hash]);
}
