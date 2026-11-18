import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type {
  AlertRule,
  ProjectDetail,
  ProjectKey,
  UpdateProjectKeyRequest,
  UpdateProjectRequest,
} from '../../api/types';
import { I18nProvider, LOCALE_STORAGE_KEY } from '../../i18n';
import { formatAbsoluteTime } from '../../ui';
import { ProjectDetailPage } from './ProjectDetailPage';

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

function noContentResponse(): MockResponse {
  return {
    ok: true,
    status: 204,
    json: async () => undefined,
  };
}

const API = 'http://localhost:5090/api/manage';

const inboundFilters = {
  releases: ['0.0.*'],
  environments: ['staging'],
  message_patterns: ['*debug*'],
};

const alertRule: AlertRule = {
  id: 11,
  project_id: 7,
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

const project: ProjectDetail = {
  id: 7,
  name: 'Star Raiders',
  slug: 'star-raiders',
  platforms: ['unity', 'dotnet'],
  created_at: '2026-01-02T03:04:05Z',
  issue_count: 12,
  key_count: 1,
  inbound_filters: inboundFilters,
  dsn: 'http://a1b2c3d4e5f60718293a4b5c6d7e8f90@localhost:5080/7',
};

const activeKey: ProjectKey = {
  id: 5,
  public_key: 'a1b2c3d4e5f60718293a4b5c6d7e8f90',
  label: 'production',
  is_active: true,
  rate_limit_per_minute: null,
  created_at: '2026-01-02T03:04:05Z',
  revoked_at: null,
  dsn: 'http://a1b2c3d4e5f60718293a4b5c6d7e8f90@localhost:5080/7',
};

const revokedKey: ProjectKey = {
  id: 4,
  public_key: 'ffffffffffffffffffffffffffffffff',
  label: 'old build',
  is_active: false,
  rate_limit_per_minute: 600,
  created_at: '2026-01-01T00:00:00Z',
  revoked_at: '2026-01-03T00:00:00Z',
  dsn: 'http://ffffffffffffffffffffffffffffffff@localhost:5080/7',
};

const createdKey: ProjectKey = {
  id: 9,
  public_key: '0123456789abcdef0123456789abcdef',
  label: 'staging',
  is_active: true,
  rate_limit_per_minute: 600,
  created_at: '2026-01-04T00:00:00Z',
  revoked_at: null,
  dsn: 'http://0123456789abcdef0123456789abcdef@localhost:5080/7',
};

interface StubOptions {
  project?: () => MockResponse | Promise<MockResponse>;
  keys?: () => MockResponse | Promise<MockResponse>;
  alertRules?: () => MockResponse | Promise<MockResponse>;
  notifications?: () => MockResponse | Promise<MockResponse>;
  updateProject?: (body: UpdateProjectRequest) => MockResponse;
  updateKey?: (keyId: number, body: UpdateProjectKeyRequest) => MockResponse;
  createKey?: (body: unknown) => MockResponse;
  deleteKey?: (keyId: number) => MockResponse;
  deleteProject?: () => MockResponse;
}

function stubApi(options: StubOptions = {}) {
  const fetchMock = vi.fn(
    async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost');
      const path = url.pathname;
      const method = (init?.method ?? 'GET').toUpperCase();
      const body =
        typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;

      if (path === '/api/manage/projects/7' && method === 'GET') {
        return (options.project ?? (() => jsonResponse(project)))();
      }

      if (path === '/api/manage/projects/7' && method === 'PATCH') {
        return (
          options.updateProject ??
          ((patch: UpdateProjectRequest) => jsonResponse({ ...project, ...patch }))
        )(body);
      }

      if (path === '/api/manage/projects/7' && method === 'DELETE') {
        return (options.deleteProject ?? (() => noContentResponse()))();
      }

      if (path === '/api/manage/projects/7/keys' && method === 'GET') {
        return (
          options.keys ?? (() => jsonResponse([activeKey, revokedKey]))
        )();
      }

      if (path === '/api/manage/projects/7/alert-rules' && method === 'GET') {
        return (options.alertRules ?? (() => jsonResponse({ items: [] })))();
      }

      if (
        path === '/api/manage/projects/7/alert-notifications' &&
        method === 'GET'
      ) {
        return (options.notifications ?? (() => jsonResponse({ items: [] })))();
      }

      if (path === '/api/manage/projects/7/keys' && method === 'POST') {
        return (
          options.createKey ?? (() => jsonResponse(createdKey, 201))
        )(body);
      }

      const keyMatch = /^\/api\/manage\/keys\/(\d+)$/.exec(path);
      if (keyMatch !== null) {
        const keyId = Number(keyMatch[1]);

        if (method === 'PATCH') {
          return (
            options.updateKey ??
            ((_: number, patch: UpdateProjectKeyRequest) =>
              jsonResponse({ ...activeKey, ...patch }))
          )(keyId, body);
        }

        if (method === 'DELETE') {
          return (options.deleteKey ?? (() => noContentResponse()))(keyId);
        }
      }

      throw new Error(`Unexpected fetch: ${method} ${path}`);
    },
  );

  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

type FetchMock = ReturnType<typeof stubApi>;

function methodCalls(
  fetchMock: FetchMock,
  method: 'DELETE' | 'PATCH' | 'POST',
) {
  return fetchMock.mock.calls.filter(
    ([, init]) => (init?.method ?? 'GET').toUpperCase() === method,
  );
}

function getCalls(fetchMock: FetchMock) {
  return fetchMock.mock.calls.filter(
    ([, init]) => (init?.method ?? 'GET').toUpperCase() === 'GET',
  );
}

function callBody(call: unknown): unknown {
  const init = (call as [RequestInfo | URL, RequestInit | undefined])[1];
  return JSON.parse(String(init?.body));
}

function renderPage() {
  const onProjectsChanged = vi.fn();
  render(
    <I18nProvider>
      <ProjectDetailPage
        projectId={7}
        onProjectsChanged={onProjectsChanged}
      />
    </I18nProvider>,
  );

  return { onProjectsChanged };
}

async function findKeyRows(): Promise<HTMLElement[]> {
  const table = await screen.findByRole('table');
  return within(table).getAllByRole('row');
}

beforeEach(() => {
  window.history.replaceState(null, '', '/');
  window.localStorage.clear();
});

afterEach(() => {
  Reflect.deleteProperty(navigator, 'clipboard');
  Reflect.deleteProperty(document, 'execCommand');
});

describe('ProjectDetailPage', () => {
  it('loads the project and its keys in parallel and renders both', async () => {
    const fetchMock = stubApi();

    renderPage();

    expect(screen.getByText('Loading...')).toBeInTheDocument();

    expect(
      await screen.findByRole('heading', { level: 2, name: 'Star Raiders' }),
    ).toBeInTheDocument();

    const facts = screen.getByTestId('project-detail-facts');
    expect(within(facts).getByText('star-raiders')).toHaveClass(
      'project-detail-mono',
    );
    expect(within(facts).getByText('Platforms seen')).toBeInTheDocument();
    expect(within(facts).getByText('unity')).toHaveClass('platform-chip');
    expect(within(facts).getByText('dotnet')).toHaveClass('platform-chip');
    expect(
      within(facts).getByText(formatAbsoluteTime(project.created_at)),
    ).toBeInTheDocument();
    expect(within(facts).getByText('12')).toBeInTheDocument();
    expect(within(facts).getByText('1')).toBeInTheDocument();
    expect(screen.queryByLabelText('Platform')).not.toBeInTheDocument();

    const rows = await findKeyRows();
    expect(rows).toHaveLength(3);

    const activeRow = within(rows[1]);
    const publicKey = activeRow.getByText(activeKey.public_key);
    expect(publicKey.tagName).toBe('CODE');
    expect(publicKey).toHaveClass('project-detail-key');
    expect(activeRow.getByText('production')).toBeInTheDocument();
    expect(activeRow.getByText('Active')).toBeInTheDocument();
    expect(
      activeRow.getByLabelText(`Rate limit for key ${activeKey.id}`),
    ).toHaveValue('');
    expect(activeRow.getByText(activeKey.dsn)).toHaveClass('project-detail-dsn');
    expect(
      activeRow.getByRole('button', { name: 'Copy DSN' }),
    ).toBeInTheDocument();
    expect(activeRow.getByRole('button', { name: 'Revoke' })).toBeInTheDocument();

    const revokedRow = within(rows[2]);
    expect(revokedRow.getByText('old build')).toBeInTheDocument();
    expect(revokedRow.getByText(/Revoked/)).toBeInTheDocument();
    expect(
      revokedRow.getByText(formatAbsoluteTime(revokedKey.revoked_at!)),
    ).toBeInTheDocument();
    expect(
      revokedRow.getByLabelText(`Rate limit for key ${revokedKey.id}`),
    ).toHaveValue('600');
    expect(
      revokedRow.queryByRole('button', { name: 'Revoke' }),
    ).not.toBeInTheDocument();

    // The detail and the key list are independent requests started together;
    // the alert sections start their own requests once the project is known.
    expect(getCalls(fetchMock).map(([input]) => String(input))).toEqual([
      `${API}/projects/7`,
      `${API}/projects/7/keys`,
      `${API}/projects/7/alert-rules`,
      `${API}/projects/7/alert-notifications?limit=50`,
    ]);
  });

  it('patches only the changed name and refreshes the project list', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi();
    const { onProjectsChanged } = renderPage();

    await screen.findByRole('heading', { level: 2, name: 'Star Raiders' });

    const nameInput = screen.getByLabelText('Name');
    expect(nameInput).toHaveValue('Star Raiders');

    await user.clear(nameInput);
    await user.type(nameInput, '  Star Raiders Live  ');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Saved');
    expect(onProjectsChanged).toHaveBeenCalledTimes(1);

    const patches = methodCalls(fetchMock, 'PATCH');
    expect(patches).toHaveLength(1);
    expect(patches[0][0]).toBe(`${API}/projects/7`);
    expect(callBody(patches[0])).toEqual({ name: 'Star Raiders Live' });
  });

  it('shows an em dash when the project has no observed platforms', async () => {
    stubApi({
      project: () => jsonResponse({ ...project, platforms: [] }),
    });

    renderPage();

    const facts = await screen.findByTestId('project-detail-facts');
    expect(within(facts).getByText('—')).toBeInTheDocument();
  });

  it('shows the API error text when an update is rejected with 400', async () => {
    const user = userEvent.setup();
    stubApi({
      updateProject: () =>
        errorResponse(400, { error: 'name must not be blank.' }),
    });
    const { onProjectsChanged } = renderPage();

    await screen.findByRole('heading', { level: 2, name: 'Star Raiders' });

    const nameInput = screen.getByLabelText('Name');
    await user.clear(nameInput);
    await user.type(nameInput, 'Renamed');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(
      await screen.findByText(
        'Failed to update project: HTTP 400: name must not be blank.',
      ),
    ).toBeInTheDocument();
    expect(onProjectsChanged).not.toHaveBeenCalled();
  });

  it('patches the rate limit with an explicit null for a blank field and keeps 0', async () => {
    const user = userEvent.setup();
    const updateKey = vi.fn(
      (_keyId: number, patch: UpdateProjectKeyRequest) =>
        jsonResponse({ ...activeKey, ...patch }),
    );
    stubApi({ updateKey });

    renderPage();
    await screen.findByRole('heading', { level: 2, name: 'Star Raiders' });

    const rateInput = () => {
      const rows = within(screen.getByRole('table')).getAllByRole('row');
      return within(rows[1]).getByLabelText(
        `Rate limit for key ${activeKey.id}`,
      );
    };
    const saveButton = () => {
      const rows = within(screen.getByRole('table')).getAllByRole('row');
      return within(rows[1]).getByRole('button', { name: 'Save' });
    };

    await user.type(rateInput(), '120');
    await user.click(saveButton());

    await waitFor(() => expect(updateKey).toHaveBeenCalledTimes(1));
    expect(updateKey.mock.calls[0][0]).toBe(activeKey.id);
    expect(updateKey.mock.calls[0][1]).toEqual({ rate_limit_per_minute: 120 });

    await waitFor(() => expect(rateInput()).toHaveValue('120'));

    await user.clear(rateInput());
    await user.click(saveButton());

    await waitFor(() => expect(updateKey).toHaveBeenCalledTimes(2));
    expect(updateKey.mock.calls[1][1]).toEqual({ rate_limit_per_minute: null });

    await waitFor(() => expect(rateInput()).toHaveValue(''));

    await user.type(rateInput(), '0');
    await user.click(saveButton());

    await waitFor(() => expect(updateKey).toHaveBeenCalledTimes(3));
    expect(updateKey.mock.calls[2][1]).toEqual({ rate_limit_per_minute: 0 });
  });

  it('rejects a negative rate limit without sending a request', async () => {
    const user = userEvent.setup();
    const updateKey = vi.fn();
    stubApi({ updateKey });

    renderPage();
    await screen.findByRole('heading', { level: 2, name: 'Star Raiders' });

    const rows = await findKeyRows();
    const row = within(rows[1]);

    await user.type(
      row.getByLabelText(`Rate limit for key ${activeKey.id}`),
      '-5',
    );
    await user.click(row.getByRole('button', { name: 'Save' }));

    expect(
      await screen.findByText(
        'Failed to save rate limit: Rate limit must be a non-negative integer.',
      ),
    ).toBeInTheDocument();
    expect(updateKey).not.toHaveBeenCalled();
  });

  it('creates a key, shows its DSN with a copy button and refreshes the list', async () => {
    const user = userEvent.setup();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });

    let keyLoads = 0;
    const fetchMock = stubApi({
      keys: () =>
        jsonResponse(
          keyLoads++ === 0
            ? [activeKey, revokedKey]
            : [createdKey, activeKey, revokedKey],
        ),
    });

    renderPage();
    await screen.findByRole('heading', { level: 2, name: 'Star Raiders' });
    await findKeyRows();

    await user.type(screen.getByLabelText('Label'), 'staging');
    await user.type(screen.getByLabelText('Rate limit per minute'), '600');
    await user.click(screen.getByRole('button', { name: 'Create key' }));

    const createdPanel = await screen.findByTestId('created-key');
    expect(within(createdPanel).getByLabelText('DSN')).toHaveValue(
      createdKey.dsn,
    );
    expect(
      within(createdPanel).getByRole('button', { name: 'Copy new key DSN' }),
    ).toBeInTheDocument();

    const posts = methodCalls(fetchMock, 'POST');
    expect(posts).toHaveLength(1);
    expect(posts[0][0]).toBe(`${API}/projects/7/keys`);
    expect(callBody(posts[0])).toEqual({
      label: 'staging',
      rate_limit_per_minute: 600,
    });

    expect(await screen.findByText(createdKey.public_key)).toBeInTheDocument();
    expect(keyLoads).toBe(2);

    await user.click(
      within(createdPanel).getByRole('button', { name: 'Copy new key DSN' }),
    );
    expect(writeText).toHaveBeenCalledWith(createdKey.dsn);
    expect(within(createdPanel).getByRole('status')).toHaveTextContent(
      'Copied',
    );
  });

  it('omits the optional create fields when they are left blank', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi({ keys: () => jsonResponse([activeKey]) });

    renderPage();
    await screen.findByRole('heading', { level: 2, name: 'Star Raiders' });

    await user.click(screen.getByRole('button', { name: 'Create key' }));

    await screen.findByTestId('created-key');

    const posts = methodCalls(fetchMock, 'POST');
    expect(posts).toHaveLength(1);
    expect(callBody(posts[0])).toEqual({});
  });

  it('copies a key DSN from the table', async () => {
    const user = userEvent.setup();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    stubApi();

    renderPage();
    const rows = await findKeyRows();
    const activeRow = within(rows[1]);

    await user.click(activeRow.getByRole('button', { name: 'Copy DSN' }));

    expect(writeText).toHaveBeenCalledWith(activeKey.dsn);
    expect(await activeRow.findByRole('status')).toHaveTextContent('Copied');
  });

  it('revokes a key and keeps the row visible as revoked', async () => {
    const user = userEvent.setup();
    const revokedActiveKey: ProjectKey = {
      ...activeKey,
      is_active: false,
      revoked_at: '2026-01-05T06:07:08Z',
    };

    let keyLoads = 0;
    const deleteKey = vi.fn(() => noContentResponse());
    stubApi({
      keys: () =>
        jsonResponse(
          keyLoads++ === 0
            ? [activeKey, revokedKey]
            : [revokedActiveKey, revokedKey],
        ),
      deleteKey,
    });

    renderPage();
    await screen.findByRole('heading', { level: 2, name: 'Star Raiders' });

    let rows = await findKeyRows();
    await user.click(within(rows[1]).getByRole('button', { name: 'Revoke' }));

    await waitFor(() => expect(deleteKey).toHaveBeenCalledWith(activeKey.id));
    await waitFor(() => {
      const currentRows = within(screen.getByRole('table')).getAllByRole('row');
      expect(
        within(currentRows[1]).queryByRole('button', { name: 'Revoke' }),
      ).not.toBeInTheDocument();
    });

    rows = within(screen.getByRole('table')).getAllByRole('row');

    expect(keyLoads).toBe(2);
    expect(within(rows[1]).getByText(/Revoked/)).toBeInTheDocument();
    expect(
      within(rows[1]).getByText(
        formatAbsoluteTime(revokedActiveKey.revoked_at!),
      ),
    ).toBeInTheDocument();
    expect(await screen.findByText(revokedActiveKey.public_key)).toBeInTheDocument();

    const facts = screen.getByTestId('project-detail-facts');
    expect(within(facts).getByText('0')).toBeInTheDocument();
  });

  it('keeps the delete button disabled until the exact project name is typed', async () => {
    const user = userEvent.setup();
    stubApi();

    renderPage();
    await screen.findByRole('heading', { level: 2, name: 'Star Raiders' });

    const confirm = screen.getByLabelText('Confirm project name');
    const button = screen.getByRole('button', { name: 'Delete project' });
    expect(button).toBeDisabled();

    await user.type(confirm, 'star raiders');
    expect(button).toBeDisabled();

    await user.clear(confirm);
    await user.type(confirm, 'Star Raiders');
    expect(button).toBeEnabled();
  });

  it('deletes the project after confirmation and navigates to #/projects', async () => {
    const user = userEvent.setup();
    const deleteProject = vi.fn(() => noContentResponse());
    stubApi({ deleteProject });
    const { onProjectsChanged } = renderPage();

    await screen.findByRole('heading', { level: 2, name: 'Star Raiders' });

    await user.type(
      screen.getByLabelText('Confirm project name'),
      'Star Raiders',
    );
    await user.click(screen.getByRole('button', { name: 'Delete project' }));

    await waitFor(() => expect(deleteProject).toHaveBeenCalledTimes(1));
    expect(onProjectsChanged).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(window.location.hash).toBe('#/projects'));
  });

  it('saves the inbound filters and shows the alert rules on the same page', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi({
      alertRules: () => jsonResponse({ items: [alertRule] }),
    });

    renderPage();

    await screen.findByRole('heading', { level: 2, name: 'Star Raiders' });
    expect(await screen.findByText('Crash spike')).toBeInTheDocument();

    const filters = await screen.findByTestId('inbound-filters-section');
    expect(within(filters).getByLabelText('Releases')).toHaveValue('0.0.*');

    await user.clear(within(filters).getByLabelText('Releases'));
    await user.type(within(filters).getByLabelText('Releases'), '0.0.*\n1.*');
    await user.click(
      within(filters).getByRole('button', { name: 'Save filters' }),
    );

    await waitFor(() => expect(methodCalls(fetchMock, 'PATCH')).toHaveLength(1));
    expect(callBody(methodCalls(fetchMock, 'PATCH')[0])).toEqual({
      inbound_filters: {
        releases: ['0.0.*', '1.*'],
        environments: ['staging'],
        message_patterns: ['*debug*'],
      },
    });
    expect(await within(filters).findByRole('status')).toHaveTextContent(
      'Filters saved',
    );
  });

  it('reports a failed filter save without hiding the keys or alert rules', async () => {
    const user = userEvent.setup();
    stubApi({
      alertRules: () => jsonResponse({ items: [alertRule] }),
      updateProject: () => errorResponse(500, { error: 'boom' }),
    });

    renderPage();

    await screen.findByRole('heading', { level: 2, name: 'Star Raiders' });
    expect(await screen.findByText('Crash spike')).toBeInTheDocument();

    const filters = await screen.findByTestId('inbound-filters-section');
    await user.click(
      within(filters).getByRole('button', { name: 'Save filters' }),
    );

    expect(
      await screen.findByText('Failed to save inbound filters: HTTP 500: boom'),
    ).toBeInTheDocument();
    // The key table and the alert list keep rendering their own data.
    expect(screen.getByText(activeKey.public_key)).toBeInTheDocument();
    expect(screen.getByText('Crash spike')).toBeInTheDocument();
  });

  it('renders a not-found state with a link back for a 404', async () => {
    stubApi({
      project: () =>
        errorResponse(404, { error: 'Project 7 was not found.' }),
      keys: () => errorResponse(404, { error: 'Project 7 was not found.' }),
    });

    renderPage();

    expect(await screen.findByText('Project not found')).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Back to projects' }),
    ).toHaveAttribute('href', '#/projects');
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('renders an error state for a failed project load that is not a 404', async () => {
    stubApi({ project: () => errorResponse(500, { error: 'boom' }) });

    renderPage();

    expect(
      await screen.findByText('Failed to load project: HTTP 500: boom'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Back to projects' }),
    ).toHaveAttribute('href', '#/projects');
  });

  it('renders the facts, key table, forms and danger zone in Korean', async () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'ko');
    stubApi();

    renderPage();

    const facts = await screen.findByTestId('project-detail-facts');
    expect(within(facts).getByText('슬러그')).toBeInTheDocument();
    expect(within(facts).getByText('확인된 플랫폼')).toBeInTheDocument();
    expect(within(facts).getByText('생성일')).toBeInTheDocument();
    expect(within(facts).getByText('미해결 이슈')).toBeInTheDocument();
    expect(within(facts).getByText('활성 키')).toBeInTheDocument();
    expect(
      within(facts).getByText(formatAbsoluteTime(project.created_at, 'ko')),
    ).toBeInTheDocument();

    expect(
      screen.getByRole('link', { name: '프로젝트 목록으로' }),
    ).toHaveAttribute('href', '#/projects');

    const rows = await findKeyRows();
    const activeRow = within(rows[1]);
    expect(activeRow.getByText('활성')).toBeInTheDocument();
    expect(activeRow.getByLabelText('키 5의 요청 제한')).toHaveValue('');
    expect(activeRow.getByRole('button', { name: '저장' })).toBeInTheDocument();
    expect(
      activeRow.getByRole('button', { name: 'DSN 복사' }),
    ).toBeInTheDocument();
    expect(activeRow.getByRole('button', { name: '해지' })).toBeInTheDocument();

    const revokedRow = within(rows[2]);
    expect(revokedRow.getByText(/해지됨/)).toBeInTheDocument();

    expect(
      screen.getByRole('heading', { level: 3, name: '프로젝트 편집' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: '변경 사항 저장' }),
    ).toBeInTheDocument();

    expect(
      screen.getByRole('heading', { level: 3, name: '키 만들기' }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('분당 요청 제한')).toBeInTheDocument();

    expect(
      screen.getByRole('heading', { level: 3, name: '알림 규칙' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: '새 알림 규칙' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { level: 4, name: '최근 알림' }),
    ).toBeInTheDocument();

    expect(
      screen.getByRole('heading', { level: 3, name: '수신 필터' }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('릴리스')).toHaveValue('0.0.*');
    expect(screen.getByLabelText('환경')).toHaveValue('staging');
    expect(screen.getByLabelText('메시지 패턴')).toHaveValue('*debug*');
    expect(
      screen.getByRole('button', { name: '필터 저장' }),
    ).toBeInTheDocument();

    expect(
      screen.getByRole('heading', { level: 3, name: '위험 구역' }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('프로젝트 이름 확인')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: '프로젝트 삭제' }),
    ).toBeDisabled();
  });

  it('renders the not-found state in Korean', async () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'ko');
    stubApi({
      project: () =>
        errorResponse(404, { error: 'Project 7 was not found.' }),
      keys: () => errorResponse(404, { error: 'Project 7 was not found.' }),
    });

    renderPage();

    expect(
      await screen.findByText('프로젝트를 찾을 수 없습니다'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: '프로젝트 목록으로' }),
    ).toHaveAttribute('href', '#/projects');
  });
});
