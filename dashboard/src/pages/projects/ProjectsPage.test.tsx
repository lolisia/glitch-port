import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { CreatedProject, ProjectListItem } from '../../api/types';
import { ProjectsPage } from './ProjectsPage';
import { copyToClipboard } from './copy';

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

const API = 'http://localhost:5090/api/manage';
const AUTH_HEADERS = { Authorization: 'Bearer dev-admin-token' };

const projects: ProjectListItem[] = [
  {
    id: 1,
    name: 'Starter',
    slug: 'starter-project',
    platforms: ['unreal'],
    created_at: '2026-01-01T00:00:00Z',
    issue_count: 4,
    key_count: 2,
  },
  {
    id: 2,
    name: 'Space game',
    slug: 'space-game',
    platforms: ['unity', 'dotnet'],
    created_at: '2026-01-02T00:00:00Z',
    issue_count: 0,
    key_count: 1,
  },
];

const created: CreatedProject = {
  id: 7,
  name: 'New game',
  slug: 'new-game',
  platforms: [],
  created_at: '2026-01-03T00:00:00Z',
  dsn: 'http://public-key@localhost:5080/7',
};

const createdListItem: ProjectListItem = {
  id: 7,
  name: 'New game',
  slug: 'new-game',
  platforms: [],
  created_at: '2026-01-03T00:00:00Z',
  issue_count: 0,
  key_count: 1,
};

interface StubOptions {
  /** GET /projects responses, consumed in order; the last one repeats. */
  lists?: MockResponse[];
  create?: (body: unknown) => MockResponse;
}

function stubApi(options: StubOptions = {}) {
  const lists = options.lists ?? [jsonResponse(projects)];

  const fetchMock = vi.fn(
    async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();

      if (url.pathname === '/api/manage/projects') {
        if (method === 'POST') {
          const body =
            typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;

          return (options.create ?? (() => jsonResponse(created, 201)))(body);
        }

        const next = lists.length > 1 ? lists.shift()! : lists[0];
        return next;
      }

      throw new Error(`Unexpected fetch: ${method} ${url.pathname}`);
    },
  );

  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function listCalls(fetchMock: ReturnType<typeof stubApi>) {
  return fetchMock.mock.calls.filter(
    ([, init]) => (init?.method ?? 'GET').toUpperCase() === 'GET',
  );
}

function postCalls(fetchMock: ReturnType<typeof stubApi>) {
  return fetchMock.mock.calls.filter(
    ([, init]) => (init?.method ?? 'GET').toUpperCase() === 'POST',
  );
}

function createBody(call: unknown): unknown {
  const init = (call as [RequestInfo | URL, RequestInit | undefined])[1];
  return JSON.parse(String(init?.body));
}

afterEach(() => {
  Reflect.deleteProperty(navigator, 'clipboard');
  Reflect.deleteProperty(document, 'execCommand');
});

describe('ProjectsPage', () => {
  it('renders the project table with links, slugs and counters', async () => {
    const fetchMock = stubApi();

    render(<ProjectsPage onProjectsChanged={vi.fn()} />);

    expect(screen.getByText('Loading...')).toBeInTheDocument();

    const table = await screen.findByRole('table');
    expect(
      within(table)
        .getAllByRole('columnheader')
        .map((header) => header.textContent),
    ).toEqual(['Name', 'Slug', 'Platforms', 'Unresolved issues', 'Active keys']);

    const rows = within(table).getAllByRole('row');
    expect(rows).toHaveLength(3);

    const first = within(rows[1]);
    expect(first.getByRole('link', { name: 'Starter' })).toHaveAttribute(
      'href',
      '#/projects/1',
    );
    expect(first.getByText('starter-project')).toBeInTheDocument();
    expect(first.getByText('unreal')).toHaveClass('platform-chip');
    expect(first.getByText('4')).toBeInTheDocument();
    expect(first.getByText('2')).toBeInTheDocument();

    const second = within(rows[2]);
    expect(second.getByRole('link', { name: 'Space game' })).toHaveAttribute(
      'href',
      '#/projects/2',
    );
    expect(second.getByText('space-game')).toBeInTheDocument();
    expect(second.getByText('unity')).toHaveClass('platform-chip');
    expect(second.getByText('dotnet')).toHaveClass('platform-chip');
    expect(second.getByText('0')).toBeInTheDocument();
    expect(second.getByText('1')).toBeInTheDocument();

    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects`, {
      headers: AUTH_HEADERS,
    });
  });

  it('renders the empty state when there are no projects', async () => {
    stubApi({ lists: [jsonResponse([])] });

    render(<ProjectsPage onProjectsChanged={vi.fn()} />);

    expect(await screen.findByText('No projects yet.')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('renders the load error', async () => {
    stubApi({ lists: [errorResponse(500, { error: 'boom' })] });

    render(<ProjectsPage onProjectsChanged={vi.fn()} />);

    expect(
      await screen.findByText('Failed to load projects: HTTP 500: boom'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('reloads the list with the Refresh button', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi({
      lists: [jsonResponse(projects), jsonResponse([])],
    });

    render(<ProjectsPage onProjectsChanged={vi.fn()} />);
    await screen.findByRole('table');

    await user.click(screen.getByRole('button', { name: 'Refresh' }));

    expect(await screen.findByText('No projects yet.')).toBeInTheDocument();
    expect(listCalls(fetchMock)).toHaveLength(2);
  });

  it('has no platform input and shows an em dash for a project without platforms', async () => {
    stubApi({
      lists: [
        jsonResponse([
          {
            ...projects[0],
            id: 3,
            name: 'Fresh project',
            slug: 'fresh-project',
            platforms: [],
          },
        ]),
      ],
    });
    render(<ProjectsPage onProjectsChanged={vi.fn()} />);

    const table = await screen.findByRole('table');
    expect(screen.queryByLabelText('Platform')).not.toBeInTheDocument();
    expect(document.getElementById('project-platform-options')).toBeNull();

    const row = within(table).getAllByRole('row')[1];
    expect(within(row).getByText('—')).toBeInTheDocument();
  });

  it('creates a project and shows the returned DSN in a read-only copyable field', async () => {
    const user = userEvent.setup();
    const onProjectsChanged = vi.fn();
    const fetchMock = stubApi({
      lists: [jsonResponse([]), jsonResponse([...projects, createdListItem])],
    });

    render(<ProjectsPage onProjectsChanged={onProjectsChanged} />);

    expect(await screen.findByText('No projects yet.')).toBeInTheDocument();
    expect(screen.getByLabelText('Name')).toBeRequired();
    expect(
      screen.getByRole('button', { name: 'Create project' }),
    ).toBeDisabled();

    await user.type(screen.getByLabelText('Name'), '  New game  ');
    await user.click(screen.getByRole('button', { name: 'Create project' }));

    const dsn = await screen.findByLabelText('DSN');
    expect(dsn).toHaveValue(created.dsn);
    expect(dsn).toHaveAttribute('readonly');
    expect(screen.getByRole('link', { name: 'Open project' })).toHaveAttribute(
      'href',
      '#/projects/7',
    );
    expect(onProjectsChanged).toHaveBeenCalledTimes(1);

    const posts = postCalls(fetchMock);
    expect(posts).toHaveLength(1);
    expect(posts[0][0]).toBe(`${API}/projects`);
    expect(posts[0][1]).toEqual({
      method: 'POST',
      headers: { ...AUTH_HEADERS, 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: 'New game' }),
    });

    // The page reloads its own list so the created project is selectable.
    expect(
      await screen.findByRole('link', { name: 'New game' }),
    ).toHaveAttribute('href', '#/projects/7');
    expect(listCalls(fetchMock)).toHaveLength(2);
  });

  it('creates a project with only the name', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi({ lists: [jsonResponse([])] });

    render(<ProjectsPage onProjectsChanged={vi.fn()} />);
    await screen.findByText('No projects yet.');

    await user.type(screen.getByLabelText('Name'), 'Only name');
    await user.click(screen.getByRole('button', { name: 'Create project' }));

    await screen.findByLabelText('DSN');

    expect(createBody(postCalls(fetchMock)[0])).toEqual({ name: 'Only name' });
  });

  it('surfaces the 409 conflict text from the API', async () => {
    const user = userEvent.setup();
    const onProjectsChanged = vi.fn();
    stubApi({
      lists: [jsonResponse([])],
      create: () =>
        errorResponse(409, {
          error: "A project with slug 'dup' already exists.",
        }),
    });

    render(<ProjectsPage onProjectsChanged={onProjectsChanged} />);
    await screen.findByText('No projects yet.');

    await user.type(screen.getByLabelText('Name'), 'Dup');
    await user.click(screen.getByRole('button', { name: 'Create project' }));

    expect(
      await screen.findByText(
        "Failed to create project: HTTP 409: A project with slug 'dup' already exists.",
      ),
    ).toBeInTheDocument();
    expect(screen.queryByLabelText('DSN')).not.toBeInTheDocument();
    expect(onProjectsChanged).not.toHaveBeenCalled();
  });

  it('surfaces the 400 validation text from the API', async () => {
    const user = userEvent.setup();
    stubApi({
      lists: [jsonResponse([])],
      create: () =>
        errorResponse(400, {
          error:
            'slug must contain only lowercase letters, digits and single hyphens.',
        }),
    });

    render(<ProjectsPage onProjectsChanged={vi.fn()} />);
    await screen.findByText('No projects yet.');

    await user.type(screen.getByLabelText('Name'), 'Bad slug');
    await user.click(screen.getByRole('button', { name: 'Create project' }));

    expect(
      await screen.findByText(
        'Failed to create project: HTTP 400: slug must contain only lowercase letters, digits and single hyphens.',
      ),
    ).toBeInTheDocument();
    expect(screen.queryByLabelText('DSN')).not.toBeInTheDocument();
  });

  it('copies the created DSN with the clipboard API', async () => {
    const user = userEvent.setup();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    stubApi({ lists: [jsonResponse([]), jsonResponse([])] });

    render(<ProjectsPage onProjectsChanged={vi.fn()} />);
    await screen.findByText('No projects yet.');

    await user.type(screen.getByLabelText('Name'), 'New game');
    await user.click(screen.getByRole('button', { name: 'Create project' }));
    await screen.findByLabelText('DSN');

    await user.click(screen.getByRole('button', { name: 'Copy DSN' }));

    expect(writeText).toHaveBeenCalledWith(created.dsn);
    expect(await screen.findByRole('status')).toHaveTextContent('Copied');
  });

  it('falls back to a hidden textarea when the clipboard API is unavailable', async () => {
    const execCommand = vi.fn(() => true);
    Object.defineProperty(document, 'execCommand', {
      configurable: true,
      value: execCommand,
    });

    await expect(copyToClipboard('dsn-value')).resolves.toBe(true);

    expect(execCommand).toHaveBeenCalledWith('copy');
    expect(document.querySelectorAll('textarea')).toHaveLength(0);
  });

  it('reports a failed copy without throwing when no mechanism exists', async () => {
    await expect(copyToClipboard('dsn-value')).resolves.toBe(false);
  });
});
