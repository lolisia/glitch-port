import type { DeepStringify } from '../types';

/** English messages for the release list screen. */
export const releases = {
  title: 'Releases',
  environment: 'Environment',
  environmentPlaceholder: 'Filter by environment',
  apply: 'Filter',
  empty: 'No releases yet.',
  loadError: 'Failed to load releases',
  loadMore: 'Load more',
  table: {
    release: 'Release',
    firstSeen: 'First seen',
    lastSeen: 'Last seen',
    events: 'Events',
    issues: 'Issues',
    newIssues: 'New issues',
    unresolvedIssues: 'Unresolved issues',
  },
} as const;

export type ReleasesMessages = DeepStringify<typeof releases>;
