import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  ApiError,
  adminToken,
  apiBase,
  bulkUpdateIssues,
  createLabel,
  deleteEvent,
  deleteIssue,
  deleteLabel,
  fetchMessages,
  getEvent,
  getIssue,
  getStats,
  listIssueEvents,
  listIssues,
  listLabels,
  projectId,
  updateIssue,
  updateLabel,
} from './client';
import type {
  BulkIssuesRequest,
  EventDetail,
  Issue,
  IssueDetail,
  Label,
  Message,
  StatsBucket,
} from './types';

interface MockResponse {
  ok: boolean;
  status: number;
  headers?: { get: (name: string) => string | null };
  json: () => Promise<unknown>;
}

const API = 'http://localhost:5090/api/manage';
const AUTH_HEADERS = { Authorization: 'Bearer dev-admin-token' };

function jsonResponse(body: unknown, status = 200): MockResponse {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  };
}

function errorResponse(body: unknown, status: number): MockResponse {
  return {
    ok: false,
    status,
    json: async () => body,
  };
}

function noContentResponse() {
  const json = vi.fn(async () => undefined);
  const response: MockResponse = { ok: true, status: 204, json };
  return { response, json };
}

function emptyOkResponse() {
  const json = vi.fn(async () => undefined);
  const response: MockResponse = {
    ok: true,
    status: 200,
    headers: {
      get: (name) => (name.toLowerCase() === 'content-length' ? '0' : null),
    },
    json,
  };
  return { response, json };
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

async function captureError(promise: Promise<unknown>): Promise<ApiError> {
  try {
    await promise;
  } catch (error) {
    return error as ApiError;
  }

  throw new Error('Expected the request to reject');
}

const message: Message = {
  id: '11111111-1111-1111-1111-111111111111',
  received_at: '2026-01-02T03:04:05Z',
  endpoint: 'POST /api/1/messages',
  message: 'hello',
};

const label: Label = { id: 5, name: 'crash', color: '#ff0000' };

const issue: Issue = {
  id: 42,
  title: 'Boom',
  culprit: 'Game::Tick',
  level: 'error',
  platform: 'unreal',
  status: 'unresolved',
  first_seen: '2026-01-01T00:00:00Z',
  last_seen: '2026-01-02T00:00:00Z',
  event_count: 3,
  last_release: null,
  last_environment: 'production',
  labels: [label],
};

const event: EventDetail = {
  id: 'evt-1',
  issue_id: 42,
  timestamp: '2026-01-02T00:00:00Z',
  received_at: '2026-01-02T00:00:01Z',
  level: 'error',
  platform: 'unreal',
  environment: 'production',
  release: null,
  user_id: null,
  endpoint: 'POST /api/1/envelope',
  payload: {
    message: 'Boom',
    exception: [
      {
        type: 'Error',
        value: 'boom',
        stacktrace: {
          frames: [{ filename: 'Game.cpp', lineno: 12, in_app: true }],
        },
      },
    ],
    tags: { env: 'production' },
    extra: { nested: true },
    sdk: { name: 'glitch-port-unreal', version: '1.0.0' },
  },
};

const issueDetail: IssueDetail = { ...issue, latest_event: event };
const stats: StatsBucket[] = [{ bucket: '2026-01-02T00:00:00Z', count: 7 }];

afterEach(() => {
  vi.unstubAllEnvs();
  vi.resetModules();
});

describe('configuration', () => {
  it('uses the default API base, admin token and project id', () => {
    expect(apiBase).toBe('http://localhost:5090');
    expect(adminToken).toBe('dev-admin-token');
    expect(projectId).toBe(1);
  });
});

describe('fetchMessages', () => {
  it('fetches the legacy message list with a bearer token', async () => {
    const fetchMock = stubFetch(jsonResponse([message]));

    await expect(fetchMessages()).resolves.toEqual([message]);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/messages?limit=100`, {
      headers: AUTH_HEADERS,
    });
  });
});

describe('listIssues', () => {
  it('serializes every filter in the documented order', async () => {
    const fetchMock = stubFetch(
      jsonResponse({ items: [issue], next_cursor: 'next' }),
    );

    await expect(
      listIssues({
        status: 'unresolved',
        level: 'error',
        environment: 'production',
        release: '1.0.0',
        label: 'crash',
        q: 'boom',
        sort: 'last_seen',
        limit: 25,
        cursor: 'next',
      }),
    ).resolves.toEqual({ items: [issue], next_cursor: 'next' });

    expect(fetchMock).toHaveBeenCalledWith(
      `${API}/projects/1/issues?status=unresolved&level=error&environment=production&release=1.0.0&label=crash&q=boom&sort=last_seen&limit=25&cursor=next`,
      { headers: AUTH_HEADERS },
    );
  });

  it('drops undefined and empty-string filters and omits the query separator', async () => {
    const fetchMock = stubFetch(
      jsonResponse({ items: [], next_cursor: null }),
    );

    await expect(
      listIssues({
        status: undefined,
        level: '',
        environment: undefined,
        release: '',
        label: undefined,
        q: '',
        sort: undefined,
        limit: undefined,
        cursor: '',
      }),
    ).resolves.toEqual({ items: [], next_cursor: null });

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/1/issues`, {
      headers: AUTH_HEADERS,
    });
  });
});

describe('getIssue', () => {
  it('fetches one issue with its latest event', async () => {
    const fetchMock = stubFetch(jsonResponse(issueDetail));

    await expect(getIssue(42)).resolves.toEqual(issueDetail);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/issues/42`, {
      headers: AUTH_HEADERS,
    });
  });
});

describe('updateIssue', () => {
  it('patches the issue with a JSON body', async () => {
    const fetchMock = stubFetch(
      jsonResponse({ ...issue, status: 'resolved' }),
    );
    const patch = { status: 'resolved' as const, labels: ['crash'] };

    await updateIssue(42, patch);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/issues/42`, {
      method: 'PATCH',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify(patch),
    });
  });
});

describe('deleteIssue', () => {
  it('resolves undefined for a 204 response without parsing JSON', async () => {
    const { response, json } = noContentResponse();
    const fetchMock = stubFetch(response);

    await expect(deleteIssue(42)).resolves.toBeUndefined();

    expect(json).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledWith(`${API}/issues/42`, {
      method: 'DELETE',
      headers: AUTH_HEADERS,
    });
  });
});

describe('bulkUpdateIssues', () => {
  it('posts the bulk request as JSON', async () => {
    const fetchMock = stubFetch(jsonResponse({ affected: 2 }));
    const request: BulkIssuesRequest = {
      ids: [1, 2],
      action: 'add_labels',
      labels: ['crash'],
    };

    await expect(bulkUpdateIssues(request)).resolves.toEqual({ affected: 2 });

    expect(fetchMock).toHaveBeenCalledWith(`${API}/issues/bulk`, {
      method: 'POST',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });
  });
});

describe('listIssueEvents', () => {
  it('serializes limit and cursor', async () => {
    const fetchMock = stubFetch(
      jsonResponse({ items: [], next_cursor: 'cursor-2' }),
    );

    await expect(
      listIssueEvents(42, { limit: 50, cursor: 'cursor-1' }),
    ).resolves.toEqual({ items: [], next_cursor: 'cursor-2' });

    expect(fetchMock).toHaveBeenCalledWith(
      `${API}/issues/42/events?limit=50&cursor=cursor-1`,
      { headers: AUTH_HEADERS },
    );
  });

  it('omits the query string when no filters are given', async () => {
    const fetchMock = stubFetch(
      jsonResponse({ items: [], next_cursor: null }),
    );

    await listIssueEvents(42);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/issues/42/events`, {
      headers: AUTH_HEADERS,
    });
  });
});

describe('getEvent', () => {
  it('encodes the event id in the path', async () => {
    const fetchMock = stubFetch(jsonResponse(event));

    await expect(getEvent('evt/1 with space')).resolves.toEqual(event);

    expect(fetchMock).toHaveBeenCalledWith(
      `${API}/events/evt%2F1%20with%20space`,
      { headers: AUTH_HEADERS },
    );
  });
});

describe('deleteEvent', () => {
  it('deletes the encoded event path and resolves undefined', async () => {
    const { response, json } = noContentResponse();
    const fetchMock = stubFetch(response);

    await expect(deleteEvent('evt/1')).resolves.toBeUndefined();

    expect(json).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledWith(`${API}/events/evt%2F1`, {
      method: 'DELETE',
      headers: AUTH_HEADERS,
    });
  });
});

describe('listLabels', () => {
  it('fetches the project labels', async () => {
    const fetchMock = stubFetch(jsonResponse([label]));

    await expect(listLabels()).resolves.toEqual([label]);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/1/labels`, {
      headers: AUTH_HEADERS,
    });
  });
});

describe('createLabel', () => {
  it('posts the label as JSON', async () => {
    const fetchMock = stubFetch(jsonResponse(label));
    const input = { name: 'crash', color: '#ff0000' };

    await expect(createLabel(input)).resolves.toEqual(label);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/1/labels`, {
      method: 'POST',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify(input),
    });
  });
});

describe('updateLabel', () => {
  it('patches the label as JSON', async () => {
    const fetchMock = stubFetch(jsonResponse({ ...label, color: '#00ff00' }));
    const input = { color: '#00ff00' };

    await updateLabel(5, input);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/labels/5`, {
      method: 'PATCH',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify(input),
    });
  });
});

describe('deleteLabel', () => {
  it('resolves undefined for a 204 response without parsing JSON', async () => {
    const { response, json } = noContentResponse();
    const fetchMock = stubFetch(response);

    await expect(deleteLabel(5)).resolves.toBeUndefined();

    expect(json).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledWith(`${API}/labels/5`, {
      method: 'DELETE',
      headers: AUTH_HEADERS,
    });
  });
});

describe('getStats', () => {
  it('defaults to the 24h period', async () => {
    const fetchMock = stubFetch(jsonResponse(stats));

    await expect(getStats()).resolves.toEqual(stats);

    expect(fetchMock).toHaveBeenCalledWith(
      `${API}/projects/1/stats?period=24h`,
      { headers: AUTH_HEADERS },
    );
  });

  it('requests the 14d period when asked', async () => {
    const fetchMock = stubFetch(jsonResponse(stats));

    await getStats('14d');

    expect(fetchMock).toHaveBeenCalledWith(
      `${API}/projects/1/stats?period=14d`,
      { headers: AUTH_HEADERS },
    );
  });
});

describe('ApiError', () => {
  it.each([
    [401, 'unauthorized'],
    [404, 'issue not found'],
    [400, 'invalid status'],
    [409, 'conflict'],
  ])('carries the status, message and body for HTTP %i', async (status, text) => {
    const body = { error: text };
    stubFetch(errorResponse(body, status));

    const error = await captureError(getIssue(42));

    expect(error).toBeInstanceOf(ApiError);
    expect(error.name).toBe('ApiError');
    expect(error.status).toBe(status);
    expect(error.message).toBe(`HTTP ${status}: ${text}`);
    expect(error.body).toEqual(body);
  });

  it('keeps the plain HTTP message when the body has no error field', async () => {
    const body = { detail: 'nope' };
    stubFetch(errorResponse(body, 400));

    const error = await captureError(getIssue(42));

    expect(error.message).toBe('HTTP 400');
    expect(error.body).toEqual(body);
  });

  it('keeps the plain HTTP message when the body is not JSON', async () => {
    stubFetch({
      ok: false,
      status: 500,
      json: async () => {
        throw new SyntaxError('Unexpected token');
      },
    });

    const error = await captureError(fetchMessages());

    expect(error).toBeInstanceOf(ApiError);
    expect(error.message).toBe('HTTP 500');
    expect(error.body).toBeUndefined();
  });

  it('propagates network failures unchanged', async () => {
    const failure = new Error('network down');
    stubFetch(failure);

    await expect(fetchMessages()).rejects.toBe(failure);
    await expect(deleteIssue(1)).rejects.toBe(failure);
  });
});

describe('response bodies', () => {
  it('resolves undefined for an ok response without a body', async () => {
    const { response, json } = emptyOkResponse();
    stubFetch(response);

    await expect(listLabels()).resolves.toBeUndefined();

    expect(json).not.toHaveBeenCalled();
  });
});

describe('environment configuration', () => {
  it('uses VITE_PROJECT_ID for every project-scoped path', async () => {
    vi.stubEnv('VITE_PROJECT_ID', '7');
    vi.resetModules();
    const fetchMock = stubFetch(
      jsonResponse({ items: [], next_cursor: null }),
    );
    const client = await import('./client');

    await client.listIssues();
    await client.listLabels();
    await client.createLabel({ name: 'crash' });
    await client.getStats('14d');

    expect(client.projectId).toBe(7);
    expect(fetchMock.mock.calls.map((call) => call[0])).toEqual([
      `${API}/projects/7/issues`,
      `${API}/projects/7/labels`,
      `${API}/projects/7/labels`,
      `${API}/projects/7/stats?period=14d`,
    ]);
  });

  it('defaults the project id to 1 when unset or blank', async () => {
    vi.resetModules();
    const unset = await import('./client');
    expect(unset.projectId).toBe(1);

    vi.stubEnv('VITE_PROJECT_ID', '   ');
    vi.resetModules();
    const blank = await import('./client');
    expect(blank.projectId).toBe(1);
  });

  it('strips trailing slashes from VITE_API_BASE', async () => {
    vi.stubEnv('VITE_API_BASE', 'https://errors.example.com///');
    vi.resetModules();
    const fetchMock = stubFetch(jsonResponse([]));
    const client = await import('./client');

    expect(client.apiBase).toBe('https://errors.example.com');
    await client.fetchMessages();

    expect(fetchMock).toHaveBeenCalledWith(
      'https://errors.example.com/api/manage/messages?limit=100',
      { headers: AUTH_HEADERS },
    );
  });

  it('keeps requests relative and sends no Authorization header for a gateway', async () => {
    vi.stubEnv('VITE_API_BASE', '');
    vi.stubEnv('VITE_ADMIN_TOKEN', '');
    vi.resetModules();
    const fetchMock = stubFetch(jsonResponse([]));
    const client = await import('./client');

    expect(client.apiBase).toBe('');
    expect(client.adminToken).toBe('');
    await expect(client.fetchMessages()).resolves.toEqual([]);

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/manage/messages?limit=100',
      { headers: {} },
    );
  });

  it('uses a non-empty VITE_ADMIN_TOKEN as the bearer token', async () => {
    vi.stubEnv('VITE_ADMIN_TOKEN', 'secret-token');
    vi.resetModules();
    const fetchMock = stubFetch(jsonResponse([label]));
    const client = await import('./client');

    await client.listLabels();

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/1/labels`, {
      headers: { Authorization: 'Bearer secret-token' },
    });
  });
});
