import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import type {
  EventDetail,
  IssueDetail,
  ProjectDetail,
  ProjectKey,
  ProjectListItem,
  Release,
} from './api/types';
import { I18nProvider, LOCALE_STORAGE_KEY } from './i18n';
import { ThemeProvider } from './theme/ThemeProvider';

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

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });

  return { promise, resolve };
}

const projects: ProjectListItem[] = [
  {
    id: 1,
    name: 'Starter',
    slug: 'starter',
    platforms: ['unreal'],
    created_at: '2026-01-01T00:00:00Z',
    issue_count: 3,
    key_count: 1,
  },
  {
    id: 2,
    name: 'Second project',
    slug: 'second-project',
    platforms: ['unity'],
    created_at: '2026-01-02T00:00:00Z',
    issue_count: 0,
    key_count: 2,
  },
];

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
  first_release: '1.0.0',
  last_release: '1.0.0',
  last_environment: 'production',
  resolved_at: null,
  resolved_in_release: null,
  regressed_at: null,
  regression_count: 0,
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

const projectDetail: ProjectDetail = {
  ...projects[0],
  dsn: 'http://a1b2c3d4e5f60718293a4b5c6d7e8f90@localhost:5080/1',
};

const projectKeys: ProjectKey[] = [
  {
    id: 5,
    public_key: 'a1b2c3d4e5f60718293a4b5c6d7e8f90',
    label: 'production',
    is_active: true,
    rate_limit_per_minute: null,
    created_at: '2026-01-01T00:00:00Z',
    revoked_at: null,
    dsn: 'http://a1b2c3d4e5f60718293a4b5c6d7e8f90@localhost:5080/1',
  },
];

const release: Release = {
  release: '1.0.0',
  first_seen: '2026-01-01T00:00:00Z',
  last_seen: '2026-01-02T00:00:00Z',
  event_count: 12,
  issue_count: 3,
  new_issue_count: 2,
  unresolved_issue_count: 1,
};

const createdKey: ProjectKey = {
  id: 9,
  public_key: '0123456789abcdef0123456789abcdef',
  label: 'staging',
  is_active: true,
  rate_limit_per_minute: null,
  created_at: '2026-01-04T00:00:00Z',
  revoked_at: null,
  dsn: 'http://0123456789abcdef0123456789abcdef@localhost:5080/1',
};

type ProjectsResponder =
  | MockResponse
  | Promise<MockResponse>
  | (() => MockResponse | Promise<MockResponse>);

type ProjectResponder = (
  projectId: number,
) => MockResponse | Promise<MockResponse>;

interface StubOptions {
  projects?: ProjectsResponder;
  /** Overrides GET /projects/{id}; defaults to a copy of `projectDetail`. */
  projectDetail?: ProjectResponder;
  /** Overrides GET /projects/{id}/keys. */
  projectKeys?: ProjectResponder;
  /** Invoked when DELETE /projects/{id} is served the 204 response. */
  onProjectDeleted?: (projectId: number) => void;
  /** Invoked when PATCH /projects/{id} is served the updated project. */
  onProjectUpdated?: (projectId: number) => void;
}

/**
 * Dispatches by URL, matching the most specific route first. The project list
 * can be overridden (with a promise) to test the loading, empty and error
 * states of the selector. Project detail and key routes default to the single
 * fixture project so the project routes can be exercised end to end.
 */
function stubFetch(options: StubOptions = {}) {
  const fetchMock = vi.fn(
    async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost');
      const path = url.pathname;
      const method = (init?.method ?? 'GET').toUpperCase();

      if (path === '/api/manage/projects' && method === 'GET') {
        const responder = options.projects ?? jsonResponse(projects);
        return typeof responder === 'function' ? responder() : responder;
      }
      if (path === '/api/manage/projects' && method === 'POST') {
        return jsonResponse(
          {
            id: 9,
            name: 'Created',
            slug: 'created',
            platforms: [],
            created_at: '2026-01-03T00:00:00Z',
            dsn: 'http://public-key@localhost:5080/9',
          },
          201,
        );
      }
      if (/^\/api\/manage\/projects\/\d+\/keys$/.test(path)) {
        const projectId = Number(path.split('/')[4]);

        if (method === 'GET') {
          const responder =
            options.projectKeys ?? (() => jsonResponse(projectKeys));
          return responder(projectId);
        }
        if (method === 'POST') {
          return jsonResponse(createdKey, 201);
        }
      }
      const projectMatch = /^\/api\/manage\/projects\/(\d+)$/.exec(path);
      if (projectMatch !== null) {
        const projectId = Number(projectMatch[1]);

        if (method === 'GET') {
          const responder =
            options.projectDetail ??
            (() => jsonResponse({ ...projectDetail, id: projectId }));
          return responder(projectId);
        }
        if (method === 'PATCH') {
          const patch =
            typeof init?.body === 'string' ? JSON.parse(init.body) : {};
          options.onProjectUpdated?.(projectId);
          return jsonResponse({ ...projectDetail, id: projectId, ...patch });
        }
        if (method === 'DELETE') {
          options.onProjectDeleted?.(projectId);
          return jsonResponse(undefined, 204);
        }
      }
      const keyMatch = /^\/api\/manage\/keys\/(\d+)$/.exec(path);
      if (keyMatch !== null) {
        if (method === 'PATCH') {
          return jsonResponse(projectKeys[0]);
        }
        if (method === 'DELETE') {
          return jsonResponse(undefined, 204);
        }
      }
      if (/^\/api\/manage\/messages$/.test(path)) {
        return jsonResponse([]);
      }
      if (/^\/api\/manage\/projects\/\d+\/issues$/.test(path)) {
        return jsonResponse({ items: [], next_cursor: null });
      }
      if (/^\/api\/manage\/projects\/\d+\/releases$/.test(path)) {
        return jsonResponse({ items: [release], next_cursor: null });
      }
      if (/^\/api\/manage\/projects\/\d+\/stats$/.test(path)) {
        return jsonResponse({ buckets: [], by_platform: [] });
      }
      if (/^\/api\/manage\/projects\/\d+\/labels$/.test(path)) {
        return jsonResponse([]);
      }
      if (/^\/api\/manage\/issues\/\d+\/tags$/.test(path)) {
        return jsonResponse({
          issue_id: issueDetail.id,
          sample_size: 0,
          user_count: 0,
          tags: [],
        });
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
    },
  );

  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

type FetchMock = ReturnType<typeof stubFetch>;

function requestUrls(fetchMock: FetchMock): URL[] {
  return fetchMock.mock.calls.map(([input]) =>
    new URL(String(input), 'http://localhost'),
  );
}

function issueRequests(fetchMock: FetchMock, projectId: number): URL[] {
  return requestUrls(fetchMock).filter(
    (url) => url.pathname === `/api/manage/projects/${projectId}/issues`,
  );
}

/** Requests whose path is scoped to a concrete project. */
function projectScopedRequests(fetchMock: FetchMock): URL[] {
  return requestUrls(fetchMock).filter((url) =>
    /^\/api\/manage\/projects\/\d+\//.test(url.pathname),
  );
}

function optionLabels(select: HTMLElement): string[] {
  return Array.from(select.querySelectorAll('option')).map(
    (option) => option.textContent ?? '',
  );
}

/** Renders the app inside the providers that supply the theme and locale. */
function renderApp() {
  return render(
    <ThemeProvider>
      <I18nProvider>
        <App />
      </I18nProvider>
    </ThemeProvider>,
  );
}

beforeEach(() => {
  window.history.replaceState(null, '', '/');
  window.localStorage.clear();
  document.documentElement.removeAttribute('data-theme');
  document.documentElement.style.removeProperty('color-scheme');
  document.documentElement.lang = 'en';
});

describe('App', () => {
  it('renders the issues page for a blank hash and rewrites it to #/issues', async () => {
    stubFetch();

    renderApp();

    expect(
      screen.getByRole('heading', { level: 1, name: 'glitch-port' }),
    ).toBeInTheDocument();
    expect(screen.getByTestId('issues-page')).toBeInTheDocument();

    const picker = await screen.findByLabelText('Project');
    expect(picker).toHaveValue('1');
    expect(optionLabels(picker)).toEqual(['Starter', 'Second project']);

    await waitFor(() => expect(window.location.hash).toBe('#/issues'));
  });

  it('rewrites an unknown hash to #/issues', async () => {
    window.history.replaceState(null, '', '/#/not-a-route');
    stubFetch();

    renderApp();

    expect(screen.getByTestId('issues-page')).toBeInTheDocument();

    await waitFor(() => expect(window.location.hash).toBe('#/issues'));
  });

  it('keeps the projects routes instead of rewriting them', async () => {
    window.history.replaceState(null, '', '/#/projects');
    stubFetch();

    renderApp();

    expect(await screen.findByTestId('projects-page')).toBeInTheDocument();
    expect(window.location.hash).toBe('#/projects');
  });

  it('renders the issue detail page for #/issues/42', async () => {
    window.history.replaceState(null, '', '/#/issues/42');
    stubFetch();

    renderApp();

    expect(screen.getByTestId('issue-detail-page')).toBeInTheDocument();
    expect(screen.queryByTestId('issues-page')).not.toBeInTheDocument();

    // The issue detail waits for the active project (labels are project scoped)
    // and then loads the issue.
    expect(await screen.findByTestId('issue-detail-header')).toBeInTheDocument();
    expect(window.location.hash).toBe('#/issues/42');
  });

  it('renders the messages page for #/messages', async () => {
    window.history.replaceState(null, '', '/#/messages');
    stubFetch();

    renderApp();

    expect(screen.getByTestId('messages-page')).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Messages' }),
    ).toBeInTheDocument();
    expect(await screen.findByText('No messages yet.')).toBeInTheDocument();
  });

  it('renders the releases route without rewriting the hash', async () => {
    window.history.replaceState(null, '', '/#/releases');
    stubFetch();

    renderApp();

    expect(await screen.findByTestId('releases-page')).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Releases' }),
    ).toHaveAttribute('aria-current', 'page');
    expect(await screen.findByRole('link', { name: '1.0.0' })).toBeInTheDocument();
    expect(window.location.hash).toBe('#/releases');
  });

  it('navigates to #/releases from the nav tab', async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, '', '/#/issues');
    stubFetch();

    renderApp();

    const releasesTab = screen.getByRole('link', { name: 'Releases' });
    expect(releasesTab).not.toHaveAttribute('aria-current');

    await user.click(releasesTab);

    expect(await screen.findByTestId('releases-page')).toBeInTheDocument();
    expect(releasesTab).toHaveAttribute('aria-current', 'page');
    expect(
      screen.getByRole('link', { name: 'Issues' }),
    ).not.toHaveAttribute('aria-current');
    expect(window.location.hash).toBe('#/releases');
  });

  it('switches screens and aria-current when the tabs are clicked', async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, '', '/#/issues');
    stubFetch();

    renderApp();

    const issuesTab = screen.getByRole('link', { name: 'Issues' });
    const messagesTab = screen.getByRole('link', { name: 'Messages' });
    const projectsTab = screen.getByRole('link', { name: 'Projects' });
    expect(issuesTab).toHaveAttribute('aria-current', 'page');
    expect(messagesTab).not.toHaveAttribute('aria-current');
    expect(projectsTab).not.toHaveAttribute('aria-current');

    await user.click(messagesTab);

    expect(await screen.findByTestId('messages-page')).toBeInTheDocument();
    expect(messagesTab).toHaveAttribute('aria-current', 'page');
    expect(issuesTab).not.toHaveAttribute('aria-current');
    expect(window.location.hash).toBe('#/messages');

    await user.click(projectsTab);

    expect(await screen.findByTestId('projects-page')).toBeInTheDocument();
    expect(projectsTab).toHaveAttribute('aria-current', 'page');
    expect(issuesTab).not.toHaveAttribute('aria-current');
    expect(messagesTab).not.toHaveAttribute('aria-current');
    expect(window.location.hash).toBe('#/projects');

    await user.click(issuesTab);

    expect(await screen.findByTestId('issues-page')).toBeInTheDocument();
    expect(issuesTab).toHaveAttribute('aria-current', 'page');
    expect(messagesTab).not.toHaveAttribute('aria-current');
    expect(projectsTab).not.toHaveAttribute('aria-current');
  });

  it('switches screens on a hashchange event (back/forward)', () => {
    window.history.replaceState(null, '', '/#/issues');
    stubFetch();

    renderApp();

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
    stubFetch();

    renderApp();

    expect(screen.getByTestId('issues-page')).toBeInTheDocument();
    expect(window.location.hash).toBe('#/issues?status=resolved&q=null');
  });

  it('persists the first project when nothing is stored or configured', async () => {
    stubFetch();

    renderApp();

    expect(await screen.findByLabelText('Project')).toHaveValue('1');
    expect(window.localStorage.getItem('gp.projectId')).toBe('1');
  });

  it('selects the stored project and loads its issues', async () => {
    window.localStorage.setItem('gp.projectId', '2');
    const fetchMock = stubFetch();

    renderApp();

    expect(await screen.findByLabelText('Project')).toHaveValue('2');

    await waitFor(() =>
      expect(issueRequests(fetchMock, 2).length).toBeGreaterThan(0),
    );
    expect(issueRequests(fetchMock, 1)).toHaveLength(0);
  });

  it('uses a valid VITE_PROJECT_ID when nothing is stored', async () => {
    vi.stubEnv('VITE_PROJECT_ID', '2');
    const fetchMock = stubFetch();

    renderApp();

    expect(await screen.findByLabelText('Project')).toHaveValue('2');
    expect(window.localStorage.getItem('gp.projectId')).toBe('2');
    await waitFor(() =>
      expect(issueRequests(fetchMock, 2).length).toBeGreaterThan(0),
    );
  });

  it('ignores an invalid VITE_PROJECT_ID and selects the first project', async () => {
    vi.stubEnv('VITE_PROJECT_ID', 'not-a-project');
    const fetchMock = stubFetch();

    renderApp();

    expect(await screen.findByLabelText('Project')).toHaveValue('1');
    expect(window.localStorage.getItem('gp.projectId')).toBe('1');
    await waitFor(() =>
      expect(issueRequests(fetchMock, 1).length).toBeGreaterThan(0),
    );
  });

  it('ignores a VITE_PROJECT_ID that is not in the loaded project list', async () => {
    vi.stubEnv('VITE_PROJECT_ID', '99');
    stubFetch();

    renderApp();

    expect(await screen.findByLabelText('Project')).toHaveValue('1');
    expect(window.localStorage.getItem('gp.projectId')).toBe('1');
  });

  it('passes the active project to the issue detail label list', async () => {
    window.localStorage.setItem('gp.projectId', '2');
    window.history.replaceState(null, '', '/#/issues/42');
    const fetchMock = stubFetch();

    renderApp();

    expect(
      await screen.findByTestId('issue-detail-header'),
    ).toBeInTheDocument();

    const scopedPaths = projectScopedRequests(fetchMock).map(
      (url) => url.pathname,
    );
    expect(scopedPaths).toContain('/api/manage/projects/2/labels');
    expect(
      scopedPaths.every((path) => path.startsWith('/api/manage/projects/2/')),
    ).toBe(true);
  });

  it.each(['99', 'abc', '0', '-4', '2.5'])(
    'falls back to the first project when gp.projectId is %j',
    async (stored) => {
      window.localStorage.setItem('gp.projectId', stored);
      stubFetch();

      renderApp();

      expect(await screen.findByLabelText('Project')).toHaveValue('1');
      expect(window.localStorage.getItem('gp.projectId')).toBe('1');
    },
  );

  it('stores the selected project and refetches its issues', async () => {
    const user = userEvent.setup();
    const fetchMock = stubFetch();

    renderApp();

    const picker = await screen.findByLabelText('Project');
    await waitFor(() =>
      expect(issueRequests(fetchMock, 1).length).toBeGreaterThan(0),
    );

    await user.selectOptions(picker, '2');

    expect(window.localStorage.getItem('gp.projectId')).toBe('2');
    expect(screen.getByLabelText('Project')).toHaveValue('2');
    await waitFor(() =>
      expect(issueRequests(fetchMock, 2).length).toBeGreaterThan(0),
    );
    expect(issueRequests(fetchMock, 1).length).toBeGreaterThan(0);
  });

  it('shows an inline state and sends no project-scoped requests while loading', async () => {
    const pending = deferred<MockResponse>();
    const fetchMock = stubFetch({ projects: () => pending.promise });

    renderApp();

    expect(screen.getByTestId('project-picker-state')).toHaveTextContent(
      'Loading projects...',
    );
    expect(screen.queryByLabelText('Project')).not.toBeInTheDocument();
    expect(screen.getByText(/No project selected/)).toBeInTheDocument();
    expect(projectScopedRequests(fetchMock)).toHaveLength(0);

    pending.resolve(jsonResponse(projects));

    expect(await screen.findByLabelText('Project')).toHaveValue('1');
    await waitFor(() =>
      expect(projectScopedRequests(fetchMock).length).toBeGreaterThan(0),
    );
  });

  it('renders an inline state for an empty project list without project requests', async () => {
    const fetchMock = stubFetch({ projects: jsonResponse([]) });

    renderApp();

    expect(await screen.findByTestId('project-picker-state')).toHaveTextContent(
      'No projects yet',
    );
    expect(screen.getByTestId('project-picker-state').textContent).toBe(
      'No projects yet — create one.',
    );
    expect(screen.queryByLabelText('Project')).not.toBeInTheDocument();
    await waitFor(() =>
      expect(screen.getByText(/No project selected/)).toBeInTheDocument(),
    );
    expect(projectScopedRequests(fetchMock)).toHaveLength(0);
    expect(window.localStorage.getItem('gp.projectId')).toBeNull();
  });

  it('clears a stale stored id when the project list is empty', async () => {
    window.localStorage.setItem('gp.projectId', '9');
    stubFetch({ projects: jsonResponse([]) });

    renderApp();

    expect(await screen.findByTestId('project-picker-state')).toHaveTextContent(
      'No projects yet',
    );
    await waitFor(() =>
      expect(window.localStorage.getItem('gp.projectId')).toBeNull(),
    );
  });

  it('falls back to the first project when a reload drops the active one', async () => {
    const user = userEvent.setup();
    const createdItem: ProjectListItem = {
      id: 9,
      name: 'Created',
      slug: 'created',
      platforms: [],
      created_at: '2026-01-03T00:00:00Z',
      issue_count: 0,
      key_count: 1,
    };
    let listCalls = 0;

    window.localStorage.setItem('gp.projectId', '2');
    const fetchMock = stubFetch({
      projects: () => {
        listCalls += 1;

        if (listCalls === 1) {
          // Initial App load: the stored project still exists.
          return jsonResponse(projects);
        }
        if (listCalls === 2) {
          // The projects screen loads its own copy while project 2 is gone.
          return jsonResponse([projects[0]]);
        }

        // The reload after a create re-resolves the selection.
        return jsonResponse([projects[0], createdItem]);
      },
    });

    renderApp();

    expect(await screen.findByLabelText('Project')).toHaveValue('2');

    await user.click(screen.getByRole('link', { name: 'Projects' }));

    const page = await screen.findByTestId('projects-page');
    await within(page).findByRole('table');

    await user.type(screen.getByLabelText('Name'), 'Created');
    await user.click(screen.getByRole('button', { name: 'Create project' }));

    await waitFor(() =>
      expect(screen.getByLabelText('Project')).toHaveValue('1'),
    );
    expect(window.localStorage.getItem('gp.projectId')).toBe('1');
    await waitFor(() =>
      expect(screen.getByLabelText('Project')).toHaveTextContent('Created'),
    );
    expect(listCalls).toBeGreaterThanOrEqual(3);

    // The issues screen follows the re-resolved project instead of issuing
    // new requests for the deleted one.
    const deletedProjectRequests = issueRequests(fetchMock, 2).length;
    await user.click(screen.getByRole('link', { name: 'Issues' }));

    await waitFor(() =>
      expect(issueRequests(fetchMock, 1).length).toBeGreaterThan(0),
    );
    expect(issueRequests(fetchMock, 2)).toHaveLength(deletedProjectRequests);
  });

  it('renders an inline state when the project list fails without project requests', async () => {
    const fetchMock = stubFetch({ projects: errorResponse(500) });

    renderApp();

    expect(await screen.findByTestId('project-picker-state')).toHaveTextContent(
      'Projects unavailable.',
    );
    await waitFor(() =>
      expect(screen.getByText(/No project selected/)).toBeInTheDocument(),
    );
    expect(projectScopedRequests(fetchMock)).toHaveLength(0);
  });

  it('renders the project detail page for #/projects/3', async () => {
    window.history.replaceState(null, '', '/#/projects/3');
    stubFetch();

    renderApp();

    expect(screen.queryByTestId('issues-page')).not.toBeInTheDocument();
    expect(
      await screen.findByRole('heading', { level: 2, name: 'Starter' }),
    ).toBeInTheDocument();
    expect(screen.getByTestId('project-detail-page')).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Projects' }),
    ).toHaveAttribute('aria-current', 'page');
    expect(await screen.findByText(projectKeys[0].public_key)).toBeInTheDocument();
    expect(window.location.hash).toBe('#/projects/3');
  });

  it('reloads the project selector after a rename on the detail page', async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, '', '/#/projects/3');

    let renamed = false;
    stubFetch({
      projects: () =>
        jsonResponse(
          renamed
            ? [{ ...projects[0], name: 'Starter Renamed' }, projects[1]]
            : projects,
        ),
      projectDetail: (projectId) =>
        jsonResponse({
          ...projectDetail,
          id: projectId,
          name: renamed ? 'Starter Renamed' : 'Starter',
        }),
      onProjectUpdated: () => {
        renamed = true;
      },
    });

    renderApp();

    await screen.findByRole('heading', { level: 2, name: 'Starter' });

    const nameInput = screen.getByLabelText('Name');
    await user.clear(nameInput);
    await user.type(nameInput, 'Starter Renamed');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() =>
      expect(optionLabels(screen.getByLabelText('Project'))).toContain(
        'Starter Renamed',
      ),
    );
    expect(screen.getByLabelText('Project')).toHaveValue('1');
  });

  it('deletes the active project, navigates to #/projects and falls back to the first remaining project', async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, '', '/#/projects/1');
    window.localStorage.setItem('gp.projectId', '1');

    let deleted = false;
    stubFetch({
      projects: () => jsonResponse(deleted ? [projects[1]] : projects),
      projectDetail: (projectId) =>
        jsonResponse({ ...projectDetail, id: projectId, name: 'Starter' }),
      onProjectDeleted: () => {
        deleted = true;
      },
    });

    renderApp();

    expect(
      await screen.findByRole('heading', { level: 2, name: 'Starter' }),
    ).toBeInTheDocument();
    expect(await screen.findByLabelText('Project')).toHaveValue('1');

    await user.type(screen.getByLabelText('Confirm project name'), 'Starter');
    await user.click(screen.getByRole('button', { name: 'Delete project' }));

    await waitFor(() => expect(window.location.hash).toBe('#/projects'));
    expect(await screen.findByTestId('projects-page')).toBeInTheDocument();

    await waitFor(() =>
      expect(screen.getByLabelText('Project')).toHaveValue('2'),
    );
    expect(window.localStorage.getItem('gp.projectId')).toBe('2');
  });

  it('shows the project not-found state for an unknown project id', async () => {
    window.history.replaceState(null, '', '/#/projects/99');
    stubFetch({
      projectDetail: () =>
        errorResponse(404, { error: 'Project 99 was not found.' }),
      projectKeys: () =>
        errorResponse(404, { error: 'Project 99 was not found.' }),
    });

    renderApp();

    expect(await screen.findByText('Project not found')).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Back to projects' }),
    ).toHaveAttribute('href', '#/projects');
    expect(window.location.hash).toBe('#/projects/99');
  });

  it('renders no language or theme controls in the shell on #/issues', async () => {
    window.history.replaceState(null, '', '/#/issues');
    stubFetch();

    renderApp();

    expect(await screen.findByLabelText('Project')).toBeInTheDocument();
    expect(screen.queryByLabelText('Language')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Theme')).not.toBeInTheDocument();
    expect(screen.queryByRole('radiogroup')).not.toBeInTheDocument();
  });

  it('navigates to the settings page from the sidebar footer link', async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, '', '/#/issues');
    stubFetch();

    renderApp();

    const settingsLink = screen.getByRole('link', { name: 'Settings' });
    expect(settingsLink).toHaveAttribute('href', '#/settings');
    expect(settingsLink).not.toHaveAttribute('aria-current');

    await user.click(settingsLink);

    expect(await screen.findByTestId('settings-page')).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { level: 2, name: 'Settings' }),
    ).toBeInTheDocument();
    expect(settingsLink).toHaveAttribute('aria-current', 'page');
    expect(
      screen.getByRole('link', { name: 'Issues' }),
    ).not.toHaveAttribute('aria-current');
    expect(window.location.hash).toBe('#/settings');
  });

  it('applies a stored dark theme before the theme radio changes', async () => {
    window.localStorage.setItem('glitchport.theme', 'dark');
    window.history.replaceState(null, '', '/#/settings');
    stubFetch();

    renderApp();

    const appearance = await screen.findByRole('radiogroup', {
      name: 'Theme',
    });
    expect(
      within(appearance).getByRole('radio', { name: 'Dark' }),
    ).toBeChecked();
    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(document.documentElement.style.colorScheme).toBe('dark');
    expect(window.localStorage.getItem('glitchport.theme')).toBe('dark');
  });

  it('updates the document theme and persists the theme preference', async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, '', '/#/settings');
    stubFetch();

    renderApp();

    const appearance = await screen.findByRole('radiogroup', {
      name: 'Theme',
    });
    const system = within(appearance).getByRole('radio', { name: 'System' });
    const light = within(appearance).getByRole('radio', { name: 'Light' });
    const dark = within(appearance).getByRole('radio', { name: 'Dark' });

    // jsdom has no matchMedia, so `system` resolves to light.
    expect(system).toBeChecked();
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(document.documentElement.style.colorScheme).toBe('light');
    expect(window.localStorage.getItem('glitchport.theme')).toBe('system');

    await user.click(dark);

    expect(dark).toBeChecked();
    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(document.documentElement.style.colorScheme).toBe('dark');
    expect(window.localStorage.getItem('glitchport.theme')).toBe('dark');

    await user.click(light);

    expect(light).toBeChecked();
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(document.documentElement.style.colorScheme).toBe('light');
    expect(window.localStorage.getItem('glitchport.theme')).toBe('light');

    await user.click(system);

    expect(system).toBeChecked();
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(window.localStorage.getItem('glitchport.theme')).toBe('system');
  });

  it('offers the language select with English and Korean and defaults to English for en-US', async () => {
    window.history.replaceState(null, '', '/#/settings');
    stubFetch();

    renderApp();

    expect(navigator.language).toBe('en-US');

    const language = await screen.findByLabelText('Language');
    expect(language).toHaveValue('en');
    expect(optionLabels(language)).toEqual(['English', '한국어']);

    expect(screen.getByRole('link', { name: 'Issues' })).toBeInTheDocument();
    expect(
      screen.getByRole('navigation', { name: 'Sections' }),
    ).toBeInTheDocument();
    expect(document.documentElement.lang).toBe('en');
    // The English default comes from the browser, not from a stored choice.
    expect(window.localStorage.getItem(LOCALE_STORAGE_KEY)).toBeNull();
  });

  it('switches the shell to Korean and back, persisting glitchport.locale', async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, '', '/#/settings');
    stubFetch();

    const { unmount } = renderApp();

    const language = await screen.findByLabelText('Language');

    await user.selectOptions(language, 'ko');

    expect(
      screen.getByRole('heading', { level: 2, name: '설정' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '이슈' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '릴리스' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '메시지' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '프로젝트' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '설정' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    expect(
      screen.getByRole('navigation', { name: '섹션' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('radiogroup', { name: '테마' })).toBeInTheDocument();
    expect(
      screen.getByRole('radio', { name: '시스템' }),
    ).toBeChecked();
    expect(await screen.findByLabelText('프로젝트')).toHaveValue('1');
    expect(screen.getByLabelText('Language')).toHaveValue('ko');
    expect(window.localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('ko');
    expect(document.documentElement.lang).toBe('ko');

    // A reload (remount) resolves the stored choice again.
    unmount();
    renderApp();

    expect(screen.getByRole('link', { name: '이슈' })).toBeInTheDocument();
    expect(document.documentElement.lang).toBe('ko');
    expect(screen.getByLabelText('Language')).toHaveValue('ko');

    await user.selectOptions(screen.getByLabelText('Language'), 'en');

    expect(
      screen.getByRole('heading', { level: 2, name: 'Settings' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Issues' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Messages' })).toBeInTheDocument();
    expect(window.localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('en');
    expect(document.documentElement.lang).toBe('en');
  });

  it('translates the project picker empty state and link', async () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'ko');
    stubFetch({ projects: jsonResponse([]) });

    renderApp();

    const state = await screen.findByTestId('project-picker-state');
    expect(state).toHaveTextContent('아직 프로젝트가 없습니다');
    expect(state).toHaveTextContent('하나 만들기');
    expect(state.querySelector('a')).toHaveAttribute('href', '#/projects');
  });

  it('translates the project picker loading and error states', async () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'ko');

    const pending = deferred<MockResponse>();
    stubFetch({ projects: () => pending.promise });

    const { unmount } = renderApp();

    expect(screen.getByTestId('project-picker-state')).toHaveTextContent(
      '프로젝트를 불러오는 중...',
    );

    pending.resolve(errorResponse(500));
    expect(await screen.findByTestId('project-picker-state')).toHaveTextContent(
      '프로젝트를 사용할 수 없습니다.',
    );

    unmount();
    stubFetch({ projects: errorResponse(500) });
    renderApp();

    expect(await screen.findByTestId('project-picker-state')).toHaveTextContent(
      '프로젝트를 사용할 수 없습니다.',
    );
  });

  it('translates the issues, issue detail and projects screens after switching to Korean', async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, '', '/#/settings');
    stubFetch();

    renderApp();

    const language = await screen.findByLabelText('Language');
    await user.selectOptions(language, 'ko');

    await user.click(screen.getByRole('link', { name: '이슈' }));

    // Issue list
    expect(
      await screen.findByRole('heading', { level: 2, name: '이슈' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '미해결' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(await screen.findByText('이슈가 없습니다.')).toBeInTheDocument();
    expect(screen.getByLabelText('레벨')).toBeInTheDocument();
    expect(screen.getByLabelText('정렬')).toHaveValue('last_seen');

    // Issue detail
    act(() => {
      window.history.replaceState(null, '', '/#/issues/42');
      window.dispatchEvent(new HashChangeEvent('hashchange'));
    });

    const header = await screen.findByTestId('issue-detail-header');
    expect(
      within(header).getByRole('link', { name: '이슈 목록으로' }),
    ).toHaveAttribute('href', '#/issues');
    expect(within(header).getByText('레벨')).toBeInTheDocument();
    expect(within(header).getByText('플랫폼')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '이슈 삭제' })).toBeInTheDocument();

    // Projects
    act(() => {
      window.history.replaceState(null, '', '/#/projects');
      window.dispatchEvent(new HashChangeEvent('hashchange'));
    });

    const page = await screen.findByTestId('projects-page');
    expect(
      within(page).getByRole('heading', { level: 2, name: '프로젝트' }),
    ).toBeInTheDocument();
    const table = await within(page).findByRole('table');
    expect(
      within(table)
        .getAllByRole('columnheader')
        .map((cell) => cell.textContent),
    ).toEqual(['이름', '슬러그', '플랫폼', '미해결 이슈', '활성 키']);
    expect(
      within(page).getByRole('heading', { level: 2, name: '프로젝트 만들기' }),
    ).toBeInTheDocument();
  });

  it('keeps API error bodies untranslated in Korean', async () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'ko');
    window.history.replaceState(null, '', '/#/projects/3');
    stubFetch({
      projectDetail: () => errorResponse(500, { error: 'boom' }),
      projectKeys: () => errorResponse(500, { error: 'boom' }),
    });

    renderApp();

    expect(
      await screen.findByText(
        '프로젝트를 불러오지 못했습니다: HTTP 500: boom',
      ),
    ).toBeInTheDocument();
  });
});
