import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Release } from '../../api/types';
import { I18nProvider } from '../../i18n';
import { ReleasesPage } from './ReleasesPage';

interface MockResponse {
  ok: boolean;
  status: number;
  json: () => Promise<unknown>;
}

function jsonResponse(body: unknown, status = 200): MockResponse {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  };
}

function errorResponse(status: number, body: unknown = {}): MockResponse {
  return {
    ok: false,
    status,
    json: async () => body,
  };
}

function releasePage(items: Release[], nextCursor: string | null = null) {
  return jsonResponse({ items, next_cursor: nextCursor });
}

function minutesAgo(minutes: number): string {
  return new Date(Date.now() - minutes * 60_000).toISOString();
}

const API = 'http://localhost:5090/api/manage';
const RELEASES_URL = `${API}/projects/1/releases`;
const AUTH_HEADERS = { Authorization: 'Bearer dev-admin-token' };

function makeRelease(overrides: Partial<Release> = {}): Release {
  return {
    release: '1.0.0',
    first_seen: minutesAgo(2 * 60 + 10),
    last_seen: minutesAgo(5),
    event_count: 120,
    issue_count: 4,
    new_issue_count: 2,
    unresolved_issue_count: 1,
    ...overrides,
  };
}

type ReleasesResponder =
  | MockResponse
  | ((search: URLSearchParams) => MockResponse | Promise<MockResponse>);

function stubApi(responders: ReleasesResponder | ReleasesResponder[] = releasePage([])) {
  const queue: ReleasesResponder[] = Array.isArray(responders)
    ? [...responders]
    : [responders];

  const fetchMock = vi.fn(
    async (input: RequestInfo | URL, _init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost');

      if (url.pathname !== '/api/manage/projects/1/releases') {
        throw new Error(`Unexpected fetch: ${input}`);
      }

      const next = queue.length > 1 ? queue.shift()! : queue[0];

      return typeof next === 'function' ? next(url.searchParams) : next;
    },
  );

  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

type FetchMock = ReturnType<typeof stubApi>;

function releaseRequests(fetchMock: FetchMock): URL[] {
  return fetchMock.mock.calls.map(
    ([input]) => new URL(String(input), 'http://localhost'),
  );
}

function lastReleaseRequest(fetchMock: FetchMock): URL {
  const requests = releaseRequests(fetchMock);
  return requests[requests.length - 1];
}

function renderPage(projectId: number | null = 1) {
  return render(
    <I18nProvider>
      <ReleasesPage projectId={projectId} />
    </I18nProvider>,
  );
}

beforeEach(() => {
  window.history.replaceState(null, '', '/');
  window.localStorage.clear();
});

describe('ReleasesPage', () => {
  it('renders the release aggregates with relative times and links', async () => {
    const release = makeRelease();
    const fetchMock = stubApi(releasePage([release]));

    renderPage();

    expect(screen.getByTestId('releases-page')).toBeInTheDocument();
    expect(screen.getByText('Loading...')).toBeInTheDocument();

    const table = await screen.findByRole('table');
    expect(
      within(table)
        .getAllByRole('columnheader')
        .map((header) => header.textContent),
    ).toEqual([
      'Release',
      'First seen',
      'Last seen',
      'Events',
      'Issues',
      'New issues',
      'Unresolved issues',
    ]);

    const row = within(table).getByRole('link', { name: '1.0.0' }).closest('tr');
    expect(row).not.toBeNull();
    const rowScope = within(row as HTMLElement);
    expect(
      rowScope.getByRole('link', { name: '1.0.0' }),
    ).toHaveAttribute('href', '#/issues?release=1.0.0&status=all');

    expect(rowScope.getByText('2 hours ago')).toBeInTheDocument();
    expect(rowScope.getByText('5 minutes ago')).toBeInTheDocument();
    expect(rowScope.getByText('120')).toBeInTheDocument();
    expect(rowScope.getByText('4')).toBeInTheDocument();
    expect(rowScope.getByText('1')).toBeInTheDocument();
    expect(
      rowScope.getByRole('link', { name: '2' }),
    ).toHaveAttribute('href', '#/issues?first_release=1.0.0&status=all');

    expect(fetchMock).toHaveBeenCalledWith(`${RELEASES_URL}?limit=50`, {
      headers: AUTH_HEADERS,
    });
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });

  it('applies the environment filter on submit', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi((search) =>
      search.get('environment') === 'production'
        ? releasePage([makeRelease({ release: '2.0.0' })])
        : releasePage([makeRelease()]),
    );

    renderPage();

    await screen.findByRole('table');

    await user.type(screen.getByLabelText('Environment'), ' production ');
    await user.click(screen.getByRole('button', { name: 'Filter' }));

    expect(
      await screen.findByRole('link', { name: '2.0.0' }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: '1.0.0' })).not.toBeInTheDocument();

    const query = lastReleaseRequest(fetchMock).searchParams;
    expect(query.get('environment')).toBe('production');
    expect(query.get('limit')).toBe('50');
  });

  it('appends the next page from the cursor', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi([
      releasePage([makeRelease()], 'cursor-1'),
      releasePage(
        [makeRelease({ release: '0.9.0', new_issue_count: 1 })],
        null,
      ),
    ]);

    renderPage();

    await screen.findByRole('link', { name: '1.0.0' });
    expect(screen.queryByRole('link', { name: '0.9.0' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Load more' }));

    expect(
      await screen.findByRole('link', { name: '0.9.0' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '1.0.0' })).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Load more' }),
    ).not.toBeInTheDocument();

    const requests = releaseRequests(fetchMock);
    expect(requests).toHaveLength(2);
    expect(requests[1].search).toBe('?limit=50&cursor=cursor-1');
  });

  it('renders the empty state when the project has no releases', async () => {
    stubApi(releasePage([]));

    renderPage();

    expect(
      await screen.findByText('No releases yet.'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('renders an error state for a failed request', async () => {
    stubApi(errorResponse(500));

    renderPage();

    expect(
      await screen.findByText('Failed to load releases: HTTP 500'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('renders the no-project state without calling the API', () => {
    const fetchMock = stubApi();

    renderPage(null);

    expect(screen.getByTestId('releases-page')).toBeInTheDocument();
    expect(screen.getByText(/No project selected/)).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('renders the release list in Korean', async () => {
    window.localStorage.setItem('glitchport.locale', 'ko');
    stubApi(releasePage([makeRelease()]));

    renderPage();

    const table = await screen.findByRole('table');
    expect(
      within(table)
        .getAllByRole('columnheader')
        .map((header) => header.textContent),
    ).toEqual([
      '릴리스',
      '첫 발생',
      '마지막 발생',
      '이벤트',
      '이슈',
      '신규 이슈',
      '미해결 이슈',
    ]);
    expect(screen.getByLabelText('환경')).toBeInTheDocument();
    await waitFor(() =>
      expect(screen.getByRole('button', { name: '필터' })).toBeEnabled(),
    );
  });
});
