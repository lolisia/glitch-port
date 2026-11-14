import { afterEach, describe, expect, it, vi } from 'vitest';
import { DEFAULT_TIMEOUT_MS } from '../src/limits.ts';
import { createTransport, type TransportOptions } from '../src/transport.ts';
import type { GlitchPortEvent, ParsedDsn } from '../src/types.ts';

const DSN: ParsedDsn = {
  url: 'https://glitchport.example.com',
  publicKey: 'public-key',
  projectId: '42',
};

function makeEvent(overrides: Partial<GlitchPortEvent> = {}): GlitchPortEvent {
  return {
    event_id: '0123456789abcdef0123456789abcdef',
    timestamp: '2026-01-01T00:00:00.000Z',
    level: 'error',
    platform: 'javascript',
    message: 'boom',
    sdk: { name: 'glitchport.supabase', version: '0.1.0' },
    ...overrides,
  };
}

interface FetchCall {
  url: string;
  init: RequestInit;
}

type Responder = (call: FetchCall) => Response | Promise<Response>;

/** Build a `fetch` stand-in that records every call and delegates its response. */
function fakeFetch(respond: Responder): { calls: FetchCall[]; fetchImpl: typeof fetch } {
  const calls: FetchCall[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const call: FetchCall = { url: String(input), init: init ?? {} };
    calls.push(call);
    return respond(call);
  };
  return { calls, fetchImpl };
}

function createTestTransport(
  fetchImpl: typeof fetch,
  options: Omit<Partial<TransportOptions>, 'dsn' | 'fetchImpl'> = {},
) {
  return createTransport({ dsn: DSN, fetchImpl, ...options });
}

function accepted(): Response {
  return new Response(null, { status: 202 });
}

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('send request', () => {
  it('posts the serialized event with the DSN key to the project endpoint', async () => {
    const { calls, fetchImpl } = fakeFetch(() => accepted());
    const transport = createTestTransport(fetchImpl);
    const event = makeEvent({ message: 'kaboom' });

    await expect(transport.send(event)).resolves.toBe(true);

    expect(calls).toHaveLength(1);
    const call = calls[0]!;
    expect(call.url).toBe('https://glitchport.example.com/api/42/events');
    expect(call.init.method).toBe('POST');

    const headers = new Headers(call.init.headers);
    expect(headers.get('X-GlitchPort-Key')).toBe('public-key');
    expect(headers.get('Content-Type')).toBe('application/json');

    expect(call.init.body).toBe(JSON.stringify(event));
    expect(JSON.parse(call.init.body as string).event_id).toBe(event.event_id);
    expect(call.init.signal).toBeInstanceOf(AbortSignal);
    expect(transport.pendingCount).toBe(0);
  });

  it('percent-encodes the project ID in the request path', async () => {
    const { calls, fetchImpl } = fakeFetch(() => accepted());
    const transport = createTransport({
      dsn: { ...DSN, projectId: 'team 1/a+b' },
      fetchImpl,
    });

    await expect(transport.send(makeEvent())).resolves.toBe(true);

    expect(calls[0]!.url).toBe('https://glitchport.example.com/api/team%201%2Fa%2Bb/events');
  });

  it.each([
    { status: 202, expected: true },
    { status: 200, expected: false },
    { status: 201, expected: false },
    { status: 400, expected: false },
    { status: 401, expected: false },
    { status: 413, expected: false },
    { status: 500, expected: false },
  ])('resolves $expected for HTTP $status', async ({ status, expected }) => {
    const { fetchImpl } = fakeFetch(() => new Response(null, { status }));
    const transport = createTestTransport(fetchImpl);

    await expect(transport.send(makeEvent())).resolves.toBe(expected);
  });
});

describe('retry after', () => {
  it('skips sends during a Retry-After seconds window and recovers at its end', async () => {
    vi.useFakeTimers();
    const start = new Date('2026-01-01T00:00:00.000Z');
    vi.setSystemTime(start);

    let status = 429;
    const { calls, fetchImpl } = fakeFetch(() =>
      new Response(null, {
        status,
        headers: status === 429 ? { 'Retry-After': '30' } : undefined,
      }),
    );
    const transport = createTestTransport(fetchImpl);

    await expect(transport.send(makeEvent())).resolves.toBe(false);
    expect(calls).toHaveLength(1);

    // Inside the window the send is dropped without issuing a request.
    await expect(transport.send(makeEvent())).resolves.toBe(false);
    vi.setSystemTime(start.getTime() + 29_999);
    await expect(transport.send(makeEvent())).resolves.toBe(false);
    expect(calls).toHaveLength(1);

    status = 202;
    vi.setSystemTime(start.getTime() + 30_000);
    await expect(transport.send(makeEvent())).resolves.toBe(true);
    expect(calls).toHaveLength(2);
  });

  it('does not open a retry window for other error statuses', async () => {
    const { calls, fetchImpl } = fakeFetch(() => new Response(null, { status: 500 }));
    const transport = createTestTransport(fetchImpl);

    await expect(transport.send(makeEvent())).resolves.toBe(false);
    await expect(transport.send(makeEvent())).resolves.toBe(false);

    expect(calls).toHaveLength(2);
  });

  it('accepts an HTTP-date Retry-After', async () => {
    vi.useFakeTimers();
    const start = new Date('2026-03-04T05:06:07.000Z');
    vi.setSystemTime(start);
    const retryAt = new Date(start.getTime() + 12_000).toUTCString();

    let status = 429;
    const { calls, fetchImpl } = fakeFetch(() =>
      new Response(null, {
        status,
        headers: status === 429 ? { 'Retry-After': retryAt } : undefined,
      }),
    );
    const transport = createTestTransport(fetchImpl);

    await expect(transport.send(makeEvent())).resolves.toBe(false);
    expect(calls).toHaveLength(1);

    vi.setSystemTime(start.getTime() + 11_999);
    await expect(transport.send(makeEvent())).resolves.toBe(false);
    expect(calls).toHaveLength(1);

    status = 202;
    vi.setSystemTime(start.getTime() + 12_000);
    await expect(transport.send(makeEvent())).resolves.toBe(true);
    expect(calls).toHaveLength(2);
  });

  it('falls back to a 5 second window for a 503 without Retry-After', async () => {
    vi.useFakeTimers();
    const start = new Date('2026-01-01T00:00:00.000Z');
    vi.setSystemTime(start);

    let status = 503;
    const { calls, fetchImpl } = fakeFetch(() => new Response(null, { status }));
    const transport = createTestTransport(fetchImpl);

    await expect(transport.send(makeEvent())).resolves.toBe(false);
    expect(calls).toHaveLength(1);

    vi.setSystemTime(start.getTime() + 4_999);
    await expect(transport.send(makeEvent())).resolves.toBe(false);
    expect(calls).toHaveLength(1);

    status = 202;
    vi.setSystemTime(start.getTime() + 5_000);
    await expect(transport.send(makeEvent())).resolves.toBe(true);
    expect(calls).toHaveLength(2);
  });

  it.each([{ header: 'soon' }, { header: '1.5' }, { header: '' }])(
    'falls back to a 5 second window for an invalid Retry-After ($header)',
    async ({ header }) => {
      vi.useFakeTimers();
      const start = new Date('2026-01-01T00:00:00.000Z');
      vi.setSystemTime(start);

      let status = 429;
      const { calls, fetchImpl } = fakeFetch(() =>
        new Response(null, {
          status,
          headers: status === 429 ? { 'Retry-After': header } : undefined,
        }),
      );
      const transport = createTestTransport(fetchImpl);

      await expect(transport.send(makeEvent())).resolves.toBe(false);
      expect(calls).toHaveLength(1);

      vi.setSystemTime(start.getTime() + 4_999);
      await expect(transport.send(makeEvent())).resolves.toBe(false);
      expect(calls).toHaveLength(1);

      status = 202;
      vi.setSystemTime(start.getTime() + 5_000);
      await expect(transport.send(makeEvent())).resolves.toBe(true);
      expect(calls).toHaveLength(2);
    },
  );
});

describe('timeouts', () => {
  it('uses the default 3000 ms timeout', async () => {
    vi.useFakeTimers();
    const fetchImpl: typeof fetch = () => new Promise<Response>(() => {});
    const transport = createTestTransport(fetchImpl);

    let settled = false;
    const sendPromise = transport.send(makeEvent());
    void sendPromise.then(() => {
      settled = true;
    });

    await vi.advanceTimersByTimeAsync(DEFAULT_TIMEOUT_MS - 1);
    expect(settled).toBe(false);
    expect(transport.pendingCount).toBe(1);

    await vi.advanceTimersByTimeAsync(1);
    await expect(sendPromise).resolves.toBe(false);
    expect(settled).toBe(true);
    expect(transport.pendingCount).toBe(0);
  });

  it('resolves false when fetch ignores the abort signal', async () => {
    vi.useFakeTimers();
    let signal: AbortSignal | undefined;
    const fetchImpl: typeof fetch = (_input, init) => {
      signal = init?.signal ?? undefined;
      return new Promise<Response>(() => {});
    };
    const transport = createTestTransport(fetchImpl, { timeoutMs: 250 });

    const sendPromise = transport.send(makeEvent());
    await vi.advanceTimersByTimeAsync(250);

    await expect(sendPromise).resolves.toBe(false);
    expect(signal?.aborted).toBe(true);
  });

  it('ignores a late fetch rejection after timing out', async () => {
    vi.useFakeTimers();
    let rejectFetch: ((error: unknown) => void) | undefined;
    const fetchImpl: typeof fetch = () =>
      new Promise<Response>((_resolve, reject) => {
        rejectFetch = reject;
      });
    const transport = createTestTransport(fetchImpl, { timeoutMs: 100 });

    const sendPromise = transport.send(makeEvent());
    await vi.advanceTimersByTimeAsync(100);
    await expect(sendPromise).resolves.toBe(false);

    rejectFetch?.(new Error('late network failure'));
    await vi.advanceTimersByTimeAsync(0);
  });

  it('clears the timeout timer when fetch settles first', async () => {
    vi.useFakeTimers();
    const { fetchImpl } = fakeFetch(() => accepted());
    const transport = createTestTransport(fetchImpl, { timeoutMs: 250 });

    await expect(transport.send(makeEvent())).resolves.toBe(true);

    expect(vi.getTimerCount()).toBe(0);
  });
});

describe('failure handling', () => {
  it('resolves false when fetch rejects', async () => {
    const { fetchImpl } = fakeFetch(() => Promise.reject(new Error('network down')));
    const transport = createTestTransport(fetchImpl);

    await expect(transport.send(makeEvent())).resolves.toBe(false);
    expect(transport.pendingCount).toBe(0);
  });

  it('resolves false when fetch throws synchronously', async () => {
    const fetchImpl: typeof fetch = () => {
      throw new Error('synchronous failure');
    };
    const transport = createTestTransport(fetchImpl);

    await expect(transport.send(makeEvent())).resolves.toBe(false);
    expect(transport.pendingCount).toBe(0);
  });

  it('resolves false when the event cannot be serialized', async () => {
    const circular: Record<string, unknown> = {};
    circular.self = circular;
    const { calls, fetchImpl } = fakeFetch(() => accepted());
    const transport = createTestTransport(fetchImpl);

    await expect(transport.send(makeEvent({ extra: circular }))).resolves.toBe(false);
    expect(calls).toHaveLength(0);
  });

  it('resolves false when JSON.stringify throws from toJSON', async () => {
    const { calls, fetchImpl } = fakeFetch(() => accepted());
    const transport = createTestTransport(fetchImpl);

    const event = makeEvent({
      extra: {
        toJSON(): never {
          throw new Error('cannot serialize');
        },
      },
    });

    await expect(transport.send(event)).resolves.toBe(false);
    expect(calls).toHaveLength(0);
  });

  it('resolves false when no fetch implementation is available', async () => {
    vi.stubGlobal('fetch', undefined);
    const transport = createTransport({ dsn: DSN });

    await expect(transport.send(makeEvent())).resolves.toBe(false);
  });
});

describe('flush and pendingCount', () => {
  it('is true immediately when nothing is pending', async () => {
    const { fetchImpl } = fakeFetch(() => accepted());
    const transport = createTestTransport(fetchImpl);

    expect(transport.pendingCount).toBe(0);
    await expect(transport.flush()).resolves.toBe(true);
  });

  it('tracks in-flight sends and resolves true once they settle', async () => {
    const resolvers: Array<(response: Response) => void> = [];
    const fetchImpl: typeof fetch = () =>
      new Promise<Response>((resolve) => {
        resolvers.push(resolve);
      });
    const transport = createTestTransport(fetchImpl);

    const first = transport.send(makeEvent());
    const second = transport.send(makeEvent());
    expect(transport.pendingCount).toBe(2);

    const flushed = transport.flush(1_000);
    resolvers[0]!(accepted());
    await expect(first).resolves.toBe(true);
    expect(transport.pendingCount).toBe(1);

    resolvers[1]!(accepted());
    await expect(second).resolves.toBe(true);
    expect(transport.pendingCount).toBe(0);
    await expect(flushed).resolves.toBe(true);
  });

  it('resolves false when pending sends do not settle in time', async () => {
    vi.useFakeTimers();
    let resolveFetch: ((response: Response) => void) | undefined;
    const fetchImpl: typeof fetch = () =>
      new Promise<Response>((resolve) => {
        resolveFetch = resolve;
      });
    const transport = createTestTransport(fetchImpl, { timeoutMs: 10_000 });

    const sendPromise = transport.send(makeEvent());
    const flushed = transport.flush(100);

    let flushSettled = false;
    void flushed.then(() => {
      flushSettled = true;
    });

    await vi.advanceTimersByTimeAsync(99);
    expect(flushSettled).toBe(false);

    await vi.advanceTimersByTimeAsync(1);
    await expect(flushed).resolves.toBe(false);
    expect(flushSettled).toBe(true);
    expect(transport.pendingCount).toBe(1);

    resolveFetch?.(accepted());
    await expect(sendPromise).resolves.toBe(true);
    expect(transport.pendingCount).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('uses the default timeout for flush', async () => {
    vi.useFakeTimers();
    let resolveFetch: ((response: Response) => void) | undefined;
    const fetchImpl: typeof fetch = () =>
      new Promise<Response>((resolve) => {
        resolveFetch = resolve;
      });
    const transport = createTestTransport(fetchImpl, { timeoutMs: 60_000 });

    const sendPromise = transport.send(makeEvent());
    const flushed = transport.flush();

    let flushSettled = false;
    void flushed.then(() => {
      flushSettled = true;
    });

    await vi.advanceTimersByTimeAsync(DEFAULT_TIMEOUT_MS - 1);
    expect(flushSettled).toBe(false);

    await vi.advanceTimersByTimeAsync(1);
    await expect(flushed).resolves.toBe(false);

    resolveFetch?.(accepted());
    await expect(sendPromise).resolves.toBe(true);
  });

  it('does not count sends dropped by a retry window as pending', async () => {
    vi.useFakeTimers();
    const { fetchImpl } = fakeFetch(
      () => new Response(null, { status: 429, headers: { 'Retry-After': '10' } }),
    );
    const transport = createTestTransport(fetchImpl);

    await transport.send(makeEvent());
    expect(transport.pendingCount).toBe(0);

    const dropped = transport.send(makeEvent());
    expect(transport.pendingCount).toBe(0);
    await expect(dropped).resolves.toBe(false);
  });
});

describe('debug logging', () => {
  it('logs prefixed diagnostics without payload contents', async () => {
    const debug = vi.spyOn(console, 'debug').mockImplementation(() => {});
    const { fetchImpl } = fakeFetch(() => accepted());
    const transport = createTestTransport(fetchImpl, { debug: true });

    await expect(transport.send(makeEvent({ message: 'super-secret-payload' }))).resolves.toBe(true);

    expect(debug).toHaveBeenCalled();
    for (const call of debug.mock.calls) {
      const line = call.map((value) => String(value)).join(' ');
      expect(line).toContain('[glitchport]');
      expect(line).not.toContain('super-secret-payload');
    }
  });

  it('logs skipped sends during a retry window', async () => {
    vi.useFakeTimers();
    const debug = vi.spyOn(console, 'debug').mockImplementation(() => {});
    const { fetchImpl } = fakeFetch(
      () => new Response(null, { status: 429, headers: { 'Retry-After': '10' } }),
    );
    const transport = createTestTransport(fetchImpl, { debug: true });

    await transport.send(makeEvent());
    debug.mockClear();

    await expect(transport.send(makeEvent())).resolves.toBe(false);

    const lines = debug.mock.calls.map((call) => call.map((value) => String(value)).join(' '));
    expect(lines.join('\n')).toMatch(/skip/i);
  });

  it('stays silent when debug is disabled', async () => {
    const debug = vi.spyOn(console, 'debug').mockImplementation(() => {});
    const { fetchImpl } = fakeFetch(() => accepted());
    const transport = createTestTransport(fetchImpl);

    await expect(transport.send(makeEvent())).resolves.toBe(true);

    expect(debug).not.toHaveBeenCalled();
  });
});
