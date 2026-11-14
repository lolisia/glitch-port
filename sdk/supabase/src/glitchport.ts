/**
 * Public entry point of the GlitchPort SDK for Supabase Edge Functions.
 *
 * The module owns one client instance per JavaScript realm: {@link init}
 * parses the DSN, builds the HTTP transport and replaces the global scope; all
 * later module-level captures use that global scope.
 *
 * Supabase Edge Functions run on Deno without `AsyncLocalStorage`, so a request
 * scope cannot be made ambient. Handlers wrapped with {@link withGlitchPort}
 * receive a per-request scope through {@link scope}, which is the only way to
 * attach request-local state and to capture with that state. Module-level
 * helpers always talk to the global scope.
 *
 * Capturing must never break the caller: every public capture entry point
 * swallows internal failures and still returns a valid event ID. Runtime code
 * uses standard web APIs only, so the module runs unchanged on Deno, Node and
 * in browsers.
 */

import { parseDsn } from './dsn.ts';
import { createEvent, createEventId } from './event.ts';
import {
  DEFAULT_MAX_BREADCRUMBS,
  DEFAULT_TIMEOUT_MS,
  MAX_BREADCRUMBS,
} from './limits.ts';
import { Scope } from './scope.ts';
import { buildExceptionChain } from './stack.ts';
import { createTransport, type Transport } from './transport.ts';
import type {
  CaptureHint,
  GlitchPortBreadcrumbInput,
  GlitchPortEvent,
  GlitchPortException,
  GlitchPortOptions,
  GlitchPortUser,
  Level,
} from './types.ts';

export type {
  CaptureHint,
  GlitchPortBreadcrumb,
  GlitchPortBreadcrumbInput,
  GlitchPortEvent,
  GlitchPortException,
  GlitchPortOptions,
  GlitchPortSdkInfo,
  GlitchPortStackFrame,
  GlitchPortStacktrace,
  GlitchPortUser,
  Level,
  ParsedDsn,
  ScopeSnapshot,
} from './types.ts';

/**
 * Request-local view of the scope returned by {@link scope}.
 *
 * Every method has the same signature and behaviour as the equivalent
 * module-level function, but reads and writes the scope created for one
 * request by {@link withGlitchPort}. Captures go through the same sampling,
 * `beforeSend` and transport path, so per-request tags, user, contexts and
 * breadcrumbs end up on the emitted event.
 */
export interface RequestScope {
  /** Set a request-local tag. */
  setTag(key: string, value: string): void;
  /** Attach the request-local user, or clear it with `null`. */
  setUser(user: GlitchPortUser | null): void;
  /** Attach a request-local context, or remove it with `null`. */
  setContext(name: string, context: Record<string, unknown> | null): void;
  /** Record a request-local breadcrumb. */
  addBreadcrumb(breadcrumb: GlitchPortBreadcrumbInput): void;
  /** Capture an exception with the request scope; returns the event ID. */
  captureException(error: unknown, hint?: CaptureHint): string;
  /** Capture a message with the request scope; returns the event ID. */
  captureMessage(message: string, level?: Level): string;
}

/** Fully resolved client configuration produced by {@link init}. */
interface ClientConfig {
  environment?: string;
  release?: string;
  serverName?: string;
  sampleRate: number;
  maxBreadcrumbs: number;
  timeoutMs: number;
  fetchImpl?: typeof fetch;
  debug: boolean;
  beforeSend?: (event: GlitchPortEvent) => GlitchPortEvent | null;
  inAppInclude?: string[];
}

/** Mutable module state installed by {@link init}. */
interface ClientState {
  config: ClientConfig;
  transport: Transport;
  scope: Scope;
}

/** Values shared by both capture helpers before they reach the event builder. */
interface CapturePayload {
  level: Level;
  message?: string;
  exceptions?: GlitchPortException[];
  tags?: Record<string, string>;
  extra?: Record<string, unknown>;
}

/** Scope used by module-level helpers until {@link init} replaces it. */
const initialGlobalScope = new Scope();

/** Active client, or `undefined` before {@link init} (and for invalid DSNs). */
let state: ClientState | undefined;

/** Request scopes keyed by the exact `Request` object given to the wrapper. */
const requestScopes = new WeakMap<Request, RequestScope>();

/** Clamp `sampleRate` to 0..1; missing and non-finite values mean "keep all". */
function normalizeSampleRate(value: number | undefined): number {
  if (value === undefined || !Number.isFinite(value)) {
    return 1;
  }
  return Math.min(1, Math.max(0, value));
}

/** Normalize the breadcrumb capacity: default 50, clamped to 0..100. */
function normalizeMaxBreadcrumbs(value: number | undefined): number {
  if (value === undefined || !Number.isFinite(value)) {
    return DEFAULT_MAX_BREADCRUMBS;
  }
  return Math.min(MAX_BREADCRUMBS, Math.max(0, Math.floor(value)));
}

/** Normalize the transport timeout: default 3000 ms, never below 1 ms. */
function normalizeTimeoutMs(value: number | undefined): number {
  if (value === undefined || !Number.isFinite(value)) {
    return DEFAULT_TIMEOUT_MS;
  }
  return Math.max(1, Math.floor(value));
}

/** Generate an event ID that is valid even if the crypto API misbehaves. */
function safeEventId(): string {
  try {
    return createEventId();
  } catch {
    try {
      let fallback = '';
      for (let index = 0; index < 32; index += 1) {
        fallback += (Math.floor(Math.random() * 16) % 16).toString(16);
      }
      return fallback;
    } catch {
      // Last resort: a syntactically valid, non-random identifier.
      return '00000000000000000000000000000000';
    }
  }
}

/** Scope used by module-level helpers: the installed one, else the initial. */
function currentScope(): Scope {
  return state?.scope ?? initialGlobalScope;
}

/**
 * Build and send one event. Never throws: event construction, sampling,
 * `beforeSend`, transport setup and the fire-and-forget send are all guarded,
 * and the caller always gets the pre-generated event ID back.
 */
function captureWithScope(
  scope: Scope,
  eventId: string,
  payload: CapturePayload,
): string {
  try {
    const client = state;
    if (client === undefined) {
      return eventId;
    }
    const { config } = client;

    const event = createEvent({
      eventId,
      level: payload.level,
      message: payload.message,
      exceptions: payload.exceptions,
      scope: scope.snapshot(),
      options: {
        environment: config.environment,
        release: config.release,
        serverName: config.serverName,
      },
      tags: payload.tags,
      extra: payload.extra,
      maxBreadcrumbs: config.maxBreadcrumbs,
    });

    // A rate of 1 never samples; a rate of 0 always drops.
    if (config.sampleRate <= 0) {
      return eventId;
    }
    if (config.sampleRate < 1 && Math.random() >= config.sampleRate) {
      return eventId;
    }

    let outgoing = event;
    if (config.beforeSend !== undefined) {
      try {
        const processed = config.beforeSend(event);
        if (processed === null || processed === undefined) {
          return eventId;
        }
        outgoing = processed;
      } catch {
        // A failing hook drops this event instead of breaking the caller.
        return eventId;
      }
    }

    try {
      void Promise.resolve(client.transport.send(outgoing)).catch(() => {});
    } catch {
      // A hostile transport implementation must not escape capture.
    }
  } catch {
    // Reporting must never throw: the caller still receives a usable ID.
  }
  return eventId;
}

/** Capture an exception onto a specific scope. */
function captureExceptionOn(
  scope: Scope,
  error: unknown,
  hint?: CaptureHint,
): string {
  const eventId = safeEventId();
  try {
    const exceptions = buildExceptionChain(error, state?.config.inAppInclude);
    return captureWithScope(scope, eventId, {
      level: hint?.level ?? 'error',
      exceptions,
      tags: hint?.tags,
      extra: hint?.extra,
    });
  } catch {
    return eventId;
  }
}

/** Capture a message onto a specific scope. */
function captureMessageOn(scope: Scope, message: string, level?: Level): string {
  const eventId = safeEventId();
  try {
    return captureWithScope(scope, eventId, {
      level: level ?? 'info',
      message,
    });
  } catch {
    return eventId;
  }
}

/** Build the request-local `RequestScope` facade for one `Scope`. */
function createRequestScope(scoped: Scope): RequestScope {
  return {
    setTag(key: string, value: string): void {
      scoped.setTag(key, value);
    },
    setUser(user: GlitchPortUser | null): void {
      scoped.setUser(user);
    },
    setContext(name: string, context: Record<string, unknown> | null): void {
      scoped.setContext(name, context);
    },
    addBreadcrumb(breadcrumb: GlitchPortBreadcrumbInput): void {
      scoped.addBreadcrumb(breadcrumb);
    },
    captureException(error: unknown, hint?: CaptureHint): string {
      return captureExceptionOn(scoped, error, hint);
    },
    captureMessage(message: string, level?: Level): string {
      return captureMessageOn(scoped, message, level);
    },
  };
}

/** Extract protocol, host and path from a request URL, dropping query/hash. */
function queryFreeUrl(req: Request): string {
  try {
    const url = new URL(req.url);
    return `${url.protocol}//${url.host}${url.pathname}`;
  } catch {
    return req.url;
  }
}

/**
 * Copy only the headers that are safe to report. Authorization, cookies and
 * forwarding headers can carry credentials or infrastructure details and are
 * deliberately excluded.
 */
function allowedRequestHeaders(req: Request): Record<string, string> {
  const headers: Record<string, string> = {};
  try {
    const userAgent = req.headers.get('user-agent');
    if (userAgent !== null) {
      headers['user-agent'] = userAgent;
    }
    const contentType = req.headers.get('content-type');
    if (contentType !== null) {
      headers['content-type'] = contentType;
    }
  } catch {
    // A malformed header collection simply yields no header context.
  }
  return headers;
}

/** Read the Deno version string when the SDK runs on a Deno runtime. */
function denoVersion(): string | undefined {
  const deno = (globalThis as { Deno?: { version?: { deno?: unknown } } }).Deno;
  const version = deno?.version?.deno;
  return typeof version === 'string' ? version : undefined;
}

/** Attach the automatic request context, runtime context and method tag. */
function applyRequestDefaults(scoped: Scope, req: Request): void {
  const method = req.method.toUpperCase();
  scoped.setContext('request', {
    method,
    url: queryFreeUrl(req),
    headers: allowedRequestHeaders(req),
  });

  const version = denoVersion();
  if (version !== undefined) {
    scoped.setContext('runtime', { name: 'deno', version });
  }

  scoped.setTag('http.method', method);
}

/**
 * Wait for pending sends before a failure response. When the Edge runtime
 * exposes `EdgeRuntime.waitUntil` the flush promise is handed over and the
 * response can be produced immediately; everywhere else the flush is awaited.
 */
async function settlePendingSends(): Promise<void> {
  const timeoutMs = state?.config.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  const flushed = flush(timeoutMs);

  const edgeRuntime = (globalThis as { EdgeRuntime?: { waitUntil?: unknown } })
    .EdgeRuntime;
  const waitUntil = edgeRuntime?.waitUntil;
  if (typeof waitUntil === 'function') {
    try {
      waitUntil.call(edgeRuntime, flushed);
      return;
    } catch {
      // Fall through and await the promise ourselves when the hook refuses it.
    }
  }

  await flushed;
}

/** JSON body of the automatic 500 response. Key order is part of the API. */
function internalErrorResponse(eventId: string): Response {
  return new Response(
    JSON.stringify({ error: 'Internal Server Error', event_id: eventId }),
    {
      status: 500,
      headers: { 'content-type': 'application/json' },
    },
  );
}

/**
 * Initialize the SDK for this realm.
 *
 * The DSN is parsed first, so an invalid DSN throws and leaves previously
 * installed state untouched. A successful call replaces the transport and the
 * global scope, and applies defaults for every omitted option.
 *
 * @throws {Error} with an `Invalid DSN:` message when `options.dsn` is invalid.
 */
export function init(options: GlitchPortOptions): void {
  const dsn = parseDsn(options.dsn);
  const maxBreadcrumbs = normalizeMaxBreadcrumbs(options.maxBreadcrumbs);
  const config: ClientConfig = {
    environment: options.environment,
    release: options.release,
    serverName: options.serverName,
    sampleRate: normalizeSampleRate(options.sampleRate),
    maxBreadcrumbs,
    timeoutMs: normalizeTimeoutMs(options.timeoutMs),
    fetchImpl: options.fetchImpl ?? globalThis.fetch,
    debug: options.debug === true,
    beforeSend: options.beforeSend,
    inAppInclude:
      options.inAppInclude === undefined ? undefined : [...options.inAppInclude],
  };

  const transport = createTransport({
    dsn,
    fetchImpl: config.fetchImpl,
    timeoutMs: config.timeoutMs,
    debug: config.debug,
  });

  state = {
    config,
    transport,
    scope: new Scope({}, { maxBreadcrumbs }),
  };
}

/**
 * Capture an exception on the global scope.
 *
 * The exception chain follows `Error.cause` and is ordered root cause first.
 * The default level is `error`. Before {@link init}, while the event is
 * sampled away, when `beforeSend` returns `null` or when anything fails, the
 * event is not sent, but the returned 32-character hexadecimal event ID is
 * always valid.
 */
export function captureException(error: unknown, hint?: CaptureHint): string {
  return captureExceptionOn(currentScope(), error, hint);
}

/**
 * Capture a message on the global scope. The default level is `info`.
 * Behaves like {@link captureException} for sampling, `beforeSend` and errors.
 */
export function captureMessage(message: string, level?: Level): string {
  return captureMessageOn(currentScope(), message, level);
}

/** Set a tag on the global scope. */
export function setTag(key: string, value: string): void {
  currentScope().setTag(key, value);
}

/** Attach the user to the global scope, or clear it with `null`. */
export function setUser(user: GlitchPortUser | null): void {
  currentScope().setUser(user);
}

/** Attach a context to the global scope, or remove it with `null`. */
export function setContext(
  name: string,
  context: Record<string, unknown> | null,
): void {
  currentScope().setContext(name, context);
}

/** Record a breadcrumb on the global scope. */
export function addBreadcrumb(breadcrumb: GlitchPortBreadcrumbInput): void {
  currentScope().addBreadcrumb(breadcrumb);
}

/**
 * Wait for in-flight sends.
 *
 * Resolves `true` when everything settled within the timeout (or nothing was
 * pending) and `false` on timeout or transport failure. Before {@link init}
 * there is nothing to wait for and the promise resolves `true`.
 */
export function flush(timeoutMs?: number): Promise<boolean> {
  const client = state;
  if (client === undefined) {
    return Promise.resolve(true);
  }
  try {
    const waitMs = timeoutMs ?? client.config.timeoutMs;
    return Promise.resolve(client.transport.flush(waitMs)).catch(() => false);
  } catch {
    return Promise.resolve(false);
  }
}

/**
 * Return the scope of a request that is currently running inside a
 * {@link withGlitchPort} handler.
 *
 * @throws {Error} when the request was not passed to the wrapper, or when the
 * handler has already finished.
 */
export function scope(req: Request): RequestScope {
  const requestScope = requestScopes.get(req);
  if (requestScope === undefined) {
    throw new Error(
      'GlitchPort.scope(req) is only available for a Request that is currently ' +
        'being handled by withGlitchPort. Pass the exact Request object the ' +
        'wrapped handler received.',
    );
  }
  return requestScope;
}

/**
 * Wrap a Supabase Edge Function handler.
 *
 * Each request gets an independent clone of the global scope, enriched with
 * the automatic request context, runtime context and `http.method` tag, and is
 * available to the handler through {@link scope}. Unhandled exceptions are
 * captured on that request scope with level `error`; the wrapper then waits
 * for the send (`EdgeRuntime.waitUntil` when available, an awaited flush
 * otherwise) and either rethrows the original value (`rethrow: true`) or
 * returns the standard 500 JSON response. Normal responses are returned
 * unchanged and send nothing.
 */
export function withGlitchPort(
  handler: (req: Request, info?: unknown) => Response | Promise<Response>,
  opts?: { rethrow?: boolean },
): (req: Request, info?: unknown) => Promise<Response> {
  return async (req: Request, info?: unknown): Promise<Response> => {
    const scoped = currentScope().clone();
    const requestScope = createRequestScope(scoped);
    requestScopes.set(req, requestScope);

    try {
      applyRequestDefaults(scoped, req);
      return await handler(req, info);
    } catch (error) {
      const eventId = requestScope.captureException(error, { level: 'error' });
      await settlePendingSends();
      if (opts?.rethrow === true) {
        throw error;
      }
      return internalErrorResponse(eventId);
    } finally {
      requestScopes.delete(req);
    }
  };
}
