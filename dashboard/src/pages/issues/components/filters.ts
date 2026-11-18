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
  /** Selected platforms; an empty list means "all platforms". */
  platform: string[];
  q: string;
  sort: IssueSortFilter;
}

export const DEFAULT_ISSUE_FILTERS: IssueFilters = {
  status: 'unresolved',
  level: '',
  environment: '',
  release: '',
  label: '',
  platform: [],
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

/**
 * Reads the comma-separated `platform` filter into distinct, trimmed values.
 * Blank entries are dropped, so `platform=` (or only commas) means no filter.
 */
export function parsePlatformFilter(value: string | null): string[] {
  if (value === null) {
    return [];
  }

  const platforms: string[] = [];

  for (const entry of value.split(',')) {
    const platform = entry.trim();

    if (platform !== '' && !platforms.includes(platform)) {
      platforms.push(platform);
    }
  }

  return platforms;
}

/** Reads the known filter keys, falling back for missing or invalid values. */
export function readIssueFilters(query: URLSearchParams): IssueFilters {
  return {
    status: parseIssueStatusFilter(query.get('status')),
    level: readTrimmed(query, 'level'),
    environment: readTrimmed(query, 'environment'),
    release: readTrimmed(query, 'release'),
    label: readTrimmed(query, 'label'),
    platform: parsePlatformFilter(query.get('platform')),
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
  // One comma-separated value keeps shared links compact and matches the API.
  append('platform', filters.platform.join(','));
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
    platform: filters.platform,
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
  labelKey: string;
}> = [
  { value: 'unresolved', labelKey: 'issues.status.unresolved' },
  { value: 'resolved', labelKey: 'issues.status.resolved' },
  { value: 'ignored', labelKey: 'issues.status.ignored' },
  { value: 'all', labelKey: 'issues.status.all' },
];

export const LEVEL_OPTIONS: ReadonlyArray<{ value: string; labelKey: string }> =
  [
    { value: '', labelKey: 'issues.filters.allLevels' },
    { value: 'debug', labelKey: 'issues.filters.debug' },
    { value: 'info', labelKey: 'issues.filters.info' },
    { value: 'warning', labelKey: 'issues.filters.warning' },
    { value: 'error', labelKey: 'issues.filters.error' },
    { value: 'fatal', labelKey: 'issues.filters.fatal' },
  ];

export const SORT_OPTIONS: ReadonlyArray<{
  value: IssueSortFilter;
  labelKey: string;
}> = [
  { value: 'last_seen', labelKey: 'issues.filters.lastSeen' },
  { value: 'first_seen', labelKey: 'issues.filters.firstSeen' },
  { value: 'count', labelKey: 'issues.filters.eventCount' },
];
