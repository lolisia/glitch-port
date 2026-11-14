/**
 * Construction of GlitchPort protocol v1 events.
 *
 * This module turns capture input plus a scope snapshot into the snake_case
 * payload sent to the collector, applying every protocol limit (truncation,
 * count caps and the 64 KiB contexts/extra guard) before serialization.
 * Sampling and `beforeSend` are deliberately left to the public client.
 */

import {
  DEFAULT_MAX_BREADCRUMBS,
  MAX_BREADCRUMBS,
  MAX_CONTEXTS_BYTES,
  MAX_ENVIRONMENT_LENGTH,
  MAX_EXCEPTIONS,
  MAX_EXCEPTION_TYPE_LENGTH,
  MAX_EXTRA_BYTES,
  MAX_FINGERPRINT,
  MAX_FINGERPRINT_LENGTH,
  MAX_FRAMES,
  MAX_FRAME_STRING_LENGTH,
  MAX_LOGGER_LENGTH,
  MAX_MESSAGE_LENGTH,
  MAX_RELEASE_LENGTH,
  MAX_SERVER_NAME_LENGTH,
  MAX_TAGS,
  MAX_TAG_KEY_LENGTH,
  MAX_TAG_VALUE_LENGTH,
  MAX_VALUE_LENGTH,
  SDK_NAME,
  SDK_VERSION,
  truncate,
} from './limits.ts';
import type {
  GlitchPortBreadcrumb,
  GlitchPortEvent,
  GlitchPortException,
  GlitchPortOptions,
  GlitchPortStackFrame,
  GlitchPortUser,
  Level,
  ScopeSnapshot,
} from './types.ts';

/** Levels accepted by the event protocol. */
const LEVELS: readonly Level[] = ['debug', 'info', 'warning', 'error', 'fatal'];

/** Input accepted by {@link createEvent}. */
export interface CreateEventInput {
  /** Severity of the event; invalid values fall back to `error`. */
  level: Level;
  /** Human-readable message. Required when no exception is supplied. */
  message?: string;
  /** Exceptions, root cause first. Required when no message is supplied. */
  exceptions?: GlitchPortException[];
  /** Snapshot of the active scope. */
  scope: ScopeSnapshot;
  /** Client-wide values copied onto the event. */
  options?: Pick<GlitchPortOptions, 'environment' | 'release' | 'serverName'>;
  /** Pre-generated event ID; a random one is created when omitted. */
  eventId?: string;
  /** Event time as an ISO 8601 string; defaults to now. */
  timestamp?: string;
  /** Logger/category name. */
  logger?: string;
  /** Capture-time tags; they win over scope tags with the same key. */
  tags?: Record<string, string>;
  /** Arbitrary additional data, guarded to 64 KiB. */
  extra?: Record<string, unknown>;
  /** Capture-time contexts; they win over scope contexts with the same name. */
  contexts?: Record<string, unknown>;
  /** Capture-time user; falls back to the scope user when omitted. */
  user?: GlitchPortUser;
  /** Capture-time breadcrumbs; fall back to the scope breadcrumbs when omitted. */
  breadcrumbs?: GlitchPortBreadcrumb[];
  /** Grouping fingerprint; at most 10 components of 200 characters. */
  fingerprint?: string[];
  /** Maximum breadcrumbs kept; default 50, hard cap 100. */
  maxBreadcrumbs?: number;
}

/** The subset of the Web Crypto API used to generate event IDs. */
interface CryptoLike {
  randomUUID?: () => string;
  getRandomValues?: (array: Uint8Array) => unknown;
}

const textEncoder = new TextEncoder();

function getCrypto(): CryptoLike | undefined {
  const candidate = (globalThis as unknown as { crypto?: CryptoLike }).crypto;
  return typeof candidate === 'object' && candidate !== null ? candidate : undefined;
}

/**
 * Generate a protocol event ID: 32 lowercase hexadecimal characters.
 *
 * Uses `crypto.randomUUID()` when available and falls back to
 * `crypto.getRandomValues()`. The last-resort `Math.random()` path keeps the
 * function total (and therefore keeps `createEvent` from throwing for reasons
 * other than a missing message/exception) in exotic runtimes.
 */
export function createEventId(): string {
  const cryptoApi = getCrypto();

  if (typeof cryptoApi?.randomUUID === 'function') {
    return cryptoApi.randomUUID().replace(/-/g, '').toLowerCase();
  }

  if (typeof cryptoApi?.getRandomValues === 'function') {
    const bytes = new Uint8Array(16);
    cryptoApi.getRandomValues(bytes);
    let id = '';
    for (const byte of bytes) {
      id += byte.toString(16).padStart(2, '0');
    }
    return id;
  }

  let id = '';
  for (let index = 0; index < 32; index += 1) {
    id += Math.floor(Math.random() * 16).toString(16);
  }
  return id;
}

/** Return `level` when it is part of the protocol, `error` otherwise. */
function normalizeLevel(level: Level): Level {
  return LEVELS.includes(level) ? level : 'error';
}

/** Normalize a breadcrumb capacity: default 50, clamped to 0..100. */
function normalizeMaxBreadcrumbs(value: number | undefined): number {
  if (value === undefined || !Number.isFinite(value)) {
    return DEFAULT_MAX_BREADCRUMBS;
  }
  return Math.min(MAX_BREADCRUMBS, Math.max(0, Math.floor(value)));
}

function copyBreadcrumb(breadcrumb: GlitchPortBreadcrumb): GlitchPortBreadcrumb {
  const copy: GlitchPortBreadcrumb = { ...breadcrumb };
  if (breadcrumb.data !== undefined) {
    copy.data = { ...breadcrumb.data };
  }
  return copy;
}

/** Keep the most recent `maxBreadcrumbs` breadcrumbs, as copies. */
function limitBreadcrumbs(
  breadcrumbs: GlitchPortBreadcrumb[] | undefined,
  maxBreadcrumbs: number | undefined,
): GlitchPortBreadcrumb[] | undefined {
  if (breadcrumbs === undefined || breadcrumbs.length === 0) {
    return undefined;
  }
  const limit = normalizeMaxBreadcrumbs(maxBreadcrumbs);
  if (limit === 0) {
    return undefined;
  }
  return breadcrumbs.slice(-limit).map(copyBreadcrumb);
}

/**
 * Merge scope and capture tags (capture wins) and keep the first `MAX_TAGS`
 * keys in insertion order, truncating keys and values to protocol limits.
 */
function mergeTags(
  scopeTags: Record<string, string> | undefined,
  inputTags: Record<string, string> | undefined,
): Record<string, string> | undefined {
  const merged = new Map<string, string>();
  for (const tags of [scopeTags, inputTags]) {
    if (tags === undefined) {
      continue;
    }
    for (const [key, value] of Object.entries(tags)) {
      merged.set(key, value);
    }
  }
  if (merged.size === 0) {
    return undefined;
  }

  const entries: Array<[string, string]> = [];
  for (const [key, value] of merged) {
    if (entries.length >= MAX_TAGS) {
      break;
    }
    entries.push([truncate(key, MAX_TAG_KEY_LENGTH), truncate(value, MAX_TAG_VALUE_LENGTH)]);
  }
  return Object.fromEntries(entries);
}

/** Merge scope and capture contexts (capture wins), preserving key order. */
function mergeContexts(
  scopeContexts: Record<string, unknown> | undefined,
  inputContexts: Record<string, unknown> | undefined,
): Record<string, unknown> | undefined {
  const merged = new Map<string, unknown>();
  for (const contexts of [scopeContexts, inputContexts]) {
    if (contexts === undefined) {
      continue;
    }
    for (const [name, context] of Object.entries(contexts)) {
      merged.set(name, context);
    }
  }
  if (merged.size === 0) {
    return undefined;
  }
  return Object.fromEntries(merged);
}

/**
 * Guard a record to `maxBytes` of serialized JSON by keeping the longest
 * prefix of its keys (keys are dropped from the end). A value that cannot be
 * serialized (for example a cycle) is treated as not fitting, so this never
 * throws for cyclic input.
 */
function limitRecord(
  record: Record<string, unknown> | undefined,
  maxBytes: number,
): Record<string, unknown> | undefined {
  if (record === undefined) {
    return undefined;
  }
  const entries = Object.entries(record);
  if (entries.length === 0) {
    return undefined;
  }

  // Serialized size and serializability are both monotonic in the number of
  // leading entries, so the largest fitting prefix can be found by bisection.
  let low = 0;
  let high = entries.length;
  let keep = 0;
  while (low <= high) {
    const middle = Math.floor((low + high) / 2);
    if (prefixFits(entries, middle, maxBytes)) {
      keep = middle;
      low = middle + 1;
    } else {
      high = middle - 1;
    }
  }

  if (keep === 0) {
    return undefined;
  }
  return Object.fromEntries(entries.slice(0, keep));
}

function prefixFits(
  entries: Array<[string, unknown]>,
  count: number,
  maxBytes: number,
): boolean {
  const candidate = Object.fromEntries(entries.slice(0, count));
  try {
    return textEncoder.encode(JSON.stringify(candidate)).length <= maxBytes;
  } catch {
    return false;
  }
}

/** Normalize one stack frame, truncating every optional string field. */
function normalizeFrame(frame: GlitchPortStackFrame): GlitchPortStackFrame {
  const normalized: GlitchPortStackFrame = {
    function: truncate(frame.function, MAX_FRAME_STRING_LENGTH),
    filename: truncate(frame.filename, MAX_FRAME_STRING_LENGTH),
    lineno: frame.lineno,
    colno: frame.colno,
    in_app: frame.in_app,
  };
  if (frame.module !== undefined) {
    normalized.module = truncate(frame.module, MAX_FRAME_STRING_LENGTH);
  }
  if (frame.abs_path !== undefined) {
    normalized.abs_path = truncate(frame.abs_path, MAX_FRAME_STRING_LENGTH);
  }
  return normalized;
}

/** Normalize one exception, keeping the 250 frames closest to the throw site. */
function normalizeException(exception: GlitchPortException): GlitchPortException {
  const frames = exception.stacktrace?.frames;
  const normalized: GlitchPortException = {
    type: truncate(exception.type, MAX_EXCEPTION_TYPE_LENGTH),
    value: truncate(exception.value, MAX_VALUE_LENGTH),
    stacktrace: {
      frames:
        frames === undefined || frames.length === 0
          ? []
          : frames.slice(-MAX_FRAMES).map(normalizeFrame),
    },
  };
  if (exception.module !== undefined) {
    normalized.module = truncate(exception.module, MAX_EXCEPTION_TYPE_LENGTH);
  }
  return normalized;
}

/** Truncate fingerprint components and keep at most `MAX_FINGERPRINT` of them. */
function normalizeFingerprint(
  fingerprint: string[] | undefined,
): string[] | undefined {
  if (fingerprint === undefined || fingerprint.length === 0) {
    return undefined;
  }
  return fingerprint
    .slice(0, MAX_FINGERPRINT)
    .map((component) => truncate(component, MAX_FINGERPRINT_LENGTH));
}

/**
 * Build a protocol v1 event from capture input and the active scope snapshot.
 *
 * @throws {Error} only when neither a message (including an empty string) nor
 * at least one exception is provided.
 */
export function createEvent(input: CreateEventInput): GlitchPortEvent {
  const exceptions = input.exceptions ?? [];
  if (input.message === undefined && exceptions.length === 0) {
    throw new Error('An event requires a message or at least one exception.');
  }

  const event: GlitchPortEvent = {
    event_id: input.eventId ?? createEventId(),
    timestamp: input.timestamp ?? new Date().toISOString(),
    level: normalizeLevel(input.level),
    platform: 'javascript',
    sdk: { name: SDK_NAME, version: SDK_VERSION },
  };

  if (input.logger !== undefined) {
    event.logger = truncate(input.logger, MAX_LOGGER_LENGTH);
  }
  if (input.message !== undefined) {
    event.message = truncate(input.message, MAX_MESSAGE_LENGTH);
  }

  const release = input.options?.release;
  if (release !== undefined) {
    event.release = truncate(release, MAX_RELEASE_LENGTH);
  }
  const environment = input.options?.environment;
  if (environment !== undefined) {
    event.environment = truncate(environment, MAX_ENVIRONMENT_LENGTH);
  }
  const serverName = input.options?.serverName;
  if (serverName !== undefined) {
    event.server_name = truncate(serverName, MAX_SERVER_NAME_LENGTH);
  }

  if (exceptions.length > 0) {
    event.exception = exceptions.slice(0, MAX_EXCEPTIONS).map(normalizeException);
  }

  const tags = mergeTags(input.scope.tags, input.tags);
  if (tags !== undefined) {
    event.tags = tags;
  }

  const user = input.user ?? input.scope.user;
  if (user !== undefined) {
    event.user = { ...user };
  }

  const contexts = limitRecord(
    mergeContexts(input.scope.contexts, input.contexts),
    MAX_CONTEXTS_BYTES,
  );
  if (contexts !== undefined) {
    event.contexts = contexts;
  }

  const breadcrumbs = limitBreadcrumbs(
    input.breadcrumbs ?? input.scope.breadcrumbs,
    input.maxBreadcrumbs,
  );
  if (breadcrumbs !== undefined) {
    event.breadcrumbs = breadcrumbs;
  }

  const fingerprint = normalizeFingerprint(input.fingerprint);
  if (fingerprint !== undefined) {
    event.fingerprint = fingerprint;
  }

  const extra = limitRecord(input.extra, MAX_EXTRA_BYTES);
  if (extra !== undefined) {
    event.extra = extra;
  }

  return event;
}
