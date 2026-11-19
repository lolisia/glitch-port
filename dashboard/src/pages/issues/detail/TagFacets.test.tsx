import { render, screen, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { IssueTagsResponse } from '../../../api/types';
import { I18nProvider, LOCALE_STORAGE_KEY } from '../../../i18n';
import { TagFacets } from './TagFacets';

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

const TAGS_URL = 'http://localhost:5090/api/manage/issues/42/tags';
const AUTH_HEADERS = { Authorization: 'Bearer dev-admin-token' };

function stubTags(response: MockResponse) {
  const fetchMock = vi.fn(async () => response);
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const tags: IssueTagsResponse = {
  issue_id: 42,
  sample_size: 4,
  user_count: 2,
  tags: [
    {
      key: 'release',
      total: 4,
      distinct: 2,
      top: [
        { value: '1.2.3', count: 3 },
        { value: '1.2.4', count: 1 },
      ],
    },
    {
      key: 'os.name',
      total: 2,
      distinct: 1,
      top: [{ value: 'Windows 11', count: 2 }],
    },
    {
      key: 'tags.map',
      total: 2,
      distinct: 1,
      top: [{ value: 'dust', count: 2 }],
    },
  ],
};

function renderPanel() {
  return render(
    <I18nProvider>
      <TagFacets issueId={42} />
    </I18nProvider>,
  );
}

beforeEach(() => {
  window.localStorage.clear();
});

describe('TagFacets', () => {
  it('renders the sample counters and every facet with percentages, counts and bars', async () => {
    const fetchMock = stubTags(jsonResponse(tags));

    renderPanel();

    expect(screen.getByText('Loading...')).toBeInTheDocument();

    const panel = await screen.findByTestId('issue-tags');
    expect(panel).toHaveAttribute('data-slot', 'card');
    expect(within(panel).getByText('Tag distribution')).toBeInTheDocument();
    expect(within(panel).getByText('Sampled events: 4')).toBeInTheDocument();
    expect(within(panel).getByText('Distinct users: 2')).toBeInTheDocument();

    const facets = within(panel).getAllByTestId('tag-facet');
    expect(facets).toHaveLength(3);
    expect(facets[0]).toHaveAttribute('data-slot', 'card');

    // Fixed keys use their translated label.
    expect(within(facets[0]).getByText('Release')).toBeInTheDocument();
    expect(within(facets[0]).getByText('1.2.3')).toBeInTheDocument();
    expect(within(facets[0]).getByText('75%')).toBeInTheDocument();
    expect(within(facets[0]).getByText('25%')).toBeInTheDocument();
    expect(within(facets[0]).getByText('3')).toBeInTheDocument();
    expect(within(facets[0]).getByText('1')).toBeInTheDocument();

    const bars = facets[0].querySelectorAll('.issue-tag-value-bar');
    expect(Array.from(bars, (bar) => (bar as HTMLElement).style.width)).toEqual([
      '75%',
      '25%',
    ]);
    // Tailwind progress bars keep the width in style and the token colour.
    expect(bars[0]).toHaveClass('bg-primary');

    expect(within(facets[1]).getByText('OS')).toBeInTheDocument();
    expect(within(facets[1]).getByText('100%')).toBeInTheDocument();

    // Payload tag keys have no translation and keep the raw API key.
    expect(within(facets[2]).getByText('tags.map')).toBeInTheDocument();
    expect(within(facets[2]).getByText('dust')).toBeInTheDocument();

    expect(fetchMock).toHaveBeenCalledWith(TAGS_URL, {
      headers: AUTH_HEADERS,
    });
  });

  it('renders an empty state when the issue has no tag values', async () => {
    stubTags(
      jsonResponse({
        issue_id: 42,
        sample_size: 0,
        user_count: 0,
        tags: [],
      }),
    );

    renderPanel();

    const panel = await screen.findByTestId('issue-tags');
    expect(
      within(panel).getByText('No tag values recorded.'),
    ).toBeInTheDocument();
    expect(within(panel).getByText('Sampled events: 0')).toBeInTheDocument();
  });

  it('contains a failed request inside the panel', async () => {
    stubTags(jsonResponse({ error: 'boom' }, 500));

    renderPanel();

    const panel = await screen.findByTestId('issue-tags');
    expect(
      await within(panel).findByText('Failed to load tags: HTTP 500: boom'),
    ).toBeInTheDocument();
    // The heading stays so the failure is scoped to this panel.
    expect(within(panel).getByText('Tag distribution')).toBeInTheDocument();
    expect(screen.queryByText('Sampled events: 0')).not.toBeInTheDocument();
  });

  it('translates the fixed key labels and counters in Korean', async () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'ko');
    stubTags(jsonResponse(tags));

    renderPanel();

    const panel = await screen.findByTestId('issue-tags');
    expect(within(panel).getByText('태그 분포')).toBeInTheDocument();
    expect(within(panel).getByText('표본 이벤트: 4')).toBeInTheDocument();
    expect(within(panel).getByText('순 사용자: 2')).toBeInTheDocument();
    const facets = within(panel).getAllByTestId('tag-facet');
    expect(within(facets[0]).getByText('릴리스')).toBeInTheDocument();
    expect(within(facets[1]).getByText('운영체제')).toBeInTheDocument();
    expect(within(facets[2]).getByText('tags.map')).toBeInTheDocument();
  });
});
