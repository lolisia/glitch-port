import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type {
  AlertNotification,
  AlertRule,
  AlertRuleTestResult,
  CreateAlertRuleRequest,
  UpdateAlertRuleRequest,
} from '../../../api/types';
import { I18nProvider } from '../../../i18n';
import { AlertRulesSection } from './AlertRulesSection';

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
  return { ok: true, status: 204, json: async () => undefined };
}

const frequencyRule: AlertRule = {
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

const newIssueRule: AlertRule = {
  id: 12,
  project_id: 7,
  name: 'Any new crash',
  trigger: 'new_issue',
  min_level: null,
  environment: null,
  threshold: null,
  window_minutes: null,
  cooldown_minutes: 60,
  channel: 'slack',
  target_url: 'https://hooks.slack.com/services/abc',
  is_enabled: false,
  created_at: '2026-01-02T03:04:05Z',
  updated_at: '2026-01-02T03:04:05Z',
};

const failedNotification: AlertNotification = {
  id: 3,
  rule_id: 11,
  rule_name: 'Crash spike',
  issue_id: 42,
  trigger: 'event_frequency',
  status: 'failed',
  attempts: 3,
  last_error: 'HTTP 500 from target',
  created_at: '2026-01-02T04:00:00Z',
  sent_at: null,
};

const sentNotification: AlertNotification = {
  id: 4,
  rule_id: 12,
  rule_name: 'Any new crash',
  issue_id: null,
  trigger: 'new_issue',
  status: 'sent',
  attempts: 1,
  last_error: null,
  created_at: '2026-01-02T03:00:00Z',
  sent_at: '2026-01-02T03:00:05Z',
};

interface StubOptions {
  rules?: () => MockResponse | Promise<MockResponse>;
  notifications?: () => MockResponse | Promise<MockResponse>;
  createRule?: (body: CreateAlertRuleRequest) => MockResponse;
  updateRule?: (ruleId: number, body: UpdateAlertRuleRequest) => MockResponse;
  deleteRule?: (ruleId: number) => MockResponse;
  testRule?: (ruleId: number) => MockResponse;
}

function stubApi(options: StubOptions = {}) {
  const fetchMock = vi.fn(
    async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost');
      const path = url.pathname;
      const method = (init?.method ?? 'GET').toUpperCase();
      const body =
        typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;

      if (path === '/api/manage/projects/7/alert-rules' && method === 'GET') {
        return (options.rules ?? (() => jsonResponse({ items: [] })))();
      }

      if (path === '/api/manage/projects/7/alert-rules' && method === 'POST') {
        return (
          options.createRule ??
          ((created: CreateAlertRuleRequest) =>
            jsonResponse(
              {
                ...frequencyRule,
                id: 99,
                ...created,
              },
              201,
            ))
        )(body);
      }

      if (
        path === '/api/manage/projects/7/alert-notifications' &&
        method === 'GET'
      ) {
        return (options.notifications ?? (() => jsonResponse({ items: [] })))();
      }

      const ruleMatch = /^\/api\/manage\/alert-rules\/(\d+)$/.exec(path);
      if (ruleMatch !== null) {
        const ruleId = Number(ruleMatch[1]);

        if (method === 'PATCH') {
          return (
            options.updateRule ??
            ((id: number, patch: UpdateAlertRuleRequest) =>
              jsonResponse({
                ...frequencyRule,
                id,
                ...patch,
              }))
          )(ruleId, body);
        }

        if (method === 'DELETE') {
          return (options.deleteRule ?? (() => noContentResponse()))(ruleId);
        }
      }

      const testMatch = /^\/api\/manage\/alert-rules\/(\d+)\/test$/.exec(path);
      if (testMatch !== null && method === 'POST') {
        return (
          options.testRule ??
          (() =>
            jsonResponse({
              delivered: true,
              status_code: 204,
              error: null,
            } satisfies AlertRuleTestResult))
        )(Number(testMatch[1]));
      }

      throw new Error(`Unexpected fetch: ${method} ${path}`);
    },
  );

  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

type FetchMock = ReturnType<typeof stubApi>;

function callBody(call: unknown): unknown {
  const init = (call as [RequestInfo | URL, RequestInit | undefined])[1];
  return JSON.parse(String(init?.body));
}

function callsTo(fetchMock: FetchMock, path: string, method: string) {
  return fetchMock.mock.calls.filter(([input, init]) => {
    const url = new URL(String(input), 'http://localhost');
    return (
      url.pathname === path &&
      (init?.method ?? 'GET').toUpperCase() === method
    );
  });
}

function renderSection() {
  render(
    <I18nProvider>
      <AlertRulesSection projectId={7} />
    </I18nProvider>,
  );
}

/** Opens the create form and fills the fields every trigger needs. */
async function fillRequiredFields(
  user: ReturnType<typeof userEvent.setup>,
  name = 'Crash spike',
  url = 'https://hooks.example.com/alerts',
) {
  await user.click(screen.getByRole('button', { name: 'New alert rule' }));
  await user.type(screen.getByLabelText('Rule name'), name);
  await user.type(screen.getByLabelText('Target URL'), url);
}

describe('AlertRulesSection', () => {
  it('lists rules with trigger, condition, channel and enabled toggle', async () => {
    stubApi({ rules: () => jsonResponse({ items: [frequencyRule] }) });

    renderSection();

    expect(await screen.findByText('Crash spike')).toBeInTheDocument();
    expect(screen.getByText('Event frequency')).toBeInTheDocument();
    expect(
      screen.getByText('When 10 events arrive within 5 minutes'),
    ).toBeInTheDocument();
    expect(screen.getByText('Webhook')).toBeInTheDocument();
    expect(
      screen.getByLabelText('Enable rule Crash spike'),
    ).toBeChecked();
    expect(
      await screen.findByText('No alert notifications yet.'),
    ).toBeInTheDocument();
  });

  it('requires threshold and window for an event_frequency rule', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi();

    renderSection();
    await screen.findByText('No alert rules yet.');

    await fillRequiredFields(user);
    await user.selectOptions(screen.getByLabelText('Trigger'), 'event_frequency');
    await user.click(screen.getByRole('button', { name: 'Save rule' }));

    expect(
      await screen.findByText(
        'Failed to save alert rule: Threshold must be a positive integer.',
      ),
    ).toBeInTheDocument();
    expect(
      callsTo(fetchMock, '/api/manage/projects/7/alert-rules', 'POST'),
    ).toHaveLength(0);

    await user.type(screen.getByLabelText('Threshold (events)'), '10');
    await user.click(screen.getByRole('button', { name: 'Save rule' }));

    expect(
      await screen.findByText(
        'Failed to save alert rule: Window must be an integer between 1 and 1440.',
      ),
    ).toBeInTheDocument();

    await user.type(screen.getByLabelText('Window (minutes)'), '5');
    await user.click(screen.getByRole('button', { name: 'Save rule' }));

    await waitFor(() =>
      expect(
        callsTo(fetchMock, '/api/manage/projects/7/alert-rules', 'POST'),
      ).toHaveLength(1),
    );

    expect(
      callBody(
        callsTo(fetchMock, '/api/manage/projects/7/alert-rules', 'POST')[0],
      ),
    ).toEqual({
      name: 'Crash spike',
      trigger: 'event_frequency',
      channel: 'webhook',
      target_url: 'https://hooks.example.com/alerts',
      cooldown_minutes: 60,
      threshold: 10,
      window_minutes: 5,
    });
    expect(await screen.findByText('Crash spike')).toBeInTheDocument();
  });

  it('omits threshold and window for other triggers and clears them on switch', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi();

    renderSection();
    await screen.findByText('No alert rules yet.');

    await fillRequiredFields(user, 'New crash', 'https://example.com/hook');

    // Fill the frequency fields first, then switch away: they disappear and
    // are not part of the submitted create body.
    await user.selectOptions(screen.getByLabelText('Trigger'), 'event_frequency');
    await user.type(screen.getByLabelText('Threshold (events)'), '10');
    await user.type(screen.getByLabelText('Window (minutes)'), '5');
    await user.selectOptions(screen.getByLabelText('Trigger'), 'regression');

    expect(
      screen.queryByLabelText('Threshold (events)'),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByLabelText('Window (minutes)'),
    ).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Save rule' }));

    await waitFor(() =>
      expect(
        callsTo(fetchMock, '/api/manage/projects/7/alert-rules', 'POST'),
      ).toHaveLength(1),
    );

    const body = callBody(
      callsTo(fetchMock, '/api/manage/projects/7/alert-rules', 'POST')[0],
    );
    expect(body).toEqual({
      name: 'New crash',
      trigger: 'regression',
      channel: 'webhook',
      target_url: 'https://example.com/hook',
      cooldown_minutes: 60,
    });
    expect(body).not.toHaveProperty('threshold');
    expect(body).not.toHaveProperty('window_minutes');
  });

  it('patches an edited rule and clears the frequency fields after a trigger change', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi({
      rules: () => jsonResponse({ items: [frequencyRule] }),
    });

    renderSection();
    await screen.findByText('Crash spike');

    await user.click(screen.getByRole('button', { name: 'Edit' }));
    await user.selectOptions(screen.getByLabelText('Trigger'), 'new_issue');
    await user.click(screen.getByRole('button', { name: 'Save rule' }));

    await waitFor(() =>
      expect(
        callsTo(fetchMock, '/api/manage/alert-rules/11', 'PATCH'),
      ).toHaveLength(1),
    );

    expect(
      callBody(callsTo(fetchMock, '/api/manage/alert-rules/11', 'PATCH')[0]),
    ).toEqual({
      name: 'Crash spike',
      trigger: 'new_issue',
      channel: 'webhook',
      target_url: 'https://hooks.example.com/alerts',
      cooldown_minutes: 30,
      min_level: 'error',
      environment: 'production',
      threshold: null,
      window_minutes: null,
    });

    // The response replaces the row; the form closes.
    await waitFor(() =>
      expect(screen.queryByLabelText('Rule name')).not.toBeInTheDocument(),
    );
    expect(screen.getByText('When a new issue is created')).toBeInTheDocument();
  });

  it('patches the enabled state from the list toggle', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi({
      rules: () => jsonResponse({ items: [frequencyRule] }),
    });

    renderSection();
    await screen.findByText('Crash spike');

    const toggle = screen.getByLabelText('Enable rule Crash spike');
    await user.click(toggle);

    await waitFor(() =>
      expect(
        callsTo(fetchMock, '/api/manage/alert-rules/11', 'PATCH'),
      ).toHaveLength(1),
    );
    expect(
      callBody(callsTo(fetchMock, '/api/manage/alert-rules/11', 'PATCH')[0]),
    ).toEqual({ is_enabled: false });
    await waitFor(() => expect(toggle).not.toBeChecked());
  });

  it('deletes a rule only after the inline confirmation', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi({
      rules: () => jsonResponse({ items: [newIssueRule] }),
    });

    renderSection();
    await screen.findByText('Any new crash');

    await user.click(screen.getByRole('button', { name: 'Delete' }));

    expect(
      screen.getByText('Delete rule Any new crash?'),
    ).toBeInTheDocument();
    expect(
      callsTo(fetchMock, '/api/manage/alert-rules/12', 'DELETE'),
    ).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: 'Confirm delete' }));

    await waitFor(() =>
      expect(
        callsTo(fetchMock, '/api/manage/alert-rules/12', 'DELETE'),
      ).toHaveLength(1),
    );
    expect(await screen.findByText('No alert rules yet.')).toBeInTheDocument();
  });

  it('shows the delivered or failed result of a test send', async () => {
    const user = userEvent.setup();
    const fetchMock = stubApi({
      rules: () => jsonResponse({ items: [frequencyRule, newIssueRule] }),
      testRule: (ruleId) =>
        ruleId === 11
          ? jsonResponse({
              delivered: false,
              status_code: 500,
              error: 'boom',
            } satisfies AlertRuleTestResult)
          : jsonResponse({
              delivered: true,
              status_code: 204,
              error: null,
            } satisfies AlertRuleTestResult),
    });

    renderSection();
    await screen.findByText('Crash spike');

    const rows = screen.getAllByRole('row');
    const frequencyRow = within(rows[1]);
    const newIssueRow = within(rows[2]);

    await user.click(frequencyRow.getByRole('button', { name: 'Test send' }));

    expect(
      await frequencyRow.findByText('Delivery failed (HTTP 500): boom'),
    ).toBeInTheDocument();
    expect(
      callsTo(fetchMock, '/api/manage/alert-rules/11/test', 'POST'),
    ).toHaveLength(1);

    await user.click(newIssueRow.getByRole('button', { name: 'Test send' }));

    expect(
      await newIssueRow.findByText('Delivered (HTTP 204)'),
    ).toBeInTheDocument();
    expect(
      callsTo(fetchMock, '/api/manage/alert-rules/12/test', 'POST'),
    ).toHaveLength(1);
  });

  it('renders notifications with status badges, attempts and last_error', async () => {
    stubApi({
      rules: () => jsonResponse({ items: [frequencyRule] }),
      notifications: () =>
        jsonResponse({ items: [failedNotification, sentNotification] }),
    });

    renderSection();

    // The rule name appears both in the rule table and on its notification.
    expect(await screen.findAllByText('Crash spike')).toHaveLength(2);
    const table = await screen.findByText('HTTP 500 from target');
    expect(table).toHaveClass('alert-notification-error');

    expect(screen.getByText('Failed')).toHaveClass(
      'alert-notification-status-failed',
    );
    // "Sent" is also a column header; pick the status badge itself.
    expect(
      screen
        .getAllByText('Sent')
        .find((element) =>
          element.classList.contains('alert-notification-status'),
        ),
    ).toHaveClass('alert-notification-status-sent');
    expect(screen.getByText('3')).toHaveClass('alert-notification-attempts');
    expect(screen.getByText('Any new crash')).toBeInTheDocument();
  });

  it('keeps the rule list when the notification request fails', async () => {
    stubApi({
      rules: () => jsonResponse({ items: [frequencyRule] }),
      notifications: () => errorResponse(500, { error: 'boom' }),
    });

    renderSection();

    expect(await screen.findByText('Crash spike')).toBeInTheDocument();
    expect(
      await screen.findByText(
        'Failed to load alert notifications: HTTP 500: boom',
      ),
    ).toBeInTheDocument();
  });

  it('keeps the notifications when the rule request fails', async () => {
    stubApi({
      rules: () => errorResponse(500, { error: 'boom' }),
      notifications: () => jsonResponse({ items: [failedNotification] }),
    });

    renderSection();

    expect(
      await screen.findByText('Failed to load alert rules: HTTP 500: boom'),
    ).toBeInTheDocument();
    expect(
      await screen.findByText('HTTP 500 from target'),
    ).toBeInTheDocument();
  });
});
