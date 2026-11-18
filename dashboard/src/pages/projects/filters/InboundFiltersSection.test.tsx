import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { InboundFilters, ProjectDetail } from '../../../api/types';
import { I18nProvider } from '../../../i18n';
import { InboundFiltersSection } from './InboundFiltersSection';

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

const project: ProjectDetail = {
  id: 7,
  name: 'Star Raiders',
  slug: 'star-raiders',
  platforms: [],
  created_at: '2026-01-02T03:04:05Z',
  issue_count: 0,
  key_count: 0,
  inbound_filters: {
    releases: ['1.0.*'],
    environments: ['staging', 'dev'],
    message_patterns: ['*debug*'],
  },
  dsn: null,
};

function stubApi(response: MockResponse) {
  const fetchMock = vi.fn(
    async (_input: RequestInfo | URL, _init?: RequestInit) => response,
  );
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function renderSection(
  filters: InboundFilters,
  onSaved: (updated: ProjectDetail) => void = vi.fn(),
) {
  render(
    <I18nProvider>
      <InboundFiltersSection
        projectId={7}
        filters={filters}
        onSaved={onSaved}
      />
    </I18nProvider>,
  );
}

describe('InboundFiltersSection', () => {
  it('renders the three lists and the glob syntax help', () => {
    stubApi(jsonResponse(project));

    renderSection(project.inbound_filters!);

    expect(
      screen.getByRole('heading', { name: 'Inbound filters' }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('Releases')).toHaveValue('1.0.*');
    expect(screen.getByLabelText('Environments')).toHaveValue(
      'staging\ndev',
    );
    expect(screen.getByLabelText('Message patterns')).toHaveValue('*debug*');
    expect(
      screen.getByText(/dropped before they are stored/),
    ).toBeInTheDocument();
    expect(screen.getByText(/matching is case-insensitive/)).toBeInTheDocument();
  });

  it('PATCHes all three arrays with trimmed, non-empty lines', async () => {
    const user = userEvent.setup();
    const updated: ProjectDetail = {
      ...project,
      inbound_filters: {
        releases: ['0.0.*', '1.2.3'],
        environments: ['staging', 'production'],
        message_patterns: ['*debug*'],
      },
    };
    const fetchMock = stubApi(jsonResponse(updated));
    const onSaved = vi.fn();

    renderSection(
      { releases: [], environments: [], message_patterns: [] },
      onSaved,
    );

    await user.type(
      screen.getByLabelText('Releases'),
      '0.0.*\n\n 1.2.3 ',
    );
    await user.type(
      screen.getByLabelText('Environments'),
      'staging\n production',
    );
    await user.type(screen.getByLabelText('Message patterns'), '*debug*');
    await user.click(screen.getByRole('button', { name: 'Save filters' }));

    await waitFor(() => expect(onSaved).toHaveBeenCalledWith(updated));

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith(`${API}/projects/7`, {
      method: 'PATCH',
      headers: {
        Authorization: 'Bearer dev-admin-token',
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        inbound_filters: {
          releases: ['0.0.*', '1.2.3'],
          environments: ['staging', 'production'],
          message_patterns: ['*debug*'],
        },
      }),
    });

    expect(await screen.findByRole('status')).toHaveTextContent(
      'Filters saved',
    );
  });

  it('sends empty arrays when a list is emptied', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi(jsonResponse(project));

    renderSection(project.inbound_filters!);

    await user.clear(screen.getByLabelText('Releases'));
    await user.click(screen.getByRole('button', { name: 'Save filters' }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]!.body))).toEqual({
      inbound_filters: {
        releases: [],
        environments: ['staging', 'dev'],
        message_patterns: ['*debug*'],
      },
    });
  });

  it('reports a failed save inside the section and keeps the edits', async () => {
    const user = userEvent.setup();
    stubApi(errorResponse(500, { error: 'boom' }));
    const onSaved = vi.fn();

    renderSection(project.inbound_filters!, onSaved);

    await user.clear(screen.getByLabelText('Environments'));
    await user.type(screen.getByLabelText('Environments'), 'qa');
    await user.click(screen.getByRole('button', { name: 'Save filters' }));

    expect(
      await screen.findByText(
        'Failed to save inbound filters: HTTP 500: boom',
      ),
    ).toBeInTheDocument();
    expect(onSaved).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Environments')).toHaveValue('qa');
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });
});
