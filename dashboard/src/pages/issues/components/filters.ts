import type { IssueListQuery } from '../../../api/types';

export const ISSUE_PAGE_SIZE = 25;
export const SEARCH_DEBOUNCE_MS = 300;

export const ISSUE_STATUS_VALUES = [
  'unresolved',
  'resolved',
  'ignored',
  'all',
] as const;

export type IssueStatusFilter = (typeof ISSUE_STATUS_VALUES)[number];

export const ISSUE_SORT_VALUES = ['last_seen', 'first_seen', 'count'] as const;

export type IssueSortFilter = (typeof ISSUE_SORT_VALUES)[number];

/**
 * The filter state mirrors the hash query. Every value has an empty or
 * "default" representation that is omitted from the URL.
 */
export interface IssueFilters {
  status: IssueStatusFilter;
  level: string;
  environment: string;
  release: string;
  label: string;
  q: string;
  sort: IssueSortFilter;
}

export const DEFAULT_ISSUE_FILTERS: IssueFilters = {
  status: 'unresolved',
  level: '',
  environment: '',
  release: '',
  label: '',
  q: '',
  sort: 'last_seen',
};

export function parseIssueStatusFilter(
  value: string | null,
): IssueStatusFilter {
  return (
    ISSUE_STATUS_VALUES.find((status) => status === value) ??
    DEFAULT_ISSUE_FILTERS.status
  );
}

export function parseIssueSortFilter(value: string | null): IssueSortFilter {
  return (
    ISSUE_SORT_VALUES.find((sort) => sort === value) ??
    DEFAULT_ISSUE_FILTERS.sort
  );
}

function readTrimmed(query: URLSearchParams, key: string): string {
  return query.get(key)?.trim() ?? '';
}

/** Reads the known filter keys, falling back for missing or invalid values. */
export function readIssueFilters(query: URLSearchParams): IssueFilters {
  return {
    status: parseIssueStatusFilter(query.get('status')),
    level: readTrimmed(query, 'level'),
    environment: readTrimmed(query, 'environment'),
    release: readTrimmed(query, 'release'),
    label: readTrimmed(query, 'label'),
    q: readTrimmed(query, 'q'),
    sort: parseIssueSortFilter(query.get('sort')),
  };
}

/**
 * Rebuilds the hash query from the known filter keys only. Defaults and empty
 * values are omitted, and the cursor is never part of the URL.
 */
export function buildIssueFilterParams(
  filters: IssueFilters,
): URLSearchParams {
  const params = new URLSearchParams();
  const append = (key: string, value: string) => {
    if (value !== '') {
      params.append(key, value);
    }
  };

  if (filters.status !== DEFAULT_ISSUE_FILTERS.status) {
    append('status', filters.status);
  }
  append('q', filters.q);
  append('level', filters.level);
  append('environment', filters.environment);
  append('release', filters.release);
  append('label', filters.label);
  if (filters.sort !== DEFAULT_ISSUE_FILTERS.sort) {
    append('sort', filters.sort);
  }

  return params;
}

/** The request payload for the first page of a filtered list. */
export function buildIssueListQuery(
  filters: IssueFilters,
  cursor?: string,
): IssueListQuery {
  const query: IssueListQuery = {
    status: filters.status,
    level: filters.level,
    environment: filters.environment,
    release: filters.release,
    label: filters.label,
    q: filters.q,
    sort: filters.sort,
    limit: ISSUE_PAGE_SIZE,
  };

  if (cursor !== undefined) {
    query.cursor = cursor;
  }

  return query;
}

/** Distinct, sorted environment/release values, always keeping the active one. */
export function collectFilterOptions(
  values: ReadonlyArray<string | null>,
  activeValue: string,
): string[] {
  const options = new Set<string>();

  for (const value of values) {
    if (value !== null && value !== '') {
      options.add(value);
    }
  }

  if (activeValue !== '') {
    options.add(activeValue);
  }

  return Array.from(options).sort((left, right) => left.localeCompare(right));
}

export const STATUS_TABS: ReadonlyArray<{
  value: IssueStatusFilter;
  label: string;
}> = [
  { value: 'unresolved', label: 'Unresolved' },
  { value: 'resolved', label: 'Resolved' },
  { value: 'ignored', label: 'Ignored' },
  { value: 'all', label: 'All' },
];

export const LEVEL_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: '', label: 'All levels' },
  { value: 'debug', label: 'Debug' },
  { value: 'info', label: 'Info' },
  { value: 'warning', label: 'Warning' },
  { value: 'error', label: 'Error' },
  { value: 'fatal', label: 'Fatal' },
];

export const SORT_OPTIONS: ReadonlyArray<{
  value: IssueSortFilter;
  label: string;
}> = [
  { value: 'last_seen', label: 'Last seen' },
  { value: 'first_seen', label: 'First seen' },
  { value: 'count', label: 'Event count' },
];
