/**
 * Public types of the GlitchPort Supabase SDK.
 *
 * Option keys authored by users are camelCase; every key that is serialized
 * into an event sent to the collector is snake_case so the payload matches the
 * GlitchPort event protocol.
 */

export type Level = 'debug' | 'info' | 'warning' | 'error' | 'fatal';

/**
 * Options accepted by the SDK client. Only `dsn` is required; every other
 * option falls back to a default or to the matching environment variable.
 */
export interface GlitchPortOptions {
  /** Project DSN, for example `https://<publicKey>@glitchport.example.com/<projectId>`. */
  dsn: string;
  /** Environment name attached to every event (for example `production`). */
  environment?: string;
  /** Release identifier attached to every event (for example a git SHA). */
  release?: string;
  /** Server name reported as `server_name` (defaults to the edge function region). */
  serverName?: string;
  /** Fraction of events to send, between 0 and 1. */
  sampleRate?: number;
  /** Maximum number of breadcrumbs kept in the scope. */
  maxBreadcrumbs?: number;
  /** Called before an event is sent; return `null` to drop it. */
  beforeSend?: (event: GlitchPortEvent) => GlitchPortEvent | null;
  /** Module/path prefixes whose stack frames are marked as in-app. */
  inAppInclude?: string[];
  /** `fetch` implementation used for transport (useful in tests). */
  fetchImpl?: typeof fetch;
  /** Log diagnostic messages to the console. */
  debug?: boolean;
  /** Transport timeout in milliseconds. */
  timeoutMs?: number;
}

/** A single frame of a captured stack trace. */
export interface GlitchPortStackFrame {
  function: string;
  module?: string;
  filename: string;
  abs_path?: string;
  lineno: number;
  colno: number;
  in_app: boolean;
}

export interface GlitchPortStacktrace {
  frames: GlitchPortStackFrame[];
}

export interface GlitchPortException {
  type: string;
  value: string;
  module?: string;
  stacktrace: GlitchPortStacktrace;
}

/** Breadcrumb fields supplied by the caller; the timestamp is added on capture. */
export interface GlitchPortBreadcrumbInput {
  category?: string;
  message?: string;
  level?: Level;
  type?: string;
  data?: Record<string, unknown>;
}

export interface GlitchPortBreadcrumb extends GlitchPortBreadcrumbInput {
  timestamp: string;
}

export interface GlitchPortUser {
  id?: string;
  username?: string;
  email?: string;
  ip_address?: string;
}

export interface GlitchPortSdkInfo {
  name: string;
  version: string;
}

/** Event payload sent to the GlitchPort collector. */
export interface GlitchPortEvent {
  event_id: string;
  timestamp: string;
  level: Level;
  platform: 'javascript';
  logger?: string;
  message?: string;
  release?: string;
  environment?: string;
  server_name?: string;
  sdk: GlitchPortSdkInfo;
  exception?: GlitchPortException[];
  tags?: Record<string, string>;
  user?: GlitchPortUser;
  contexts?: Record<string, unknown>;
  breadcrumbs?: GlitchPortBreadcrumb[];
  fingerprint?: string[];
  extra?: Record<string, unknown>;
}

/** The three pieces extracted from a DSN. */
export interface ParsedDsn {
  /** Protocol plus host (including port), without path, query or hash. */
  url: string;
  /** Decoded username of the DSN: the project public key. */
  publicKey: string;
  /** Decoded single path segment of the DSN. */
  projectId: string;
}

/** Per-capture values that override or extend the current scope. */
export interface CaptureHint {
  level?: Level;
  tags?: Record<string, string>;
  extra?: Record<string, unknown>;
}

/** Immutable view of the scope at the time an event is captured. */
export interface ScopeSnapshot {
  tags: Record<string, string>;
  user?: GlitchPortUser;
  contexts: Record<string, unknown>;
  breadcrumbs: GlitchPortBreadcrumb[];
}
