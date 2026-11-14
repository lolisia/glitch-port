import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import type { EventDetail, IssueDetail } from './api/types';

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

const issueDetail: IssueDetail = {
  id: 42,
  title: 'Crash on startup',
  culprit: 'UGameEngine::Init',
  level: 'error',
  platform: 'unreal',
  status: 'unresolved',
  first_seen: '2026-01-01T00:00:00Z',
  last_seen: '2026-01-02T00:00:00Z',
  event_count: 3,
  last_release: '1.0.0',
  last_environment: 'production',
  labels: [],
  latest_event: null,
};

const eventDetail: EventDetail = {
  id: '22222222-2222-2222-2222-222222222222',
  issue_id: 42,
  timestamp: '2026-01-02T00:00:00Z',
  received_at: '2026-01-02T00:00:01Z',
  level: 'error',
  platform: 'unreal',
  environment: 'production',
  release: '1.0.0',
  user_id: 'player-1',
  endpoint: 'POST /api/1/envelope',
  payload: { message: 'Crash on startup' },
};

/**
 * Dispatches by URL, matching the most specific route first. The placeholders
 * do not fetch yet, but the stub already covers every endpoint the upcoming
 * issues screens use so those tests can build on it.
 */
function stubFetch() {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const path = new URL(String(input), 'http://localhost').pathname;

    if (/^\/api\/manage\/messages$/.test(path)) {
      return jsonResponse([]);
    }
    if (/^\/api\/manage\/projects\/\d+\/issues$/.test(path)) {
      return jsonResponse({ items: [], next_cursor: null });
    }
    if (/^\/api\/manage\/projects\/\d+\/stats$/.test(path)) {
      return jsonResponse([]);
    }
    if (/^\/api\/manage\/projects\/\d+\/labels$/.test(path)) {
      return jsonResponse([]);
    }
    if (/^\/api\/manage\/issues\/\d+\/events$/.test(path)) {
      return jsonResponse({ items: [], next_cursor: null });
    }
    if (/^\/api\/manage\/issues\/\d+$/.test(path)) {
      return jsonResponse(issueDetail);
    }
    if (/^\/api\/manage\/events\/[^/]+$/.test(path)) {
      return jsonResponse(eventDetail);
    }

    throw new Error(`Unexpected fetch: ${path}`);
  });

  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

beforeEach(() => {
  window.history.replaceState(null, '', '/');
  stubFetch();
});

describe('App', () => {
  it('renders the issues page for a blank hash and rewrites it to #/issues', async () => {
    render(<App />);

    expect(
      screen.getByRole('heading', { level: 1, name: 'glitch-port' }),
    ).toBeInTheDocument();
    expect(screen.getByText('Project 1')).toBeInTheDocument();
    expect(screen.getByTestId('issues-page')).toBeInTheDocument();

    await waitFor(() => expect(window.location.hash).toBe('#/issues'));
  });

  it('rewrites an unknown hash to #/issues', async () => {
    window.history.replaceState(null, '', '/#/projects');

    render(<App />);

    expect(screen.getByTestId('issues-page')).toBeInTheDocument();

    await waitFor(() => expect(window.location.hash).toBe('#/issues'));
  });

  it('renders the issue detail page for #/issues/42', () => {
    window.history.replaceState(null, '', '/#/issues/42');

    render(<App />);

    expect(screen.getByTestId('issue-detail-page')).toBeInTheDocument();
    expect(screen.queryByTestId('issues-page')).not.toBeInTheDocument();
    expect(window.location.hash).toBe('#/issues/42');
  });

  it('renders the messages page for #/messages', async () => {
    window.history.replaceState(null, '', '/#/messages');

    render(<App />);

    expect(screen.getByTestId('messages-page')).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Messages' }),
    ).toBeInTheDocument();
    expect(await screen.findByText('No messages yet.')).toBeInTheDocument();
  });

  it('switches screens and aria-current when the tabs are clicked', async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, '', '/#/issues');

    render(<App />);

    const issuesTab = screen.getByRole('link', { name: 'Issues' });
    const messagesTab = screen.getByRole('link', { name: 'Messages' });
    expect(issuesTab).toHaveAttribute('aria-current', 'page');
    expect(messagesTab).not.toHaveAttribute('aria-current');

    await user.click(messagesTab);

    expect(await screen.findByTestId('messages-page')).toBeInTheDocument();
    expect(messagesTab).toHaveAttribute('aria-current', 'page');
    expect(issuesTab).not.toHaveAttribute('aria-current');
    expect(window.location.hash).toBe('#/messages');

    await user.click(issuesTab);

    expect(await screen.findByTestId('issues-page')).toBeInTheDocument();
    expect(issuesTab).toHaveAttribute('aria-current', 'page');
    expect(messagesTab).not.toHaveAttribute('aria-current');
  });

  it('switches screens on a hashchange event (back/forward)', () => {
    window.history.replaceState(null, '', '/#/issues');

    render(<App />);

    expect(screen.getByTestId('issues-page')).toBeInTheDocument();

    act(() => {
      window.history.replaceState(null, '', '/#/messages');
      window.dispatchEvent(new HashChangeEvent('hashchange'));
    });

    expect(screen.getByTestId('messages-page')).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Messages' }),
    ).toHaveAttribute('aria-current', 'page');
  });

  it('keeps the query for a filtered issues route', () => {
    window.history.replaceState(null, '', '/#/issues?status=resolved&q=null');

    render(<App />);

    expect(screen.getByTestId('issues-page')).toBeInTheDocument();
    expect(window.location.hash).toBe('#/issues?status=resolved&q=null');
  });
});
