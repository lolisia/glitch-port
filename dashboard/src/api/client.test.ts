import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  ApiError,
  adminToken,
  apiBase,
  bulkUpdateIssues,
  createAlertRule,
  createLabel,
  createProject,
  createProjectKey,
  deleteAlertRule,
  deleteEvent,
  deleteIssue,
  deleteLabel,
  deleteProject,
  fetchMessages,
  getEvent,
  getIssue,
  getIssueTags,
  getProject,
  getStats,
  listAlertNotifications,
  listAlertRules,
  listIssueEvents,
  listIssues,
  listLabels,
  listProjectKeys,
  listProjects,
  listReleases,
  parseConfiguredProjectId,
  revokeProjectKey,
  testAlertRule,
  updateAlertRule,
  updateIssue,
  updateLabel,
  updateProject,
  updateProjectKey,
} from './client';
import type {
  AlertNotificationListResponse,
  AlertRule,
  AlertRuleTestResult,
  BulkIssuesRequest,
  CreatedProject,
  CreateProjectRequest,
  EventDetail,
  Issue,
  IssueDetail,
  IssueTagsResponse,
  Label,
  Message,
  ProjectDetail,
  ProjectKey,
  ProjectListItem,
  ReleaseListResponse,
  StatsResponse,
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
  first_release: '1.0.0',
  last_release: null,
  last_environment: 'production',
  resolved_at: null,
  resolved_in_release: null,
  regressed_at: null,
  regression_count: 0,
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
const stats: StatsResponse = {
  buckets: [{ bucket: '2026-01-02T00:00:00Z', count: 7 }],
  by_platform: [{ platform: 'unreal', issue_count: 2, event_count: 7 }],
};

const releaseList: ReleaseListResponse = {
  items: [
    {
      release: '1.0.0',
      first_seen: '2026-01-01T00:00:00Z',
      last_seen: '2026-01-02T00:00:00Z',
      event_count: 12,
      issue_count: 4,
      new_issue_count: 3,
      unresolved_issue_count: 2,
    },
  ],
  next_cursor: 'release-cursor',
};

const issueTags: IssueTagsResponse = {
  issue_id: 42,
  sample_size: 3,
  user_count: 2,
  tags: [
    {
      key: 'release',
      total: 3,
      distinct: 2,
      top: [
        { value: '1.0.0', count: 2 },
        { value: '1.1.0', count: 1 },
      ],
    },
  ],
};

const projectList: ProjectListItem[] = [
  {
    id: 1,
    name: 'Starter',
    slug: 'starter',
    platforms: ['unreal'],
    created_at: '2026-01-01T00:00:00Z',
    issue_count: 3,
    key_count: 1,
  },
];

const createdProject: CreatedProject = {
  id: 9,
  name: 'New project',
  slug: 'new-project',
  platforms: ['godot'],
  created_at: '2026-01-03T00:00:00Z',
  dsn: 'http://dev-public-key@localhost:5080/9',
};

const projectDetail: ProjectDetail = {
  ...projectList[0],
  dsn: 'http://public-key@localhost:5080/1',
};

const projectKey: ProjectKey = {
  id: 5,
  public_key: 'a1b2c3d4e5f60718293a4b5c6d7e8f90',
  label: 'production',
  is_active: true,
  rate_limit_per_minute: null,
  created_at: '2026-01-03T00:00:00Z',
  revoked_at: null,
  dsn: 'http://a1b2c3d4e5f60718293a4b5c6d7e8f90@localhost:5080/1',
};

afterEach(() => {
  vi.unstubAllEnvs();
  vi.resetModules();
});

describe('configuration', () => {
  it('uses the default API base and admin token', () => {
    expect(apiBase).toBe('http://localhost:5090');
    expect(adminToken).toBe('dev-admin-token');
  });

  it('re-exports the configured project id parser', () => {
    expect(parseConfiguredProjectId('4')).toBe(4);
    expect(parseConfiguredProjectId('  ')).toBeNull();
    expect(parseConfiguredProjectId(undefined)).toBeNull();
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

describe('listProjects', () => {
  it('fetches the project list', async () => {
    const fetchMock = stubFetch(jsonResponse(projectList));

    await expect(listProjects()).resolves.toEqual(projectList);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects`, {
      headers: AUTH_HEADERS,
    });
  });
});

describe('createProject', () => {
  it('posts the project and resolves the created DSN', async () => {
    const fetchMock = stubFetch(jsonResponse(createdProject, 201));
    const input: CreateProjectRequest = {
      name: 'New project',
    };

    await expect(createProject(input)).resolves.toEqual(createdProject);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects`, {
      method: 'POST',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify(input),
    });
  });

  it('surfaces the API error text for a conflicting slug', async () => {
    stubFetch(
      errorResponse({ error: "A project with slug 'dup' already exists." }, 409),
    );

    const error = await captureError(createProject({ name: 'Dup' }));

    expect(error.status).toBe(409);
    expect(error.message).toBe(
      "HTTP 409: A project with slug 'dup' already exists.",
    );
  });
});

describe('getProject', () => {
  it('fetches one project with its counts and DSN', async () => {
    const fetchMock = stubFetch(jsonResponse(projectDetail));

    await expect(getProject(1)).resolves.toEqual(projectDetail);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/1`, {
      headers: AUTH_HEADERS,
    });
  });
});

describe('updateProject', () => {
  it('patches only the fields it is given', async () => {
    const updated = { ...projectDetail, name: 'Renamed' };
    const fetchMock = stubFetch(jsonResponse(updated));

    await expect(updateProject(1, { name: 'Renamed' })).resolves.toEqual(
      updated,
    );

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/1`, {
      method: 'PATCH',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: 'Renamed' }),
    });
  });

  it('surfaces the API error text for an invalid update', async () => {
    stubFetch(errorResponse({ error: 'name must not be blank.' }, 400));

    const error = await captureError(updateProject(1, { name: '' }));

    expect(error.status).toBe(400);
    expect(error.message).toBe('HTTP 400: name must not be blank.');
  });
});

describe('deleteProject', () => {
  it('deletes the project and resolves undefined without parsing JSON', async () => {
    const { response, json } = noContentResponse();
    const fetchMock = stubFetch(response);

    await expect(deleteProject(1)).resolves.toBeUndefined();

    expect(json).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/1`, {
      method: 'DELETE',
      headers: AUTH_HEADERS,
    });
  });
});

describe('listProjectKeys', () => {
  it('fetches every key of the project', async () => {
    const fetchMock = stubFetch(jsonResponse([projectKey]));

    await expect(listProjectKeys(1)).resolves.toEqual([projectKey]);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/1/keys`, {
      headers: AUTH_HEADERS,
    });
  });
});

describe('createProjectKey', () => {
  it('posts the optional label and rate limit', async () => {
    const fetchMock = stubFetch(jsonResponse(projectKey, 201));
    const input = { label: 'production', rate_limit_per_minute: 600 };

    await expect(createProjectKey(1, input)).resolves.toEqual(projectKey);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/1/keys`, {
      method: 'POST',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify(input),
    });
  });
});

describe('updateProjectKey', () => {
  it('sends an explicit null to clear the rate limit', async () => {
    const cleared = { ...projectKey, rate_limit_per_minute: null };
    const fetchMock = stubFetch(jsonResponse(cleared));

    await expect(
      updateProjectKey(5, { rate_limit_per_minute: null }),
    ).resolves.toEqual(cleared);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/keys/5`, {
      method: 'PATCH',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify({ rate_limit_per_minute: null }),
    });
  });

  it('patches the active state when re-enabling a key', async () => {
    const fetchMock = stubFetch(jsonResponse(projectKey));

    await updateProjectKey(5, { is_active: true });

    expect(fetchMock).toHaveBeenCalledWith(`${API}/keys/5`, {
      method: 'PATCH',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify({ is_active: true }),
    });
  });
});

describe('revokeProjectKey', () => {
  it('revokes the key and resolves undefined without parsing JSON', async () => {
    const { response, json } = noContentResponse();
    const fetchMock = stubFetch(response);

    await expect(revokeProjectKey(5)).resolves.toBeUndefined();

    expect(json).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledWith(`${API}/keys/5`, {
      method: 'DELETE',
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
      listIssues(3, {
        status: 'unresolved',
        level: 'error',
        environment: 'production',
        release: '1.0.0',
        first_release: '0.9.0',
        label: 'crash',
        platform: ['unity', 'dotnet'],
        regressed: true,
        q: 'boom',
        sort: 'last_seen',
        limit: 25,
        cursor: 'next',
      }),
    ).resolves.toEqual({ items: [issue], next_cursor: 'next' });

    expect(fetchMock).toHaveBeenCalledWith(
      `${API}/projects/3/issues?status=unresolved&level=error&environment=production&release=1.0.0&first_release=0.9.0&label=crash&platform=unity%2Cdotnet&regressed=true&q=boom&sort=last_seen&limit=25&cursor=next`,
      { headers: AUTH_HEADERS },
    );
  });

  it('drops undefined and empty-string filters and omits the query separator', async () => {
    const fetchMock = stubFetch(
      jsonResponse({ items: [], next_cursor: null }),
    );

    await expect(
      listIssues(3, {
        status: undefined,
        level: '',
        environment: undefined,
        release: '',
        first_release: '',
        label: undefined,
        platform: [],
        regressed: undefined,
        q: '',
        sort: undefined,
        limit: undefined,
        cursor: '',
      }),
    ).resolves.toEqual({ items: [], next_cursor: null });

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/3/issues`, {
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

    await expect(listLabels(4)).resolves.toEqual([label]);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/4/labels`, {
      headers: AUTH_HEADERS,
    });
  });
});

describe('createLabel', () => {
  it('posts the label as JSON', async () => {
    const fetchMock = stubFetch(jsonResponse(label));
    const input = { name: 'crash', color: '#ff0000' };

    await expect(createLabel(4, input)).resolves.toEqual(label);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/4/labels`, {
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
  it('defaults to the 24h period and returns buckets plus platforms', async () => {
    const fetchMock = stubFetch(jsonResponse(stats));

    await expect(getStats(2)).resolves.toEqual(stats);

    expect(fetchMock).toHaveBeenCalledWith(
      `${API}/projects/2/stats?period=24h`,
      { headers: AUTH_HEADERS },
    );
  });

  it('requests the 14d period when asked', async () => {
    const fetchMock = stubFetch(jsonResponse(stats));

    await getStats(2, '14d');

    expect(fetchMock).toHaveBeenCalledWith(
      `${API}/projects/2/stats?period=14d`,
      { headers: AUTH_HEADERS },
    );
  });
});

describe('listReleases', () => {
  it('serializes the environment, limit and cursor', async () => {
    const fetchMock = stubFetch(jsonResponse(releaseList));

    await expect(
      listReleases(3, {
        environment: 'production',
        limit: 50,
        cursor: 'release-cursor',
      }),
    ).resolves.toEqual(releaseList);

    expect(fetchMock).toHaveBeenCalledWith(
      `${API}/projects/3/releases?environment=production&limit=50&cursor=release-cursor`,
      { headers: AUTH_HEADERS },
    );
  });

  it('omits the query string when no filters are given', async () => {
    const fetchMock = stubFetch(jsonResponse({ items: [], next_cursor: null }));

    await expect(listReleases(3)).resolves.toEqual({
      items: [],
      next_cursor: null,
    });

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/3/releases`, {
      headers: AUTH_HEADERS,
    });
  });
});

describe('getIssueTags', () => {
  it('fetches the tag distribution with a per-facet limit', async () => {
    const fetchMock = stubFetch(jsonResponse(issueTags));

    await expect(getIssueTags(42, 10)).resolves.toEqual(issueTags);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/issues/42/tags?limit=10`, {
      headers: AUTH_HEADERS,
    });
  });

  it('omits the limit so the server default applies', async () => {
    const fetchMock = stubFetch(jsonResponse(issueTags));

    await getIssueTags(42);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/issues/42/tags`, {
      headers: AUTH_HEADERS,
    });
  });
});

describe('alert rule client', () => {
  const alertRule: AlertRule = {
    id: 11,
    project_id: 1,
    name: 'Crash spike',
    trigger: 'event_frequency',
    min_level: 'error',
    environment: 'production',
    threshold: 10,
    window_minutes: 5,
    cooldown_minutes: 30,
    channel: 'webhook',
    target_url: 'https://hooks.example.com/alerts',
    is_enabled: true,
    created_at: '2026-01-02T03:04:05Z',
    updated_at: '2026-01-02T03:04:05Z',
  };

  const notifications: AlertNotificationListResponse = {
    items: [
      {
        id: 3,
        rule_id: 11,
        rule_name: 'Crash spike',
        issue_id: 42,
        trigger: 'event_frequency',
        status: 'failed',
        attempts: 2,
        last_error: 'HTTP 500 from target',
        created_at: '2026-01-02T04:00:00Z',
        sent_at: null,
      },
    ],
  };

  it('lists the alert rules of a project', async () => {
    const fetchMock = stubFetch(jsonResponse({ items: [alertRule] }));

    await expect(listAlertRules(1)).resolves.toEqual({ items: [alertRule] });

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/1/alert-rules`, {
      headers: AUTH_HEADERS,
    });
  });

  it('creates a rule with a JSON body', async () => {
    const fetchMock = stubFetch(jsonResponse(alertRule, 201));
    const input = {
      name: 'Crash spike',
      trigger: 'event_frequency' as const,
      channel: 'webhook' as const,
      target_url: 'https://hooks.example.com/alerts',
      threshold: 10,
      window_minutes: 5,
      cooldown_minutes: 30,
    };

    await expect(createAlertRule(1, input)).resolves.toEqual(alertRule);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/1/alert-rules`, {
      method: 'POST',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify(input),
    });
  });

  it('patches only the sent rule fields', async () => {
    const fetchMock = stubFetch(jsonResponse({ ...alertRule, is_enabled: false }));

    await updateAlertRule(11, { is_enabled: false });

    expect(fetchMock).toHaveBeenCalledWith(`${API}/alert-rules/11`, {
      method: 'PATCH',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify({ is_enabled: false }),
    });
  });

  it('deletes a rule without parsing a response body', async () => {
    const { response, json } = noContentResponse();
    const fetchMock = stubFetch(response);

    await expect(deleteAlertRule(11)).resolves.toBeUndefined();

    expect(json).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledWith(`${API}/alert-rules/11`, {
      method: 'DELETE',
      headers: AUTH_HEADERS,
    });
  });

  it('posts the test delivery and resolves its result', async () => {
    const result: AlertRuleTestResult = {
      delivered: false,
      status_code: 500,
      error: 'boom',
    };
    const fetchMock = stubFetch(jsonResponse(result));

    await expect(testAlertRule(11)).resolves.toEqual(result);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/alert-rules/11/test`, {
      method: 'POST',
      headers: AUTH_HEADERS,
    });
  });

  it('lists notifications with the default limit of 50', async () => {
    const fetchMock = stubFetch(jsonResponse(notifications));

    await expect(listAlertNotifications(1)).resolves.toEqual(notifications);

    expect(fetchMock).toHaveBeenCalledWith(
      `${API}/projects/1/alert-notifications?limit=50`,
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

    await expect(listLabels(1)).resolves.toBeUndefined();

    expect(json).not.toHaveBeenCalled();
  });
});

describe('environment configuration', () => {
  it('uses the explicit project id for every project-scoped path', async () => {
    const fetchMock = stubFetch(
      jsonResponse({ items: [], next_cursor: null }),
    );

    await listIssues(7);
    await listLabels(7);
    await createLabel(7, { name: 'crash' });
    await getStats(7, '14d');

    expect(fetchMock.mock.calls.map((call) => call[0])).toEqual([
      `${API}/projects/7/issues`,
      `${API}/projects/7/labels`,
      `${API}/projects/7/labels`,
      `${API}/projects/7/stats?period=14d`,
    ]);
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

    await client.listLabels(1);

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/1/labels`, {
      headers: { Authorization: 'Bearer secret-token' },
    });
  });
});
