/**
 * Request and response types for the Management API exposed by the Processor.
 *
 * Field names intentionally mirror the wire format (snake_case): these types
 * describe JSON payloads, not internal view models.
 */

export interface Message {
  id: string;
  received_at: string;
  endpoint: string;
  message: string;
}

/** One entry of `GET /api/manage/projects`. */
export interface ProjectListItem {
  id: number;
  name: string;
  slug: string;
  platform: string | null;
  created_at: string;
  /** Unresolved issues, used by the project list. */
  issue_count: number;
  /** Active (non-revoked) public keys. */
  key_count: number;
}

/** `GET /api/manage/projects/{id}`: the list entry plus its active DSN. */
export interface ProjectDetail extends ProjectListItem {
  dsn: string | null;
}

/** The project returned by `POST /api/manage/projects`, including its key's DSN. */
export interface CreatedProject {
  id: number;
  name: string;
  slug: string;
  platform: string | null;
  created_at: string;
  dsn: string;
}

/** Request body of `POST /api/manage/projects`. */
export interface CreateProjectRequest {
  name: string;
  slug?: string;
  platform?: string;
}

/** Request body of `PATCH /api/manage/projects/{id}`; omitted fields stay unchanged. */
export interface UpdateProjectRequest {
  name?: string;
  platform?: string;
}

/**
 * One public ingest key of a project, returned by the key endpoints.
 *
 * `rate_limit_per_minute` is `null` when no per-key limit is configured and `0`
 * when the key is explicitly unlimited.
 */
export interface ProjectKey {
  id: number;
  /** 32 lowercase hexadecimal characters, sent as `X-GlitchPort-Key`. */
  public_key: string;
  label: string | null;
  /** `false` for a revoked key; the row is retained. */
  is_active: boolean;
  rate_limit_per_minute: number | null;
  created_at: string;
  revoked_at: string | null;
  /** Ingest DSN built from the key and the Processor's public Relay URL. */
  dsn: string;
}

/** Request body of `POST /api/manage/projects/{id}/keys`. */
export interface CreateProjectKeyRequest {
  label?: string;
  rate_limit_per_minute?: number | null;
}

/** Request body of `PATCH /api/manage/keys/{id}`; omitted fields stay unchanged. */
export interface UpdateProjectKeyRequest {
  label?: string;
  rate_limit_per_minute?: number | null;
  is_active?: boolean;
}

export type IssueStatus = 'unresolved' | 'resolved' | 'ignored';

export type IssueLevel = 'debug' | 'info' | 'warning' | 'error' | 'fatal';

export interface Label {
  id: number;
  name: string;
  color: string;
}

export interface Issue {
  id: number;
  title: string;
  culprit: string | null;
  level: IssueLevel | string;
  platform: string;
  status: IssueStatus | string;
  first_seen: string;
  last_seen: string;
  event_count: number;
  last_release: string | null;
  last_environment: string | null;
  labels: Label[];
}

export interface IssueListResponse {
  items: Issue[];
  next_cursor: string | null;
}

export interface IssueDetail extends Issue {
  latest_event: EventDetail | null;
}

export interface EventSummary {
  /** Event ids are server-generated UUID strings, unlike numeric issue ids. */
  id: string;
  timestamp: string;
  received_at: string;
  level: IssueLevel | string;
  environment: string | null;
  release: string | null;
  user_id: string | null;
  message: string | null;
}

export interface EventSummaryList {
  items: EventSummary[];
  next_cursor: string | null;
}

export interface StackFrame {
  function?: string | null;
  module?: string | null;
  filename?: string | null;
  abs_path?: string | null;
  lineno?: number | null;
  colno?: number | null;
  in_app?: boolean | null;
  context_line?: string | null;
}

export interface ExceptionValue {
  type: string;
  value?: string | null;
  module?: string | null;
  stacktrace?: { frames?: StackFrame[] } | null;
}

export interface Breadcrumb {
  timestamp?: string | null;
  category?: string | null;
  message?: string | null;
  level?: IssueLevel | string | null;
  type?: string | null;
  data?: Record<string, unknown> | null;
}

export interface EventUser {
  id?: string | null;
  username?: string | null;
  email?: string | null;
  ip_address?: string | null;
}

export interface SdkInfo {
  name?: string | null;
  version?: string | null;
}

export interface EventPayload {
  message?: string | null;
  exception?: ExceptionValue[] | null;
  tags?: Record<string, string> | null;
  user?: EventUser | null;
  contexts?: Record<string, unknown> | null;
  breadcrumbs?: Breadcrumb[] | null;
  sdk?: SdkInfo | null;
  extra?: Record<string, unknown> | null;
  /** Event payloads may carry additional SDK-defined keys. */
  [key: string]: unknown;
}

export interface EventDetail {
  id: string;
  issue_id: number;
  timestamp: string;
  received_at: string;
  level: IssueLevel | string;
  platform: string;
  environment: string | null;
  release: string | null;
  user_id: string | null;
  endpoint: string;
  payload: EventPayload;
}

export interface StatsBucket {
  bucket: string;
  count: number;
}

export interface IssueListQuery {
  status?: 'unresolved' | 'resolved' | 'ignored' | 'all';
  level?: string;
  environment?: string;
  release?: string;
  label?: string;
  q?: string;
  sort?: 'last_seen' | 'first_seen' | 'count';
  limit?: number;
  cursor?: string;
}

export interface UpdateIssueRequest {
  status?: IssueStatus;
  labels?: string[];
}

export type BulkAction =
  | 'resolve'
  | 'unresolve'
  | 'ignore'
  | 'delete'
  | 'add_labels'
  | 'remove_labels';

export interface BulkIssuesRequest {
  ids: number[];
  action: BulkAction;
  labels?: string[];
}

export interface BulkIssuesResponse {
  affected: number;
}

export interface CreateLabelRequest {
  name: string;
  color?: string;
}

export interface UpdateLabelRequest {
  name?: string;
  color?: string;
}
