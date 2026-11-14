import { DEFAULT_RETRY_AFTER_SECONDS, DEFAULT_TIMEOUT_MS } from './limits.ts';
import type { GlitchPortEvent, ParsedDsn } from './types.ts';

/** Options accepted by {@link createTransport}. */
export interface TransportOptions {
  /** Parsed DSN: collector URL, project public key and project ID. */
  dsn: ParsedDsn;
  /** `fetch` implementation used for requests; defaults to `globalThis.fetch`. */
  fetchImpl?: typeof fetch;
  /** Per-request timeout in milliseconds (default `DEFAULT_TIMEOUT_MS`). */
  timeoutMs?: number;
  /** Log send, skip and timeout diagnostics to the console. */
  debug?: boolean;
}

/** HTTP transport used by the Supabase SDK client. */
export interface Transport {
  /** Send one event; resolves `true` only when the collector answered 202. */
  send(event: GlitchPortEvent): Promise<boolean>;
  /** Wait for in-flight sends; resolves `false` when they do not settle in time. */
  flush(timeoutMs?: number): Promise<boolean>;
  /** Number of sends that are currently in flight. */
  readonly pendingCount: number;
}

/** Prefix of every diagnostic line logged when `debug` is enabled. */
const LOG_PREFIX = '[glitchport]';

/**
 * Resolve a duration option, falling back to `fallback` for missing, negative
 * or non-finite values so a bad option can never disable the safety net.
 */
function normalizeDuration(value: number | undefined, fallback: number): number {
  if (value === undefined || !Number.isFinite(value) || value < 0) {
    return fallback;
  }
  return value;
}

/**
 * Formats accepted by `Retry-After` for an HTTP date (RFC 9110 section 5.6.7):
 * the preferred IMF-fixdate and the two obsolete formats.
 */
const HTTP_DATE_PATTERN =
  /^(?:[A-Za-z]{3}, \d{2} [A-Za-z]{3} \d{4} \d{2}:\d{2}:\d{2} GMT|[A-Za-z]+, \d{2}-[A-Za-z]{3}-\d{2} \d{2}:\d{2}:\d{2} GMT|[A-Za-z]{3} [A-Za-z]{3} [ \d]\d \d{2}:\d{2}:\d{2} \d{4})$/i;

/**
 * Convert a `Retry-After` header to a delay in milliseconds.
 *
 * The header is either a number of seconds or an HTTP date; anything else
 * (including a missing header) falls back to `DEFAULT_RETRY_AFTER_SECONDS`.
 */
function parseRetryAfter(value: string | null, now: number): number {
  const fallback = DEFAULT_RETRY_AFTER_SECONDS * 1000;
  if (value === null) {
    return fallback;
  }

  const trimmed = value.trim();
  if (trimmed === '') {
    return fallback;
  }

  if (/^\d+$/.test(trimmed)) {
    const seconds = Number(trimmed);
    return Number.isFinite(seconds) ? seconds * 1000 : fallback;
  }

  if (!HTTP_DATE_PATTERN.test(trimmed)) {
    return fallback;
  }
  const timestamp = Date.parse(trimmed);
  if (Number.isNaN(timestamp)) {
    return fallback;
  }
  return Math.max(0, timestamp - now);
}

/** Read a response header without letting a hostile response object throw. */
function readHeader(response: Response, name: string): string | null {
  try {
    return response.headers.get(name);
  } catch {
    return null;
  }
}

/** Render an unknown thrown value for diagnostics without logging payloads. */
function describeError(error: unknown): string {
  if (error instanceof Error) {
    return `${error.name}: ${error.message}`;
  }
  try {
    return String(error);
  } catch {
    return 'unknown error';
  }
}

/** Result of racing a request against its timeout timer. */
type RequestOutcome =
  | { readonly type: 'response'; readonly response: Response }
  | { readonly type: 'error'; readonly error: unknown }
  | { readonly type: 'timeout' };

/**
 * Create an HTTP transport for the GlitchPort collector.
 *
 * `send` never throws or rejects: it resolves `true` only for an HTTP 202 and
 * `false` for every other status, network failure, serialization failure or
 * timeout. A 429/503 response opens a `Retry-After` window during which sends
 * are dropped immediately without issuing a request.
 */
export function createTransport(options: TransportOptions): Transport {
  const endpoint = `${options.dsn.url}/api/${encodeURIComponent(options.dsn.projectId)}/events`;
  const requestTimeoutMs = normalizeDuration(options.timeoutMs, DEFAULT_TIMEOUT_MS);
  const debug = options.debug === true;
  const inFlight = new Set<Promise<boolean>>();
  let skipUntil = 0;

  function log(message: string, ...details: unknown[]): void {
    if (!debug) {
      return;
    }
    try {
      console.debug(`${LOG_PREFIX} transport: ${message}`, ...details);
    } catch {
      // Diagnostics must never break event reporting.
    }
  }

  function resolveFetch(): typeof fetch | undefined {
    if (typeof options.fetchImpl === 'function') {
      return options.fetchImpl;
    }
    const globalFetch = globalThis.fetch;
    return typeof globalFetch === 'function' ? globalFetch : undefined;
  }

  async function deliver(event: GlitchPortEvent): Promise<boolean> {
    const fetchImpl = resolveFetch();
    if (fetchImpl === undefined) {
      log('send dropped: no fetch implementation available');
      return false;
    }

    let body: string;
    try {
      const serialized: string | undefined = JSON.stringify(event);
      if (typeof serialized !== 'string') {
        log('send dropped: event could not be serialized');
        return false;
      }
      body = serialized;
    } catch {
      log('send dropped: event could not be serialized');
      return false;
    }

    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const timeout = new Promise<RequestOutcome>((resolve) => {
      timer = setTimeout(() => {
        // Resolve first: aborting may synchronously reject the fetch promise,
        // and the timeout diagnostic is the more useful outcome.
        resolve({ type: 'timeout' });
        try {
          controller.abort();
        } catch {
          // Some fetch implementations reject the abort signal itself.
        }
      }, requestTimeoutMs);
    });

    let fetchPromise: Promise<Response>;
    try {
      fetchPromise = Promise.resolve(
        fetchImpl(endpoint, {
          method: 'POST',
          headers: {
            'X-GlitchPort-Key': options.dsn.publicKey,
            'Content-Type': 'application/json',
          },
          body,
          signal: controller.signal,
        }),
      );
    } catch (error) {
      clearTimeout(timer);
      log('send failed before the request started', describeError(error));
      return false;
    }

    let outcome: RequestOutcome;
    try {
      outcome = await Promise.race<RequestOutcome>([
        fetchPromise.then(
          (response): RequestOutcome => ({ type: 'response', response }),
          (error: unknown): RequestOutcome => ({ type: 'error', error }),
        ),
        timeout,
      ]);
    } catch (error) {
      // Both contenders above are normalized, so this is only a safety net.
      clearTimeout(timer);
      log('send failed', describeError(error));
      return false;
    }
    clearTimeout(timer);

    if (outcome.type === 'timeout') {
      log(`send timed out after ${requestTimeoutMs} ms`);
      return false;
    }

    if (outcome.type === 'error') {
      log('send failed', describeError(outcome.error));
      return false;
    }

    const status = outcome.response.status;
    if (status === 202) {
      log(`send accepted (${status})`);
      return true;
    }

    if (status === 429 || status === 503) {
      const now = Date.now();
      const delayMs = parseRetryAfter(readHeader(outcome.response, 'Retry-After'), now);
      skipUntil = now + delayMs;
      log(`send retried later (${status}), skipping sends for ${delayMs} ms`);
      return false;
    }

    log(`send rejected (${status})`);
    return false;
  }

  function send(event: GlitchPortEvent): Promise<boolean> {
    try {
      const now = Date.now();
      if (now < skipUntil) {
        log(`send skipped, retry window ends in ${skipUntil - now} ms`);
        return Promise.resolve(false);
      }

      const pending = deliver(event).catch((error: unknown) => {
        log('send failed', describeError(error));
        return false;
      });
      inFlight.add(pending);
      pending.then(
        () => {
          inFlight.delete(pending);
        },
        () => {
          inFlight.delete(pending);
        },
      );
      return pending;
    } catch (error) {
      log('send failed', describeError(error));
      return Promise.resolve(false);
    }
  }

  function flush(timeoutMs: number = DEFAULT_TIMEOUT_MS): Promise<boolean> {
    try {
      const pending = [...inFlight];
      if (pending.length === 0) {
        return Promise.resolve(true);
      }

      const waitMs = normalizeDuration(timeoutMs, DEFAULT_TIMEOUT_MS);
      return new Promise<boolean>((resolve) => {
        let settled = false;
        let timer: ReturnType<typeof setTimeout> | undefined;

        const finish = (result: boolean): void => {
          if (settled) {
            return;
          }
          settled = true;
          if (timer !== undefined) {
            clearTimeout(timer);
          }
          resolve(result);
        };

        timer = setTimeout(() => {
          log(`flush timed out after ${waitMs} ms, ${inFlight.size} send(s) still pending`);
          finish(false);
        }, waitMs);

        void Promise.allSettled(pending).then(() => {
          log('flush completed, all pending sends settled');
          finish(true);
        });
      });
    } catch (error) {
      log('flush failed', describeError(error));
      return Promise.resolve(false);
    }
  }

  return {
    send,
    flush,
    get pendingCount(): number {
      return inFlight.size;
    },
  };
}
