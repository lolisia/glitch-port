import type {
  BulkIssuesRequest,
  BulkIssuesResponse,
  CreatedProject,
  CreateLabelRequest,
  CreateProjectRequest,
  EventDetail,
  EventSummaryList,
  Issue,
  IssueDetail,
  IssueListQuery,
  IssueListResponse,
  Label,
  Message,
  ProjectListItem,
  StatsBucket,
  UpdateIssueRequest,
  UpdateLabelRequest,
} from './types';

// An empty value keeps requests relative to the dashboard origin, which lets a
// same-origin authenticated gateway forward them. A trailing slash is removed
// so paths can always be appended with a leading slash.
const configuredApiBase =
  (import.meta.env.VITE_API_BASE as string | undefined) ??
  'http://localhost:5090';

export const apiBase = configuredApiBase.replace(/\/+$/, '');

// An empty token deliberately disables the Authorization header; the gateway
// case relies on browser authentication instead.
export const adminToken =
  (import.meta.env.VITE_ADMIN_TOKEN as string | undefined) ?? 'dev-admin-token';

// The active project is a dashboard concern (persisted selection), not client
// configuration; re-exported here so callers that only read the API module can
// still parse VITE_PROJECT_ID.
export { parseConfiguredProjectId } from '../project/activeProject';

export class ApiError extends Error {
  readonly status: number;
  readonly body: unknown;

  constructor(status: number, message: string, body: unknown) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
  }
}

type HttpMethod = 'DELETE' | 'PATCH' | 'POST';

interface RequestOptions {
  method?: HttpMethod;
  query?: object;
  body?: unknown;
  /** DELETE endpoints resolve undefined without parsing a response body. */
  parseResponse?: boolean;
}

function buildQuery(query: object | undefined): string {
  if (query === undefined) {
    return '';
  }

  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null || value === '') {
      continue;
    }
    params.append(key, String(value));
  }

  return params.toString();
}

function buildUrl(path: string, query: object | undefined): string {
  const search = buildQuery(query);
  return `${apiBase}/api/manage${path}${search === '' ? '' : `?${search}`}`;
}

function hasNoBody(response: Response): boolean {
  if (response.status === 204) {
    return true;
  }

  return response.headers?.get('content-length') === '0';
}

async function parseJson(response: Response): Promise<unknown> {
  try {
    return await response.json();
  } catch {
    return undefined;
  }
}

function toErrorMessage(status: number, body: unknown): string {
  const message = `HTTP ${status}`;

  if (body !== null && typeof body === 'object' && 'error' in body) {
    const error = (body as { error?: unknown }).error;
    if (typeof error === 'string' && error !== '') {
      return `${message}: ${error}`;
    }
  }

  return message;
}

async function apiRequest<T>(
  path: string,
  options: RequestOptions = {},
): Promise<T> {
  const { method, query, body } = options;
  const headers: Record<string, string> = {};

  if (adminToken !== '') {
    headers.Authorization = `Bearer ${adminToken}`;
  }
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  const init: RequestInit = { headers };
  // GET is the fetch default; only write the method for the other verbs.
  if (method !== undefined) {
    init.method = method;
  }
  if (body !== undefined) {
    init.body = JSON.stringify(body);
  }

  const response = await fetch(buildUrl(path, query), init);

  if (!response.ok) {
    const errorBody = hasNoBody(response)
      ? undefined
      : await parseJson(response);
    throw new ApiError(
      response.status,
      toErrorMessage(response.status, errorBody),
      errorBody,
    );
  }

  if (options.parseResponse === false || hasNoBody(response)) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

/** GET /api/manage/messages?limit=100 — legacy queue view. */
export function fetchMessages(): Promise<Message[]> {
  return apiRequest<Message[]>('/messages', { query: { limit: 100 } });
}

/** GET /api/manage/projects */
export function listProjects(): Promise<ProjectListItem[]> {
  return apiRequest<ProjectListItem[]>('/projects');
}

/** POST /api/manage/projects — 201 with the project and its first key's DSN. */
export function createProject(
  input: CreateProjectRequest,
): Promise<CreatedProject> {
  return apiRequest<CreatedProject>('/projects', {
    method: 'POST',
    body: input,
  });
}

/** GET /api/manage/projects/{projectId}/issues */
export function listIssues(
  projectId: number,
  query: IssueListQuery = {},
): Promise<IssueListResponse> {
  return apiRequest<IssueListResponse>(`/projects/${projectId}/issues`, {
    query,
  });
}

/** GET /api/manage/issues/{id} */
export function getIssue(id: number): Promise<IssueDetail> {
  return apiRequest<IssueDetail>(`/issues/${id}`);
}

/** PATCH /api/manage/issues/{id} */
export function updateIssue(
  id: number,
  patch: UpdateIssueRequest,
): Promise<Issue> {
  return apiRequest<Issue>(`/issues/${id}`, { method: 'PATCH', body: patch });
}

/** DELETE /api/manage/issues/{id} */
export function deleteIssue(id: number): Promise<void> {
  return apiRequest<void>(`/issues/${id}`, {
    method: 'DELETE',
    parseResponse: false,
  });
}

/** POST /api/manage/issues/bulk */
export function bulkUpdateIssues(
  request: BulkIssuesRequest,
): Promise<BulkIssuesResponse> {
  return apiRequest<BulkIssuesResponse>('/issues/bulk', {
    method: 'POST',
    body: request,
  });
}

/** GET /api/manage/issues/{issueId}/events */
export function listIssueEvents(
  issueId: number,
  query: { limit?: number; cursor?: string } = {},
): Promise<EventSummaryList> {
  return apiRequest<EventSummaryList>(`/issues/${issueId}/events`, { query });
}

/** GET /api/manage/events/{eventId} */
export function getEvent(eventId: string): Promise<EventDetail> {
  return apiRequest<EventDetail>(`/events/${encodeURIComponent(eventId)}`);
}

/** DELETE /api/manage/events/{eventId} */
export function deleteEvent(eventId: string): Promise<void> {
  return apiRequest<void>(`/events/${encodeURIComponent(eventId)}`, {
    method: 'DELETE',
    parseResponse: false,
  });
}

/** GET /api/manage/projects/{projectId}/labels */
export function listLabels(projectId: number): Promise<Label[]> {
  return apiRequest<Label[]>(`/projects/${projectId}/labels`);
}

/** POST /api/manage/projects/{projectId}/labels */
export function createLabel(
  projectId: number,
  input: CreateLabelRequest,
): Promise<Label> {
  return apiRequest<Label>(`/projects/${projectId}/labels`, {
    method: 'POST',
    body: input,
  });
}

/** PATCH /api/manage/labels/{id} */
export function updateLabel(
  id: number,
  input: UpdateLabelRequest,
): Promise<Label> {
  return apiRequest<Label>(`/labels/${id}`, { method: 'PATCH', body: input });
}

/** DELETE /api/manage/labels/{id} */
export function deleteLabel(id: number): Promise<void> {
  return apiRequest<void>(`/labels/${id}`, {
    method: 'DELETE',
    parseResponse: false,
  });
}

/** GET /api/manage/projects/{projectId}/stats?period=... */
export function getStats(
  projectId: number,
  period: '24h' | '14d' = '24h',
): Promise<StatsBucket[]> {
  return apiRequest<StatsBucket[]>(`/projects/${projectId}/stats`, {
    query: { period },
  });
}
