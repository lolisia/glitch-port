import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactElement } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Issue, Label, StatsResponse } from '../../api/types';
import { I18nProvider, LOCALE_STORAGE_KEY } from '../../i18n';
import { formatAbsoluteTime } from '../../ui';
import { IssuesPage } from './IssuesPage';

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

function issuePage(
  items: Issue[],
  nextCursor: string | null = null,
): MockResponse {
  return jsonResponse({ items, next_cursor: nextCursor });
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });

  return { promise, resolve };
}

const API = 'http://localhost:5090/api/manage';
const ISSUES_URL = `${API}/projects/1/issues`;
const AUTH_HEADERS = { Authorization: 'Bearer dev-admin-token' };

const crashLabel: Label = { id: 11, name: 'crash', color: '#ff0000' };
const uiLabel: Label = { id: 12, name: 'ui', color: '#00ff00' };
const projectLabels: Label[] = [crashLabel, uiLabel];
const defaultStats: StatsResponse = {
  buckets: [
    { bucket: '2026-01-01T00:00:00Z', count: 3 },
    { bucket: '2026-01-01T01:00:00Z', count: 6 },
  ],
  by_platform: [
    { platform: 'unreal', issue_count: 4, event_count: 128 },
    { platform: 'unity', issue_count: 1, event_count: 9 },
  ],
};

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  const now = Date.now();

  return {
    id: 1,
    title: 'Boom',
    culprit: 'Game::Tick',
    level: 'error',
    platform: 'unreal',
    status: 'unresolved',
    // A little past the boundary keeps the rounded label stable.
    first_seen: new Date(now - (2 * 60 * 60 * 1000 + 10_000)).toISOString(),
    last_seen: new Date(now - (5 * 60 * 1000 + 10_000)).toISOString(),
    event_count: 12,
    last_release: '1.0.0',
    last_environment: 'production',
    labels: [crashLabel],
    ...overrides,
  };
}

type IssueResponder =
  | MockResponse
  | ((search: URLSearchParams) => MockResponse | Promise<MockResponse>);

type ProjectResponder =
  | MockResponse
  | ((projectId: number) => MockResponse | Promise<MockResponse>);

interface ApiStubOptions {
  issues?: IssueResponder | IssueResponder[];
  labels?: MockResponse;
  stats?: MockResponse;
  project?: ProjectResponder;
  bulk?: MockResponse;
}

const projectDetail = {
  id: 1,
  name: 'Starter',
  slug: 'starter',
  platforms: ['unreal', 'unity'],
  created_at: '2026-01-01T00:00:00Z',
  issue_count: 1,
  key_count: 1,
  dsn: null,
};

/**
 * Routes the mocked fetch by URL. Issue pages are served from a queue so a
 * test can return a different first page after a refetch; the last entry
 * repeats when a test needs more requests than pages.
 */
function stubApi(options: ApiStubOptions = {}) {
  const issuesQueue: IssueResponder[] = Array.isArray(options.issues)
    ? [...options.issues]
    : [options.issues ?? issuePage([])];

  const fetchMock = vi.fn(
    async (input: string, _init?: RequestInit): Promise<MockResponse> => {
      const url = new URL(input);
      const { pathname } = url;

      if (pathname.endsWith('/issues/bulk')) {
        return options.bulk ?? jsonResponse({ affected: 1 });
      }
      if (pathname.endsWith('/stats')) {
        return options.stats ?? jsonResponse(defaultStats);
      }
      if (pathname.endsWith('/labels')) {
        return options.labels ?? jsonResponse(projectLabels);
      }
      if (pathname.endsWith('/issues')) {
        const next =
          issuesQueue.length > 1 ? issuesQueue.shift()! : issuesQueue[0];
        return typeof next === 'function'
          ? next(url.searchParams)
          : next;
      }
      const projectMatch = /^\/api\/manage\/projects\/(\d+)$/.exec(pathname);
      if (projectMatch !== null) {
        const projectId = Number(projectMatch[1]);
        const responder = options.project;

        if (responder === undefined) {
          return jsonResponse({ ...projectDetail, id: projectId });
        }
        return typeof responder === 'function'
          ? responder(projectId)
          : responder;
      }

      throw new Error(`Unexpected fetch: ${input}`);
    },
  );

  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

type FetchMock = ReturnType<typeof stubApi>;

function issueRequestUrls(fetchMock: FetchMock): URL[] {
  return fetchMock.mock.calls
    .map(([input]) => new URL(String(input)))
    .filter((url) => url.pathname === '/api/manage/projects/1/issues');
}

function lastIssueRequest(fetchMock: FetchMock): URL {
  const requests = issueRequestUrls(fetchMock);
  return requests[requests.length - 1];
}

function bulkCalls(fetchMock: FetchMock) {
  return fetchMock.mock.calls.filter(
    ([input]) => String(input) === `${API}/issues/bulk`,
  );
}

function hashParams(): URLSearchParams {
  const hash = window.location.hash;
  const queryIndex = hash.indexOf('?');
  return new URLSearchParams(
    queryIndex === -1 ? '' : hash.slice(queryIndex + 1),
  );
}

function optionValues(select: HTMLElement): string[] {
  return within(select)
    .getAllByRole('option')
    .map((option) => (option as HTMLOptionElement).value);
}

/** Renders the screen the way the app does, inside the locale provider. */
function renderPage(ui: ReactElement) {
  return render(<I18nProvider>{ui}</I18nProvider>);
}

describe('IssuesPage', () => {
  beforeEach(() => {
    window.history.replaceState(null, '', '/');
    window.localStorage.clear();
  });

  it('renders selectable rows with links, badges, chips, counts and relative times', async () => {
    const first = makeIssue({ id: 7, title: 'Crash on startup' });
    const second = makeIssue({
      id: 8,
      title: 'Silent failure',
      culprit: null,
      level: 'fatal',
      status: 'ignored',
      labels: [uiLabel],
      event_count: 1,
      last_seen: new Date(
        Date.now() - (3 * 60 * 60 * 1000 + 10_000),
      ).toISOString(),
      first_seen: new Date(
        Date.now() - (3 * 24 * 60 * 60 * 1000 + 10_000),
      ).toISOString(),
    });
    const fetchMock = stubApi({
      issues: issuePage([first, second]),
    });

    renderPage(<IssuesPage projectId={1} />);

    expect(screen.getByTestId('issues-page')).toBeInTheDocument();
    expect(screen.getByText('Loading...')).toBeInTheDocument();

    const table = await screen.findByRole('table');
    const headers = within(table).getAllByRole('columnheader');
    expect(headers.map((header) => header.textContent)).toEqual([
      '',
      'Title',
      'Culprit',
      'Level',
      'Platform',
      'Labels',
      'Events',
      'Last seen',
      'First seen',
      'Status',
    ]);
    expect(
      within(table).getByRole('checkbox', { name: 'Select all' }),
    ).not.toBeChecked();

    const link = within(table).getByRole('link', { name: 'Crash on startup' });
    expect(link).toHaveAttribute('href', '#/issues/7');

    const row = link.closest('tr');
    expect(row).not.toBeNull();
    const firstRow = within(row as HTMLElement);

    expect(
      firstRow.getByRole('checkbox', { name: 'Select issue 7' }),
    ).not.toBeChecked();
    expect(firstRow.getByText('Game::Tick')).toBeInTheDocument();
    expect(firstRow.getByText('error')).toBeInTheDocument();
    expect(firstRow.getByText('unreal')).toHaveClass('platform-chip');
    expect(firstRow.getByText('Unresolved')).toBeInTheDocument();
    expect(firstRow.getByText('crash')).toBeInTheDocument();
    expect(firstRow.getByText('12')).toBeInTheDocument();
    expect(firstRow.getByText('5 minutes ago')).toBeInTheDocument();
    expect(firstRow.getByText('5 minutes ago')).toHaveAttribute(
      'title',
      formatAbsoluteTime(first.last_seen),
    );
    expect(firstRow.getByText('2 hours ago')).toBeInTheDocument();
    expect(firstRow.getByText('2 hours ago')).toHaveAttribute(
      'title',
      formatAbsoluteTime(first.first_seen),
    );

    const secondRowElement = within(table)
      .getByRole('link', { name: 'Silent failure' })
      .closest('tr');
    expect(secondRowElement).not.toBeNull();
    const secondRow = within(secondRowElement as HTMLElement);
    const cells = secondRow.getAllByRole('cell');
    expect(cells[2].textContent).toBe('');
    expect(secondRow.getByText('fatal')).toBeInTheDocument();
    expect(secondRow.getByText('Ignored')).toBeInTheDocument();
    expect(secondRow.getByText('ui')).toBeInTheDocument();
    expect(secondRow.getByText('3 hours ago')).toBeInTheDocument();
    expect(secondRow.getByText('3 days ago')).toBeInTheDocument();

    // Defaults are sent explicitly; the other filters are omitted.
    expect(fetchMock).toHaveBeenCalledWith(
      `${ISSUES_URL}?status=unresolved&sort=last_seen&limit=25`,
      { headers: AUTH_HEADERS },
    );
    expect(fetchMock).toHaveBeenCalledWith(
      `${API}/projects/1/labels`,
      { headers: AUTH_HEADERS },
    );
  });

  it('renders the 24h stats chart, total and platform breakdown from getStats', async () => {
    const stats: StatsResponse = {
      buckets: [
        { bucket: '2026-02-01T00:00:00Z', count: 1 },
        { bucket: '2026-02-01T01:00:00Z', count: 4 },
      ],
      by_platform: [
        { platform: 'unity', issue_count: 4, event_count: 128 },
        { platform: 'dotnet', issue_count: 1, event_count: 9 },
      ],
    };
    const fetchMock = stubApi({ stats: jsonResponse(stats) });

    renderPage(<IssuesPage projectId={1} />);

    expect(
      await screen.findByRole('heading', {
        name: 'Events (last 24 hours)',
      }),
    ).toBeInTheDocument();

    const chart = screen.getByTestId('stats-chart');
    const bars = within(chart).getAllByTestId('stats-bar');
    expect(bars).toHaveLength(2);
    // The y-axis rounds the busiest bucket (4) to a clean 0-4 scale.
    expect(bars[0].style.height).toBe('25%');
    expect(bars[1].style.height).toBe('100%');
    expect(screen.getByText('5 events')).toBeInTheDocument();

    // Each bucket is a focusable mark named by its local time range and count.
    const start = formatAbsoluteTime('2026-02-01T01:00:00Z').replace(/:\d{2}$/, '');
    const end = formatAbsoluteTime('2026-02-01T02:00:00Z').slice(-8, -3);
    const range = `${start} – ${end}`;
    const slot = within(chart).getByRole('img', { name: `${range}: 4 events` });
    expect(within(chart).getByRole('img', { name: /: 1 event$/ })).toBeInTheDocument();

    expect(screen.queryByTestId('stats-tooltip')).not.toBeInTheDocument();
    fireEvent.mouseEnter(slot);
    const tooltip = screen.getByRole('tooltip');
    expect(tooltip).toHaveTextContent('4 events');
    expect(tooltip).toHaveTextContent(range);
    fireEvent.mouseLeave(chart.querySelector('.stats-plot')!);
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();

    fireEvent.focus(slot);
    expect(screen.getByRole('tooltip')).toHaveTextContent('4 events');
    fireEvent.blur(slot);
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();

    const platforms = screen.getByTestId('stats-platforms');
    expect(within(platforms).getByText('unity')).toHaveClass('platform-chip');
    expect(within(platforms).getByText('4 issues')).toBeInTheDocument();
    expect(within(platforms).getByText('dotnet')).toHaveClass('platform-chip');
    expect(within(platforms).getByText('1 issue')).toBeInTheDocument();

    const statsCalls = fetchMock.mock.calls.filter(([input]) =>
      String(input).endsWith('/stats?period=24h'),
    );
    expect(statsCalls).toHaveLength(1);
  });

  it('hides the platform breakdown when no platform has issues', async () => {
    stubApi({
      stats: jsonResponse({ buckets: [], by_platform: [] }),
    });

    renderPage(<IssuesPage projectId={1} />);

    await screen.findByRole('heading', { name: 'Events (last 24 hours)' });
    expect(screen.queryByTestId('stats-platforms')).not.toBeInTheDocument();
  });

  it('ignores a failed stats request', async () => {
    stubApi({
      stats: errorResponse(500),
      issues: issuePage([makeIssue()]),
    });

    renderPage(<IssuesPage projectId={1} />);

    expect(await screen.findByRole('link', { name: 'Boom' })).toBeInTheDocument();
    expect(screen.queryByText('Events (last 24 hours)')).not.toBeInTheDocument();
    expect(
      screen.queryByText(/Failed to load issues/),
    ).not.toBeInTheDocument();
  });

  it('round-trips every filter through the hash and the list request', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi({ issues: issuePage([makeIssue()]) });

    renderPage(<IssuesPage projectId={1} />);
    await screen.findByRole('table');

    expect(optionValues(screen.getByLabelText('Level'))).toEqual([
      '',
      'debug',
      'info',
      'warning',
      'error',
      'fatal',
    ]);
    expect(optionValues(screen.getByLabelText('Environment'))).toEqual([
      '',
      'production',
    ]);
    expect(optionValues(screen.getByLabelText('Release'))).toEqual([
      '',
      '1.0.0',
    ]);
    await waitFor(() =>
      expect(optionValues(screen.getByLabelText('Label'))).toEqual([
        '',
        'crash',
        'ui',
      ]),
    );
    expect(optionValues(screen.getByLabelText('Sort'))).toEqual([
      'last_seen',
      'first_seen',
      'count',
    ]);
    expect(
      (
        within(screen.getByLabelText('Sort')).getByRole('option', {
          name: 'Event count',
        }) as HTMLOptionElement
      ).value,
    ).toBe('count');

    await user.click(screen.getByRole('button', { name: 'Resolved' }));
    expect(screen.getByRole('button', { name: 'Resolved' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(screen.getByRole('button', { name: 'Unresolved' })).toHaveAttribute(
      'aria-pressed',
      'false',
    );
    await waitFor(() => expect(hashParams().get('status')).toBe('resolved'));
    await waitFor(() =>
      expect(lastIssueRequest(fetchMock).searchParams.get('status')).toBe(
        'resolved',
      ),
    );
    await screen.findByRole('table');

    await user.selectOptions(screen.getByLabelText('Level'), 'error');
    await waitFor(() => expect(hashParams().get('level')).toBe('error'));
    await waitFor(() =>
      expect(lastIssueRequest(fetchMock).searchParams.get('level')).toBe(
        'error',
      ),
    );
    await screen.findByRole('table');

    await user.selectOptions(
      screen.getByLabelText('Environment'),
      'production',
    );
    await waitFor(() =>
      expect(hashParams().get('environment')).toBe('production'),
    );
    await waitFor(() =>
      expect(lastIssueRequest(fetchMock).searchParams.get('environment')).toBe(
        'production',
      ),
    );
    await screen.findByRole('table');

    await user.selectOptions(screen.getByLabelText('Release'), '1.0.0');
    await waitFor(() => expect(hashParams().get('release')).toBe('1.0.0'));
    await waitFor(() =>
      expect(lastIssueRequest(fetchMock).searchParams.get('release')).toBe(
        '1.0.0',
      ),
    );
    await screen.findByRole('table');

    await user.selectOptions(screen.getByLabelText('Label'), 'crash');
    await waitFor(() => expect(hashParams().get('label')).toBe('crash'));
    await waitFor(() =>
      expect(lastIssueRequest(fetchMock).searchParams.get('label')).toBe(
        'crash',
      ),
    );
    await screen.findByRole('table');

    await waitFor(() =>
      expect(optionValues(screen.getByLabelText('Platform'))).toEqual([
        'unreal',
        'unity',
      ]),
    );
    await user.selectOptions(screen.getByLabelText('Platform'), [
      'unreal',
      'unity',
    ]);
    await waitFor(() =>
      expect(hashParams().get('platform')).toBe('unreal,unity'),
    );
    await waitFor(() =>
      expect(
        lastIssueRequest(fetchMock).searchParams.get('platform'),
      ).toBe('unreal,unity'),
    );
    await screen.findByRole('table');

    await user.selectOptions(screen.getByLabelText('Sort'), 'count');
    await waitFor(() => expect(hashParams().get('sort')).toBe('count'));
    await waitFor(() =>
      expect(lastIssueRequest(fetchMock).searchParams.get('sort')).toBe('count'),
    );
    await screen.findByRole('table');

    expect(window.location.hash).toBe(
      '#/issues?status=resolved&level=error&environment=production&release=1.0.0&label=crash&platform=unreal%2Cunity&sort=count',
    );

    const lastQuery = lastIssueRequest(fetchMock).searchParams;
    expect(lastQuery.get('status')).toBe('resolved');
    expect(lastQuery.get('level')).toBe('error');
    expect(lastQuery.get('environment')).toBe('production');
    expect(lastQuery.get('release')).toBe('1.0.0');
    expect(lastQuery.get('label')).toBe('crash');
    expect(lastQuery.get('platform')).toBe('unreal,unity');
    expect(lastQuery.get('sort')).toBe('count');
    expect(lastQuery.get('limit')).toBe('25');
    expect(lastQuery.get('cursor')).toBeNull();

    await user.click(screen.getByRole('button', { name: 'All' }));
    await waitFor(() => expect(hashParams().get('status')).toBe('all'));

    await user.click(screen.getByRole('button', { name: 'Unresolved' }));
    await waitFor(() => expect(hashParams().has('status')).toBe(false));
    expect(window.location.hash).toBe(
      '#/issues?level=error&environment=production&release=1.0.0&label=crash&platform=unreal%2Cunity&sort=count',
    );
  });

  it('falls back to default status and sort for invalid URL values', async () => {
    window.history.replaceState(
      null,
      '',
      '/#/issues?status=bogus&sort=sideways',
    );
    const fetchMock = stubApi();

    renderPage(<IssuesPage projectId={1} />);

    expect(screen.getByRole('button', { name: 'Unresolved' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(screen.getByLabelText('Sort')).toHaveValue('last_seen');

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        `${ISSUES_URL}?status=unresolved&sort=last_seen&limit=25`,
        { headers: AUTH_HEADERS },
      ),
    );
  });

  it('prefills the controls from the hash and requests those filters', async () => {
    window.history.replaceState(null, '', '/#/issues?status=resolved&q=null');
    const fetchMock = stubApi({
      issues: issuePage([makeIssue({ status: 'resolved' })]),
    });

    renderPage(<IssuesPage projectId={1} />);

    expect(screen.getByLabelText('Search issues')).toHaveValue('null');
    expect(screen.getByRole('button', { name: 'Resolved' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(screen.getByLabelText('Sort')).toHaveValue('last_seen');

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        `${ISSUES_URL}?status=resolved&q=null&sort=last_seen&limit=25`,
        { headers: AUTH_HEADERS },
      ),
    );
    expect(window.location.hash).toBe('#/issues?status=resolved&q=null');
  });

  it('debounces the search box and writes the trimmed query once', async () => {
    const fetchMock = stubApi();

    renderPage(<IssuesPage projectId={1} />);
    await screen.findByText('No issues found.');

    const search = screen.getByLabelText('Search issues');
    fireEvent.change(search, { target: { value: '  boom  ' } });

    // The request has not been made yet; the 300 ms timer is pending.
    expect(issueRequestUrls(fetchMock)).toHaveLength(1);

    await waitFor(() => expect(hashParams().get('q')).toBe('boom'), {
      timeout: 2000,
    });
    expect(screen.getByLabelText('Search issues')).toHaveValue('boom');

    await waitFor(() => {
      const matching = issueRequestUrls(fetchMock).filter(
        (url) => url.searchParams.get('q') === 'boom',
      );
      expect(matching).toHaveLength(1);
    });
    expect(issueRequestUrls(fetchMock)).toHaveLength(2);

    fireEvent.change(screen.getByLabelText('Search issues'), {
      target: { value: '   ' },
    });
    await waitFor(() => expect(hashParams().has('q')).toBe(false), {
      timeout: 2000,
    });
    expect(window.location.hash).toBe('#/issues');
    expect(issueRequestUrls(fetchMock)).toHaveLength(3);
  });

  it('clears the selection when the filters change', async () => {
    const user = userEvent.setup();
    stubApi({ issues: issuePage([makeIssue()]) });

    renderPage(<IssuesPage projectId={1} />);
    await screen.findByRole('link', { name: 'Boom' });

    await user.click(screen.getByRole('checkbox', { name: 'Select issue 1' }));
    expect(screen.getByText('1 selected')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Ignored' }));

    await waitFor(() =>
      expect(screen.queryByText('1 selected')).not.toBeInTheDocument(),
    );
    expect(
      await screen.findByRole('checkbox', { name: 'Select issue 1' }),
    ).not.toBeChecked();
  });

  it('loads the next page with the cursor, keeps the selection and disables while loading', async () => {
    const user = userEvent.setup();
    const first = makeIssue({ id: 1, title: 'First' });
    const second = makeIssue({ id: 2, title: 'Second' });
    const nextPage = deferred<MockResponse>();
    const fetchMock = stubApi({
      issues: [issuePage([first], 'cursor-1'), () => nextPage.promise],
    });

    renderPage(<IssuesPage projectId={1} />);

    expect(await screen.findByRole('link', { name: 'First' })).toBeInTheDocument();
    expect(
      screen.queryByRole('link', { name: 'Second' }),
    ).not.toBeInTheDocument();

    await user.click(screen.getByRole('checkbox', { name: 'Select issue 1' }));
    expect(screen.getByText('1 selected')).toBeInTheDocument();

    const loadMore = screen.getByRole('button', { name: 'Load more' });
    await user.click(loadMore);

    expect(loadMore).toBeDisabled();

    nextPage.resolve(issuePage([second]));

    expect(
      await screen.findByRole('link', { name: 'Second' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'First' })).toBeInTheDocument();
    expect(
      screen.getByRole('checkbox', { name: 'Select issue 1' }),
    ).toBeChecked();
    expect(
      screen.queryByRole('button', { name: 'Load more' }),
    ).not.toBeInTheDocument();

    const requests = issueRequestUrls(fetchMock);
    expect(requests).toHaveLength(2);
    expect(requests[1].search).toBe(
      '?status=unresolved&sort=last_seen&limit=25&cursor=cursor-1',
    );
  });

  it('selects and clears every loaded row with the header checkbox', async () => {
    const user = userEvent.setup();
    stubApi({
      issues: issuePage([
        makeIssue({ id: 1, title: 'First' }),
        makeIssue({ id: 2, title: 'Second' }),
      ]),
    });

    renderPage(<IssuesPage projectId={1} />);
    await screen.findByRole('link', { name: 'First' });

    const selectAll = screen.getByRole('checkbox', { name: 'Select all' });
    await user.click(selectAll);

    expect(screen.getByRole('checkbox', { name: 'Select issue 1' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Select issue 2' })).toBeChecked();
    expect(screen.getByText('2 selected')).toBeInTheDocument();

    await user.click(selectAll);

    expect(
      screen.getByRole('checkbox', { name: 'Select issue 1' }),
    ).not.toBeChecked();
    expect(
      screen.queryByText('2 selected'),
    ).not.toBeInTheDocument();
  });

  it('bulk resolves, refreshes the first page and clears the selection', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi({
      issues: [
        issuePage([
          makeIssue({ id: 1, title: 'First' }),
          makeIssue({ id: 2, title: 'Second' }),
        ]),
        issuePage([makeIssue({ id: 1, title: 'First resolved', status: 'resolved' })]),
      ],
    });

    renderPage(<IssuesPage projectId={1} />);
    await screen.findByRole('link', { name: 'First' });

    await user.click(screen.getByRole('checkbox', { name: 'Select issue 1' }));
    await user.click(screen.getByRole('checkbox', { name: 'Select issue 2' }));
    expect(screen.getByText('2 selected')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Resolve' }));

    expect(
      await screen.findByRole('link', { name: 'First resolved' }),
    ).toBeInTheDocument();
    expect(screen.queryByText('2 selected')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('checkbox', { name: 'Select issue 1' }),
    ).not.toBeChecked();

    const bulk = bulkCalls(fetchMock);
    expect(bulk).toHaveLength(1);
    expect(bulk[0][1]).toEqual({
      method: 'POST',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify({ ids: [1, 2], action: 'resolve' }),
    });

    const requests = issueRequestUrls(fetchMock);
    expect(requests).toHaveLength(2);
    expect(requests[1].search).toBe(
      '?status=unresolved&sort=last_seen&limit=25',
    );
  });

  it('adds and removes labels through the bulk endpoint', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi({
      issues: [
        issuePage([makeIssue({ id: 1, title: 'First' })]),
        issuePage([makeIssue({ id: 1, title: 'First labelled' })]),
        issuePage([makeIssue({ id: 1, title: 'First unlabelled' })]),
      ],
    });

    renderPage(<IssuesPage projectId={1} />);
    await screen.findByRole('link', { name: 'First' });

    await user.click(screen.getByRole('checkbox', { name: 'Select issue 1' }));

    const addLabel = screen.getByRole('button', { name: 'Add label' });
    const removeLabel = screen.getByRole('button', { name: 'Remove label' });
    expect(addLabel).toBeDisabled();
    expect(removeLabel).toBeDisabled();

    await user.type(screen.getByLabelText('Label name'), '  crash  ');
    expect(addLabel).toBeEnabled();

    await user.click(addLabel);

    expect(
      await screen.findByRole('link', { name: 'First labelled' }),
    ).toBeInTheDocument();
    expect(screen.queryByText('1 selected')).not.toBeInTheDocument();

    let labeled = bulkCalls(fetchMock);
    expect(labeled).toHaveLength(1);
    expect(labeled[0][1]).toEqual({
      method: 'POST',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify({ ids: [1], action: 'add_labels', labels: ['crash'] }),
    });

    // The label input is cleared together with the selection.
    await user.click(screen.getByRole('checkbox', { name: 'Select issue 1' }));
    expect(screen.getByLabelText('Label name')).toHaveValue('');

    await user.type(screen.getByLabelText('Label name'), 'crash');
    await user.click(screen.getByRole('button', { name: 'Remove label' }));

    expect(
      await screen.findByRole('link', { name: 'First unlabelled' }),
    ).toBeInTheDocument();

    labeled = bulkCalls(fetchMock);
    expect(labeled).toHaveLength(2);
    expect(labeled[1][1]).toEqual({
      method: 'POST',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify({
        ids: [1],
        action: 'remove_labels',
        labels: ['crash'],
      }),
    });
  });

  it('deletes the selection after confirmation', async () => {
    const user = userEvent.setup();
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const fetchMock = stubApi({
      issues: [
        issuePage([makeIssue({ id: 1, title: 'First' })]),
        issuePage([makeIssue({ id: 2, title: 'Second' })]),
      ],
    });

    renderPage(<IssuesPage projectId={1} />);
    await screen.findByRole('link', { name: 'First' });

    await user.click(screen.getByRole('checkbox', { name: 'Select issue 1' }));
    await user.click(screen.getByRole('button', { name: 'Delete' }));

    expect(confirm).toHaveBeenCalledWith('Delete 1 selected issues?');

    expect(await screen.findByRole('link', { name: 'Second' })).toBeInTheDocument();

    const bulk = bulkCalls(fetchMock);
    expect(bulk).toHaveLength(1);
    expect(bulk[0][1]).toEqual({
      method: 'POST',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify({ ids: [1], action: 'delete' }),
    });
    expect(issueRequestUrls(fetchMock)).toHaveLength(2);
  });

  it('does not delete when the confirmation is cancelled', async () => {
    const user = userEvent.setup();
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    const fetchMock = stubApi({ issues: issuePage([makeIssue()]) });

    renderPage(<IssuesPage projectId={1} />);
    await screen.findByRole('link', { name: 'Boom' });

    await user.click(screen.getByRole('checkbox', { name: 'Select issue 1' }));
    await user.click(screen.getByRole('button', { name: 'Delete' }));

    expect(confirm).toHaveBeenCalledWith('Delete 1 selected issues?');
    expect(bulkCalls(fetchMock)).toHaveLength(0);
    expect(issueRequestUrls(fetchMock)).toHaveLength(1);
    expect(screen.getByText('1 selected')).toBeInTheDocument();
  });

  it('renders a bulk error inline and keeps the selection', async () => {
    const user = userEvent.setup();
    stubApi({
      issues: issuePage([makeIssue()]),
      bulk: errorResponse(500, { error: 'nope' }),
    });

    renderPage(<IssuesPage projectId={1} />);
    await screen.findByRole('link', { name: 'Boom' });

    await user.click(screen.getByRole('checkbox', { name: 'Select issue 1' }));
    await user.click(screen.getByRole('button', { name: 'Ignore' }));

    expect(
      await screen.findByText('Bulk action failed: HTTP 500: nope'),
    ).toBeInTheDocument();
    expect(screen.getByText('1 selected')).toBeInTheDocument();
  });

  it('renders the empty state when no issues match', async () => {
    stubApi({ issues: issuePage([]) });

    renderPage(<IssuesPage projectId={1} />);

    expect(screen.getByText('Loading...')).toBeInTheDocument();
    expect(await screen.findByText('No issues found.')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Load more' }),
    ).not.toBeInTheDocument();
  });

  it('renders an error state for a failed list request', async () => {
    stubApi({ issues: errorResponse(500) });

    renderPage(<IssuesPage projectId={1} />);

    expect(
      await screen.findByText('Failed to load issues: HTTP 500'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByText(/VITE_ADMIN_TOKEN/)).not.toBeInTheDocument();
  });

  it('renders the VITE_ADMIN_TOKEN guidance for a 401 response', async () => {
    stubApi({ issues: errorResponse(401, { error: 'unauthorized' }) });

    renderPage(<IssuesPage projectId={1} />);

    expect(
      await screen.findByText('Failed to load issues: HTTP 401: unauthorized'),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        'Set VITE_ADMIN_TOKEN to an admin token accepted by the collector.',
      ),
    ).toBeInTheDocument();
  });

  it('offers only the All labels option when the label list fails', async () => {
    stubApi({
      labels: errorResponse(500),
      issues: issuePage([makeIssue()]),
    });

    renderPage(<IssuesPage projectId={1} />);
    await screen.findByRole('table');

    expect(optionValues(screen.getByLabelText('Label'))).toEqual(['']);
  });

  it('hides the platform filter when the project has no observed platforms', async () => {
    stubApi({
      issues: issuePage([makeIssue()]),
      project: jsonResponse({ ...projectDetail, platforms: [] }),
    });

    renderPage(<IssuesPage projectId={1} />);
    await screen.findByRole('table');

    expect(screen.queryByLabelText('Platform')).not.toBeInTheDocument();
  });

  it('hides the platform filter when the project request fails', async () => {
    stubApi({
      issues: issuePage([makeIssue()]),
      project: errorResponse(500, { error: 'boom' }),
    });

    renderPage(<IssuesPage projectId={1} />);
    await screen.findByRole('table');

    expect(screen.queryByLabelText('Platform')).not.toBeInTheDocument();
    expect(
      screen.queryByText(/Failed to load issues/),
    ).not.toBeInTheDocument();
  });

  it('restores the selected platforms from the hash and sends them together', async () => {
    window.history.replaceState(
      null,
      '',
      '/#/issues?platform=unity%2Cdotnet',
    );
    const fetchMock = stubApi({
      issues: issuePage([makeIssue()]),
      project: jsonResponse({
        ...projectDetail,
        platforms: ['unity', 'dotnet'],
      }),
    });

    renderPage(<IssuesPage projectId={1} />);

    const select = await screen.findByLabelText('Platform');
    expect(
      Array.from((select as HTMLSelectElement).selectedOptions).map(
        (option) => option.value,
      ),
    ).toEqual(['unity', 'dotnet']);

    await waitFor(() =>
      expect(lastIssueRequest(fetchMock).searchParams.get('platform')).toBe(
        'unity,dotnet',
      ),
    );
    expect(
      lastIssueRequest(fetchMock).searchParams.get('platform'),
    ).toBe('unity,dotnet');
  });

  it('keeps the active environment and release in the options', async () => {
    window.history.replaceState(
      null,
      '',
      '/#/issues?environment=staging&release=9.9.9',
    );
    const fetchMock = stubApi({ issues: issuePage([makeIssue()]) });

    renderPage(<IssuesPage projectId={1} />);
    await screen.findByRole('table');

    expect(optionValues(screen.getByLabelText('Environment'))).toEqual([
      '',
      'production',
      'staging',
    ]);
    expect(optionValues(screen.getByLabelText('Release'))).toEqual([
      '',
      '1.0.0',
      '9.9.9',
    ]);

    const query = lastIssueRequest(fetchMock).searchParams;
    expect(query.get('environment')).toBe('staging');
    expect(query.get('release')).toBe('9.9.9');
  });

  it('renders the no-project state without calling the API', () => {
    const fetchMock = stubApi();

    renderPage(<IssuesPage projectId={null} />);

    expect(screen.getByTestId('issues-page')).toBeInTheDocument();
    expect(screen.getByText(/No project selected/)).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'choose a project' }),
    ).toHaveAttribute('href', '#/projects');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('refetches every project-scoped request when the project changes', async () => {
    const fetchMock = stubApi();
    const requestedUrls = () =>
      fetchMock.mock.calls.map(([input]) => String(input));
    const { rerender } = renderPage(<IssuesPage projectId={1} />);

    expect(await screen.findByText('No issues found.')).toBeInTheDocument();
    expect(requestedUrls()).toContain(`${API}/projects/1/labels`);

    rerender(
      <I18nProvider>
        <IssuesPage projectId={2} />
      </I18nProvider>,
    );

    await waitFor(() =>
      expect(requestedUrls()).toContain(
        `${API}/projects/2/issues?status=unresolved&sort=last_seen&limit=25`,
      ),
    );
    expect(requestedUrls()).toContain(`${API}/projects/2/labels`);
    expect(requestedUrls()).toContain(`${API}/projects/2/stats?period=24h`);
    expect(await screen.findByText('No issues found.')).toBeInTheDocument();
  });

  it('renders the list, filters, stats and bulk bar in Korean', async () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'ko');
    const user = userEvent.setup();
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    stubApi({
      issues: issuePage([makeIssue({ id: 7, title: 'Crash on startup' })]),
    });

    renderPage(<IssuesPage projectId={1} />);

    expect(
      screen.getByRole('heading', { level: 2, name: '이슈' }),
    ).toBeInTheDocument();
    expect(screen.getByText('불러오는 중...')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '미해결' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(screen.getByLabelText('이슈 검색')).toBeInTheDocument();
    expect(screen.getByLabelText('정렬')).toHaveValue('last_seen');

    const table = await screen.findByRole('table');
    expect(
      within(table)
        .getAllByRole('columnheader')
        .map((header) => header.textContent),
    ).toEqual([
      '',
      '제목',
      '원인',
      '레벨',
      '플랫폼',
      '라벨',
      '이벤트',
      '마지막 발생',
      '첫 발생',
      '상태',
    ]);
    expect(
      within(table).getByRole('checkbox', { name: '이슈 7 선택' }),
    ).toBeInTheDocument();
    expect(within(table).getByText('오류')).toBeInTheDocument();
    expect(within(table).getByText('미해결')).toBeInTheDocument();

    expect(
      await screen.findByRole('heading', { name: '이벤트 (최근 24시간)' }),
    ).toBeInTheDocument();
    expect(screen.getByText('9개 이벤트')).toBeInTheDocument();
    expect(screen.getByText('4개 이슈')).toBeInTheDocument();

    await user.click(
      within(table).getByRole('checkbox', { name: '이슈 7 선택' }),
    );
    expect(screen.getByText('1개 선택됨')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: '삭제' }));
    expect(confirm).toHaveBeenCalledWith('선택한 이슈 1개를 삭제할까요?');
  });

  it('translates the error prefix but not the server error body', async () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'ko');
    stubApi({ issues: errorResponse(500, { error: 'boom' }) });

    renderPage(<IssuesPage projectId={1} />);

    expect(
      await screen.findByText('이슈를 불러오지 못했습니다: HTTP 500: boom'),
    ).toBeInTheDocument();
  });
});
