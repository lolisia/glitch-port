import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type {
  EventDetail,
  EventSummary,
  EventSummaryList,
  Issue,
  IssueDetail,
  Label,
  UpdateIssueRequest,
} from '../../api/types';
import { IssueDetailPage } from './IssueDetailPage';

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

function noContentResponse(): MockResponse {
  return { ok: true, status: 204, json: async () => undefined };
}

function minutesAgo(minutes: number): string {
  return new Date(Date.now() - minutes * 60_000).toISOString();
}

const FIRST_SEEN = minutesAgo(2 * 24 * 60 + 60);
const LAST_SEEN = minutesAgo(6);
const MIDDLE_SEEN = minutesAgo(30);
const OLDEST_SEEN = minutesAgo(90);

const LABELS: Label[] = [
  { id: 1, name: 'crash', color: '#d73a4a' },
  { id: 2, name: 'ui', color: '#0e8a16' },
  { id: 3, name: 'regression', color: '#fbca04' },
];

const latestEvent: EventDetail = {
  id: 'event-3',
  issue_id: 42,
  timestamp: LAST_SEEN,
  received_at: LAST_SEEN,
  level: 'error',
  platform: 'unreal',
  environment: 'production',
  release: '1.2.0',
  user_id: null,
  endpoint: 'POST /api/1/envelope',
  payload: { message: 'Latest payload body' },
};

const middleEvent: EventDetail = {
  id: 'event-2',
  issue_id: 42,
  timestamp: MIDDLE_SEEN,
  received_at: MIDDLE_SEEN,
  level: 'warning',
  platform: 'unreal',
  environment: null,
  release: null,
  user_id: null,
  endpoint: 'POST /api/1/envelope',
  payload: { message: 'Second payload body' },
};

const oldestEvent: EventDetail = {
  id: 'event-1',
  issue_id: 42,
  timestamp: OLDEST_SEEN,
  received_at: OLDEST_SEEN,
  level: 'info',
  platform: 'unreal',
  environment: 'staging',
  release: '1.0.0',
  user_id: null,
  endpoint: 'POST /api/1/envelope',
  payload: { message: 'First payload body' },
};

const latestSummary: EventSummary = {
  id: 'event-3',
  timestamp: LAST_SEEN,
  received_at: LAST_SEEN,
  level: 'error',
  environment: 'production',
  release: '1.2.0',
  user_id: null,
  message: 'Third summary',
};

const middleSummary: EventSummary = {
  id: 'event-2',
  // Empty timestamp exercises the received_at fallback.
  timestamp: '',
  received_at: MIDDLE_SEEN,
  level: 'warning',
  environment: null,
  release: null,
  user_id: null,
  message: 'Second summary',
};

const oldestSummary: EventSummary = {
  id: 'event-1',
  timestamp: OLDEST_SEEN,
  received_at: OLDEST_SEEN,
  level: 'info',
  environment: 'staging',
  release: '1.0.0',
  user_id: null,
  message: 'First summary',
};

const baseIssue: IssueDetail = {
  id: 42,
  title: 'Crash on startup',
  culprit: 'UGameEngine::Init',
  level: 'error',
  platform: 'unreal',
  status: 'unresolved',
  first_seen: FIRST_SEEN,
  last_seen: LAST_SEEN,
  event_count: 3,
  last_release: '1.2.0',
  last_environment: 'production',
  labels: [LABELS[0]],
  latest_event: latestEvent,
};

const defaultFirstPage: EventSummaryList = {
  items: [latestSummary, middleSummary, oldestSummary],
  next_cursor: null,
};

const defaultEventDetails: Record<string, EventDetail> = {
  'event-3': latestEvent,
  'event-2': middleEvent,
  'event-1': oldestEvent,
};

type Resolver<T> = T | (() => T);

interface ServerOptions {
  issue?: Resolver<IssueDetail>;
  issueError?: MockResponse;
  labels?: Resolver<Label[]>;
  firstEvents?: Resolver<EventSummaryList>;
  nextEvents?: Resolver<EventSummaryList>;
  eventDetails?: Resolver<Record<string, EventDetail>>;
  eventError?: MockResponse;
  patchIssue?: (body: UpdateIssueRequest) => Issue;
  patchError?: MockResponse;
  onDeleteEvent?: (id: string) => void;
  onDeleteIssue?: () => void;
}

interface RecordedCall {
  method: string;
  url: URL;
  body: unknown;
  headers: RequestInit['headers'];
}

function resolveValue<T>(value: Resolver<T>): T {
  return typeof value === 'function' ? (value as () => T)() : value;
}

function applyPatch(issue: IssueDetail, patch: UpdateIssueRequest): Issue {
  const labels =
    patch.labels === undefined
      ? issue.labels
      : patch.labels.map((name, index) => {
          const existing = LABELS.find((label) => label.name === name);
          return existing ?? { id: 100 + index, name, color: '#888888' };
        });

  return {
    ...issue,
    status: patch.status ?? issue.status,
    labels,
  };
}

const ISSUE_PATH = '/api/manage/issues/42';
const EVENTS_PATH = '/api/manage/issues/42/events';
const EVENT_PATH_PREFIX = '/api/manage/events/';
const LABELS_PATH = '/api/manage/projects/1/labels';

/**
 * URL-routing fetch stub. Every Management API endpoint used by the screen is
 * routed by method and path, and each call is recorded so tests can assert the
 * request method, query and body.
 */
function stubApi(options: ServerOptions = {}) {
  const calls: RecordedCall[] = [];

  const fetchMock = vi.fn(
    async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const body =
        typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
      calls.push({ method, url, body, headers: init?.headers });

      const path = url.pathname;

      if (method === 'GET' && path === ISSUE_PATH) {
        if (options.issueError !== undefined) {
          return options.issueError;
        }

        return jsonResponse(resolveValue(options.issue ?? baseIssue));
      }

      if (method === 'PATCH' && path === ISSUE_PATH) {
        if (options.patchError !== undefined) {
          return options.patchError;
        }

        const patch = body as UpdateIssueRequest;
        if (options.patchIssue !== undefined) {
          return jsonResponse(options.patchIssue(patch));
        }

        return jsonResponse(
          applyPatch(resolveValue(options.issue ?? baseIssue), patch),
        );
      }

      if (method === 'DELETE' && path === ISSUE_PATH) {
        options.onDeleteIssue?.();
        return noContentResponse();
      }

      if (method === 'GET' && path === EVENTS_PATH) {
        const cursor = url.searchParams.get('cursor');
        if (cursor !== null && options.nextEvents !== undefined) {
          return jsonResponse(resolveValue(options.nextEvents));
        }

        return jsonResponse(resolveValue(options.firstEvents ?? defaultFirstPage));
      }

      if (method === 'GET' && path.startsWith(EVENT_PATH_PREFIX)) {
        if (options.eventError !== undefined) {
          return options.eventError;
        }

        const id = decodeURIComponent(path.slice(EVENT_PATH_PREFIX.length));
        const details = resolveValue(options.eventDetails ?? defaultEventDetails);
        const detail = details[id];

        if (detail === undefined) {
          return jsonResponse({ error: 'event not found' }, 404);
        }

        return jsonResponse(detail);
      }

      if (method === 'DELETE' && path.startsWith(EVENT_PATH_PREFIX)) {
        const id = decodeURIComponent(path.slice(EVENT_PATH_PREFIX.length));
        options.onDeleteEvent?.(id);
        return noContentResponse();
      }

      if (method === 'GET' && path === LABELS_PATH) {
        return jsonResponse(resolveValue(options.labels ?? LABELS));
      }

      throw new Error(`Unexpected fetch: ${method} ${path}`);
    },
  );

  vi.stubGlobal('fetch', fetchMock);
  return { calls, fetchMock };
}

function callsTo(calls: RecordedCall[], method: string, path: string) {
  return calls.filter(
    (call) => call.method === method && call.url.pathname === path,
  );
}

beforeEach(() => {
  window.history.replaceState(null, '', '/#/issues/42');
});

describe('IssueDetailPage', () => {
  it('loads the issue, labels and event list and renders the header without an event GET', async () => {
    const { calls } = stubApi();

    render(<IssueDetailPage issueId={42} />);

    expect(screen.getByText('Loading...')).toBeInTheDocument();

    const header = await screen.findByTestId('issue-detail-header');

    expect(screen.getByTestId('issue-detail-page')).toBeInTheDocument();
    expect(
      within(header).getByRole('heading', {
        level: 2,
        name: 'Crash on startup',
      }),
    ).toBeInTheDocument();
    expect(within(header).getByText('UGameEngine::Init')).toBeInTheDocument();
    expect(within(header).getByText('error')).toBeInTheDocument();
    expect(within(header).getByText('Unresolved')).toBeInTheDocument();
    expect(within(header).getByText('3')).toBeInTheDocument();
    expect(within(header).getByText('1.2.0')).toBeInTheDocument();
    expect(within(header).getByText('production')).toBeInTheDocument();
    expect(
      within(header).getByRole('link', { name: 'Back to issues' }),
    ).toHaveAttribute('href', '#/issues');

    const firstSeenTime = header.querySelector(
      `time[datetime="${FIRST_SEEN}"]`,
    );
    const lastSeenTime = header.querySelector(`time[datetime="${LAST_SEEN}"]`);
    expect(firstSeenTime?.textContent).toMatch(/days ago/);
    expect(lastSeenTime?.textContent).toMatch(/minutes ago/);
    expect(firstSeenTime?.getAttribute('title')).toMatch(
      /^\d{4}-\d{2}-\d{2} /,
    );

    // One checkbox per project label, checked from the issue labels, plus the
    // current labels rendered as chips.
    expect(screen.getByRole('checkbox', { name: 'crash' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'ui' })).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'regression' })).not.toBeChecked();
    expect(
      within(screen.getByTestId('issue-label-chips')).getByText('crash'),
    ).toBeInTheDocument();

    const items = within(
      screen.getByTestId('issue-event-list'),
    ).getAllByRole('listitem');
    expect(items).toHaveLength(3);
    expect(within(items[0]).getByText('Third summary')).toBeInTheDocument();
    expect(within(items[0]).getByText('error')).toBeInTheDocument();
    expect(within(items[0]).getByText('production')).toBeInTheDocument();
    expect(within(items[0]).getByText('1.2.0')).toBeInTheDocument();
    expect(within(items[1]).getByText('Second summary')).toBeInTheDocument();
    expect(within(items[1]).getByText('warning')).toBeInTheDocument();
    expect(within(items[1]).queryByText('production')).not.toBeInTheDocument();
    expect(
      items[1].querySelector(`time[datetime="${MIDDLE_SEEN}"]`),
    ).not.toBeNull();

    expect(within(items[0]).getByRole('button')).toHaveAttribute(
      'aria-current',
      'true',
    );
    expect(within(items[1]).getByRole('button')).not.toHaveAttribute(
      'aria-current',
    );

    // latest_event is rendered straight from GET /issues/{id}.
    expect(await screen.findByText('Latest payload body')).toBeInTheDocument();
    expect(
      calls.some(
        (call) =>
          call.method === 'GET' &&
          call.url.pathname.startsWith(EVENT_PATH_PREFIX),
      ),
    ).toBe(false);

    const firstPageCall = callsTo(calls, 'GET', EVENTS_PATH)[0];
    expect(firstPageCall?.url.searchParams.get('limit')).toBe('25');
    expect(firstPageCall?.url.searchParams.get('cursor')).toBeNull();
  });

  it('renders a placeholder for event summaries without a message', async () => {
    stubApi({
      firstEvents: {
        items: [
          { ...latestSummary, message: null },
          { ...middleSummary, message: '   ' },
        ],
        next_cursor: null,
      },
    });

    render(<IssueDetailPage issueId={42} />);

    await screen.findByTestId('issue-detail-header');
    const items = within(
      screen.getByTestId('issue-event-list'),
    ).getAllByRole('listitem');

    expect(within(items[0]).getByText('<no message>')).toBeInTheDocument();
    expect(within(items[1]).getByText('<no message>')).toBeInTheDocument();
  });

  it('renders dashes for a missing release and environment', async () => {
    stubApi({
      issue: {
        ...baseIssue,
        last_release: null,
        last_environment: null,
      },
    });

    render(<IssueDetailPage issueId={42} />);

    const header = await screen.findByTestId('issue-detail-header');
    expect(within(header).getAllByText('-')).toHaveLength(2);
  });

  it('renders the load error and token guidance on 401', async () => {
    stubApi({ issueError: jsonResponse({ error: 'unauthorized' }, 401) });

    render(<IssueDetailPage issueId={42} />);

    expect(
      await screen.findByText('Failed to load issue: HTTP 401: unauthorized'),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        'Set VITE_ADMIN_TOKEN to an admin token accepted by the collector.',
      ),
    ).toBeInTheDocument();
    expect(screen.queryByTestId('issue-detail-header')).not.toBeInTheDocument();
  });

  it('renders the load error on 404', async () => {
    stubApi({ issueError: jsonResponse({ error: 'not found' }, 404) });

    render(<IssueDetailPage issueId={42} />);

    expect(
      await screen.findByText('Failed to load issue: HTTP 404: not found'),
    ).toBeInTheDocument();
    expect(screen.queryByTestId('issue-detail-header')).not.toBeInTheDocument();
  });

  it('PATCHes resolve, unresolve and ignore, updating the status badge', async () => {
    const user = userEvent.setup();
    const { calls } = stubApi();

    render(<IssueDetailPage issueId={42} />);

    const header = await screen.findByTestId('issue-detail-header');

    await user.click(screen.getByRole('button', { name: 'Resolve' }));
    expect(await within(header).findByText('Resolved')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Unresolve' }));
    expect(await within(header).findByText('Unresolved')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Ignore' }));
    expect(await within(header).findByText('Ignored')).toBeInTheDocument();

    const patchCalls = callsTo(calls, 'PATCH', ISSUE_PATH);
    expect(patchCalls.map((call) => call.body)).toEqual([
      { status: 'resolved' },
      { status: 'unresolved' },
      { status: 'ignored' },
    ]);
    expect(patchCalls[0].headers).toEqual({
      Authorization: 'Bearer dev-admin-token',
      'Content-Type': 'application/json',
    });

    // Every status action stays enabled, and latest_event is preserved.
    expect(screen.getByRole('button', { name: 'Resolve' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Unresolve' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Ignore' })).toBeEnabled();
    expect(await screen.findByText('Latest payload body')).toBeInTheDocument();
  });

  it('saves checked labels plus typed names in project order and clears the input', async () => {
    const user = userEvent.setup();
    const { calls } = stubApi();

    render(<IssueDetailPage issueId={42} />);

    await screen.findByTestId('issue-detail-header');

    await user.click(screen.getByRole('checkbox', { name: 'ui' }));
    expect(screen.getByRole('checkbox', { name: 'ui' })).toBeChecked();

    const input = screen.getByRole('textbox', { name: 'New label names' });
    await user.type(input, ' feature , crash , feature ');

    await user.click(screen.getByRole('button', { name: 'Save labels' }));

    const patchCall = callsTo(calls, 'PATCH', ISSUE_PATH)[0];
    expect(patchCall?.body).toEqual({
      labels: ['crash', 'ui', 'feature'],
    });

    await waitFor(() => expect(input).toHaveValue(''));
    expect(
      await within(screen.getByTestId('issue-label-chips')).findByText(
        'feature',
      ),
    ).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'crash' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'ui' })).toBeChecked();
  });

  it('keeps labels created from typed names when saving again', async () => {
    const user = userEvent.setup();
    const { calls } = stubApi();

    render(<IssueDetailPage issueId={42} />);

    await screen.findByTestId('issue-detail-header');

    const input = screen.getByRole('textbox', { name: 'New label names' });
    await user.type(input, 'feature');
    await user.click(screen.getByRole('button', { name: 'Save labels' }));

    await waitFor(() => expect(input).toHaveValue(''));

    // The API creates the typed label, so it must become a checkbox and stay
    // checked instead of disappearing from the next full-list save.
    const featureCheckbox = await screen.findByRole('checkbox', {
      name: 'feature',
    });
    expect(featureCheckbox).toBeChecked();
    expect(
      within(screen.getByTestId('issue-label-chips')).getByText('feature'),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('checkbox', { name: 'ui' }));
    await user.click(screen.getByRole('button', { name: 'Save labels' }));

    await waitFor(() =>
      expect(callsTo(calls, 'PATCH', ISSUE_PATH)).toHaveLength(2),
    );

    expect(callsTo(calls, 'PATCH', ISSUE_PATH)[1].body).toEqual({
      labels: ['crash', 'ui', 'feature'],
    });
    expect(screen.getByRole('checkbox', { name: 'feature' })).toBeChecked();
    expect(
      within(screen.getByTestId('issue-label-chips')).getByText('feature'),
    ).toBeInTheDocument();
  });

  it('shows an action error when a status update fails', async () => {
    const user = userEvent.setup();
    stubApi({ patchError: jsonResponse({ error: 'boom' }, 500) });

    render(<IssueDetailPage issueId={42} />);

    const header = await screen.findByTestId('issue-detail-header');

    await user.click(screen.getByRole('button', { name: 'Resolve' }));

    expect(
      await screen.findByText('Failed to update issue: HTTP 500: boom'),
    ).toBeInTheDocument();
    expect(within(header).getByText('Unresolved')).toBeInTheDocument();
  });

  it('fetches an event by id when selected and swaps the rendered body', async () => {
    const user = userEvent.setup();
    const { calls } = stubApi();

    render(<IssueDetailPage issueId={42} />);

    await screen.findByText('Latest payload body');
    const items = within(
      screen.getByTestId('issue-event-list'),
    ).getAllByRole('listitem');

    await user.click(within(items[1]).getByRole('button'));

    expect(await screen.findByText('Second payload body')).toBeInTheDocument();
    expect(screen.queryByText('Latest payload body')).not.toBeInTheDocument();
    expect(callsTo(calls, 'GET', '/api/manage/events/event-2')).toHaveLength(1);
    expect(within(items[1]).getByRole('button')).toHaveAttribute(
      'aria-current',
      'true',
    );
    expect(within(items[0]).getByRole('button')).not.toHaveAttribute(
      'aria-current',
    );
  });

  it('moves through the loaded events with Newer and Older and disables at the boundaries', async () => {
    const user = userEvent.setup();
    const { calls } = stubApi();

    render(<IssueDetailPage issueId={42} />);

    await screen.findByText('Latest payload body');

    const newer = screen.getByRole('button', { name: 'Newer' });
    const older = screen.getByRole('button', { name: 'Older' });
    expect(newer).toBeDisabled();
    expect(older).toBeEnabled();

    await user.click(older);
    expect(await screen.findByText('Second payload body')).toBeInTheDocument();
    expect(newer).toBeEnabled();
    expect(older).toBeEnabled();

    await user.click(older);
    expect(await screen.findByText('First payload body')).toBeInTheDocument();
    expect(newer).toBeEnabled();
    expect(older).toBeDisabled();

    await user.click(newer);
    expect(await screen.findByText('Second payload body')).toBeInTheDocument();

    const requested = calls
      .filter(
        (call) =>
          call.method === 'GET' &&
          call.url.pathname.startsWith(EVENT_PATH_PREFIX),
      )
      .map((call) => call.url.pathname);
    expect(requested).toEqual([
      '/api/manage/events/event-2',
      '/api/manage/events/event-1',
      '/api/manage/events/event-2',
    ]);
  });

  it('shows an inline error when selecting an event fails', async () => {
    const user = userEvent.setup();
    stubApi({ eventError: jsonResponse({ error: 'gone' }, 500) });

    render(<IssueDetailPage issueId={42} />);

    await screen.findByText('Latest payload body');
    const items = within(
      screen.getByTestId('issue-event-list'),
    ).getAllByRole('listitem');

    await user.click(within(items[1]).getByRole('button'));

    expect(
      await screen.findByText('Failed to load event: HTTP 500: gone'),
    ).toBeInTheDocument();
    expect(screen.getByText('Latest payload body')).toBeInTheDocument();
  });

  it('appends the next events page from the cursor', async () => {
    const user = userEvent.setup();
    const { calls } = stubApi({
      firstEvents: {
        items: [latestSummary, middleSummary],
        next_cursor: 'cursor-1',
      },
      nextEvents: {
        items: [oldestSummary],
        next_cursor: null,
      },
    });

    render(<IssueDetailPage issueId={42} />);

    await screen.findByText('Second summary');
    expect(screen.queryByText('First summary')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'More events' }));

    expect(await screen.findByText('First summary')).toBeInTheDocument();
    expect(
      within(screen.getByTestId('issue-event-list')).getAllByRole('listitem'),
    ).toHaveLength(3);

    const pageCalls = callsTo(calls, 'GET', EVENTS_PATH);
    expect(pageCalls).toHaveLength(2);
    expect(pageCalls[1].url.searchParams.get('cursor')).toBe('cursor-1');
    expect(pageCalls[1].url.searchParams.get('limit')).toBe('25');
    expect(
      screen.queryByRole('button', { name: 'More events' }),
    ).not.toBeInTheDocument();
  });

  it('does not delete an event when the confirmation is declined', async () => {
    const user = userEvent.setup();
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    const onDeleteEvent = vi.fn();
    const { calls } = stubApi({ onDeleteEvent });

    render(<IssueDetailPage issueId={42} />);

    await screen.findByText('Latest payload body');

    await user.click(screen.getByRole('button', { name: 'Delete event' }));

    expect(confirm).toHaveBeenCalledWith('Delete this event?');
    expect(onDeleteEvent).not.toHaveBeenCalled();
    expect(calls.some((call) => call.method === 'DELETE')).toBe(false);
    expect(screen.getByText('Latest payload body')).toBeInTheDocument();
  });

  it('deletes the selected event and selects the new latest event', async () => {
    const user = userEvent.setup();
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    let deleted = false;

    const { calls } = stubApi({
      issue: () =>
        deleted
          ? { ...baseIssue, event_count: 2, latest_event: middleEvent }
          : baseIssue,
      firstEvents: () =>
        deleted
          ? { items: [middleSummary, oldestSummary], next_cursor: null }
          : defaultFirstPage,
      onDeleteEvent: (id) => {
        if (id === 'event-3') {
          deleted = true;
        }
      },
    });

    render(<IssueDetailPage issueId={42} />);

    await screen.findByText('Latest payload body');

    await user.click(screen.getByRole('button', { name: 'Delete event' }));

    expect(await screen.findByText('Second payload body')).toBeInTheDocument();
    expect(callsTo(calls, 'DELETE', '/api/manage/events/event-3')).toHaveLength(
      1,
    );

    const issueCalls = callsTo(calls, 'GET', ISSUE_PATH);
    expect(issueCalls).toHaveLength(2);
    const pageCalls = callsTo(calls, 'GET', EVENTS_PATH);
    expect(pageCalls).toHaveLength(2);
    expect(pageCalls[1].url.searchParams.get('cursor')).toBeNull();

    expect(
      within(screen.getByTestId('issue-detail-header')).getByText('2'),
    ).toBeInTheDocument();
    const items = within(
      screen.getByTestId('issue-event-list'),
    ).getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(within(items[0]).getByRole('button')).toHaveAttribute(
      'aria-current',
      'true',
    );
  });

  it('does not delete the issue when the confirmation is declined', async () => {
    const user = userEvent.setup();
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    const { calls } = stubApi();

    render(<IssueDetailPage issueId={42} />);

    await screen.findByTestId('issue-detail-header');

    await user.click(screen.getByRole('button', { name: 'Delete issue' }));

    expect(confirm).toHaveBeenCalledWith(
      'Delete this issue and all of its events?',
    );
    expect(calls.some((call) => call.method === 'DELETE')).toBe(false);
    expect(window.location.hash).toBe('#/issues/42');
  });

  it('deletes the issue after confirmation and navigates to the issues list', async () => {
    const user = userEvent.setup();
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const { calls } = stubApi();

    render(<IssueDetailPage issueId={42} />);

    await screen.findByTestId('issue-detail-header');

    await user.click(screen.getByRole('button', { name: 'Delete issue' }));

    expect(callsTo(calls, 'DELETE', ISSUE_PATH)).toHaveLength(1);
    await waitFor(() => expect(window.location.hash).toBe('#/issues'));
  });

  it('renders the empty events state when the issue has no events', async () => {
    stubApi({
      issue: { ...baseIssue, event_count: 0, latest_event: null },
      firstEvents: { items: [], next_cursor: null },
    });

    render(<IssueDetailPage issueId={42} />);

    await screen.findByTestId('issue-detail-header');

    expect(screen.getByText('No events recorded.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Delete event' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Newer' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Older' })).toBeDisabled();
  });
});
