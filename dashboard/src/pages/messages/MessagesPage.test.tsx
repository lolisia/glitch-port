import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { Message } from '../../api/types';
import { I18nProvider, LOCALE_STORAGE_KEY } from '../../i18n';
import { MessagesPage } from './MessagesPage';

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

/** Renders the screen the way the app does, inside the locale provider. */
function renderPage() {
  return render(
    <I18nProvider>
      <MessagesPage />
    </I18nProvider>,
  );
}

describe('MessagesPage', () => {
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

    renderPage();

    expect(
      screen.getByRole('heading', { name: 'Messages' }),
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
    expect(within(table).getByText('POST /api/1/messages')).toBeInTheDocument();
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

    renderPage();

    expect(await screen.findByText('No messages yet.')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('renders the error state for a non-2xx response', async () => {
    stubFetch(jsonResponse({}, 500));

    renderPage();

    expect(
      await screen.findByText('Failed to load messages: HTTP 500'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('renders the error state for a network error', async () => {
    stubFetch(new Error('network down'));

    renderPage();

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

    renderPage();

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

  it('renders Korean labels and timestamps when the stored locale is ko', async () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'ko');
    const receivedAt = new Date(2026, 0, 2, 3, 4, 5);
    stubFetch(
      jsonResponse([
        {
          id: '11111111-1111-1111-1111-111111111111',
          received_at: receivedAt.toISOString(),
          endpoint: 'POST /api/1/messages',
          message: 'hello',
        },
      ]),
    );

    renderPage();

    expect(screen.getByRole('heading', { name: '메시지' })).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: '새로고침' }),
    ).toBeInTheDocument();
    expect(screen.getByText('불러오는 중...')).toBeInTheDocument();

    const table = await screen.findByRole('table');
    expect(
      within(table)
        .getAllByRole('columnheader')
        .map((header) => header.textContent),
    ).toEqual(['수신 시각', '엔드포인트', '메시지']);
    // The active locale drives the timestamp layout.
    expect(within(table).getByText('2026. 01. 02. 03:04:05')).toBeInTheDocument();
    expect(document.documentElement.lang).toBe('ko');
  });

  it('renders the Korean empty state', async () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'ko');
    stubFetch(jsonResponse([]));

    renderPage();

    expect(await screen.findByText('아직 메시지가 없습니다.')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('never translates the server error text', async () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'ko');
    stubFetch(jsonResponse({ error: 'queue unavailable' }, 503));

    renderPage();

    // Only the action prefix is localized; the API message is shown verbatim.
    expect(
      await screen.findByText(
        '메시지를 불러오지 못했습니다: HTTP 503: queue unavailable',
      ),
    ).toBeInTheDocument();
  });
});
