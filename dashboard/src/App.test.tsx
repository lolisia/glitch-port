import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import App from './App';
import type { Message } from './api';

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

function stubFetch(response: MockResponse | Error) {
  const fetchMock = vi.fn();

  if (response instanceof Error) {
    fetchMock.mockRejectedValue(response);
  } else {
    fetchMock.mockResolvedValue(response);
  }

  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });

  return { promise, resolve };
}

describe('App', () => {
  it('renders the title, Refresh button, table headers and fetched rows', async () => {
    const receivedAt = new Date(2026, 0, 2, 3, 4, 5);
    const rows: Message[] = [
      {
        id: '11111111-1111-1111-1111-111111111111',
        received_at: receivedAt.toISOString(),
        endpoint: 'POST /api/1/messages',
        message: 'hello',
      },
    ];
    const fetchMock = stubFetch(jsonResponse(rows));

    render(<App />);

    expect(
      screen.getByRole('heading', { name: 'glitch-port messages' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeInTheDocument();
    expect(screen.getByText('Loading...')).toBeInTheDocument();

    const table = await screen.findByRole('table');
    const headers = within(table).getAllByRole('columnheader');
    expect(headers.map((header) => header.textContent)).toEqual([
      'Received at',
      'Endpoint',
      'Message',
    ]);
    expect(
      within(table).getByText('POST /api/1/messages'),
    ).toBeInTheDocument();
    expect(within(table).getByText('hello')).toBeInTheDocument();
    // Constructed from local time, so the formatted local time is stable
    // regardless of the test machine's timezone.
    expect(within(table).getByText('2026-01-02 03:04:05')).toBeInTheDocument();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith(
      'http://localhost:5090/api/manage/messages?limit=100',
      { headers: { Authorization: 'Bearer dev-admin-token' } },
    );
  });

  it('renders the empty state when no messages are returned', async () => {
    stubFetch(jsonResponse([]));

    render(<App />);

    expect(await screen.findByText('No messages yet.')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('renders the error state for a non-2xx response', async () => {
    stubFetch(jsonResponse({ error: 'boom' }, 500));

    render(<App />);

    expect(
      await screen.findByText('Failed to load messages: HTTP 500'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('renders the error state for a network error', async () => {
    stubFetch(new Error('network down'));

    render(<App />);

    expect(
      await screen.findByText('Failed to load messages: network down'),
    ).toBeInTheDocument();
  });

  it('refetches on Refresh and disables the button while loading', async () => {
    const user = userEvent.setup();
    const receivedAt = new Date(2026, 0, 2, 3, 4, 5);
    const initial = deferred<MockResponse>();
    const fetchMock = vi.fn().mockReturnValueOnce(initial.promise);
    vi.stubGlobal('fetch', fetchMock);

    render(<App />);

    const refresh = screen.getByRole('button', { name: 'Refresh' });
    expect(refresh).toBeDisabled();
    expect(screen.getByText('Loading...')).toBeInTheDocument();

    initial.resolve(
      jsonResponse([
        {
          id: '11111111-1111-1111-1111-111111111111',
          received_at: receivedAt.toISOString(),
          endpoint: 'POST /api/1/messages',
          message: 'hello',
        },
      ]),
    );

    expect(await screen.findByText('hello')).toBeInTheDocument();
    expect(refresh).toBeEnabled();

    const next = deferred<MockResponse>();
    fetchMock.mockReturnValueOnce(next.promise);

    await user.click(refresh);

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(screen.getByText('Loading...')).toBeInTheDocument();
    expect(refresh).toBeDisabled();

    next.resolve(jsonResponse([]));

    expect(await screen.findByText('No messages yet.')).toBeInTheDocument();
    expect(refresh).toBeEnabled();
  });
});
