import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { GlitchPortEvent } from '../src/types.ts';

const DSN = 'https://public-key@glitchport.example.com/42';
const EVENT_ID_PATTERN = /^[0-9a-f]{32}$/;

interface FetchCall {
  url: string;
  init: RequestInit;
}

interface FakeFetch {
  calls: FetchCall[];
  fetchImpl: typeof fetch;
}

/** Build a `fetch` stand-in that records every call and delegates its response. */
function fakeFetch(
  respond: () => Response | Promise<Response> = () => new Response(null, { status: 202 }),
): FakeFetch {
  const calls: FetchCall[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    calls.push({ url: String(input), init: init ?? {} });
    return respond();
  };
  return { calls, fetchImpl };
}

/** Decode the event body of every recorded transport request. */
function sentEvents(calls: FetchCall[]): GlitchPortEvent[] {
  return calls.map((call) => JSON.parse(String(call.init.body)) as GlitchPortEvent);
}

/** A promise whose settlement the test controls. */
function deferred<T>(): {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (error: unknown) => void;
} {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

interface RequestContext {
  method: string;
  url: string;
  headers: Record<string, string>;
}

function requestContextOf(event: GlitchPortEvent): RequestContext {
  return event.contexts?.['request'] as RequestContext;
}

let glitchport!: typeof import('../src/glitchport.ts');

beforeEach(async () => {
  vi.resetModules();
  glitchport = await import('../src/glitchport.ts');
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('withGlitchPort error handling', () => {
  it('captures one error event and returns the standard 500 response', async () => {
    vi.stubGlobal('Deno', { version: { deno: '1.46.2' } });
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });

    const wrapped = glitchport.withGlitchPort(() => {
      throw new Error('boom');
    });
    const request = new Request(
      'https://edge.example.com/functions/v1/hello?secret=1#fragment',
      {
        method: 'post',
        headers: {
          'user-agent': 'test-agent',
          'content-type': 'application/json',
          authorization: 'Bearer top-secret',
          cookie: 'session=secret',
          'x-forwarded-for': '203.0.113.7',
        },
      },
    );

    const response = await wrapped(request, { region: 'us-east-1' });

    expect(response.status).toBe(500);
    expect(response.headers.get('content-type')).toBe('application/json');
    const text = await response.text();
    const body = JSON.parse(text) as { error: string; event_id: string };
    expect(Object.keys(body)).toEqual(['error', 'event_id']);
    expect(body.error).toBe('Internal Server Error');
    expect(body.event_id).toMatch(EVENT_ID_PATTERN);
    expect(text).toBe(
      JSON.stringify({ error: 'Internal Server Error', event_id: body.event_id }),
    );

    expect(calls).toHaveLength(1);
    const [event] = sentEvents(calls);
    expect(event.event_id).toBe(body.event_id);
    expect(event.level).toBe('error');
    expect(event.exception).toHaveLength(1);
    expect(event.exception?.[0].type).toBe('Error');
    expect(event.exception?.[0].value).toBe('boom');
    expect(event.exception?.[0].stacktrace.frames.length).toBeGreaterThan(0);
    expect(event.tags).toEqual({ 'http.method': 'POST' });
    expect(event.contexts?.['runtime']).toEqual({ name: 'deno', version: '1.46.2' });

    const requestContext = requestContextOf(event);
    expect(requestContext.method).toBe('POST');
    expect(requestContext.url).toBe('https://edge.example.com/functions/v1/hello');
    expect(requestContext.headers).toEqual({
      'user-agent': 'test-agent',
      'content-type': 'application/json',
    });
    expect(Object.keys(requestContext.headers).sort()).toEqual([
      'content-type',
      'user-agent',
    ]);
    expect(requestContext.headers['authorization']).toBeUndefined();
    expect(requestContext.headers['cookie']).toBeUndefined();
    expect(requestContext.headers['x-forwarded-for']).toBeUndefined();
  });

  it('omits the runtime context unless the Deno version is a string', async () => {
    vi.stubGlobal('Deno', { version: { deno: 123 } });
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });

    const wrapped = glitchport.withGlitchPort(() => {
      throw new Error('boom');
    });
    await wrapped(new Request('https://example.com/no-runtime'));

    const [event] = sentEvents(calls);
    expect(event.contexts).not.toHaveProperty('runtime');
  });

  it('rethrows the original value when rethrow is true', async () => {
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });
    const failure = new Error('rethrow me');
    const wrapped = glitchport.withGlitchPort(
      () => {
        throw failure;
      },
      { rethrow: true },
    );

    await expect(wrapped(new Request('https://example.com/rethrow'))).rejects.toBe(failure);
    expect(calls).toHaveLength(1);
    expect(sentEvents(calls)[0].level).toBe('error');
  });

  it('rethrows non-Error values unchanged', async () => {
    const { fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });
    const wrapped = glitchport.withGlitchPort(
      () => {
        throw 'plain failure';
      },
      { rethrow: true },
    );

    await expect(wrapped(new Request('https://example.com/plain'))).rejects.toBe(
      'plain failure',
    );
  });
});

describe('withGlitchPort normal responses', () => {
  it('returns the handler response unchanged and sends nothing', async () => {
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });
    const expected = new Response('ok', { status: 201 });
    const wrapped = glitchport.withGlitchPort(() => expected);

    const actual = await wrapped(new Request('https://example.com/ok'));

    expect(actual).toBe(expected);
    expect(await actual.text()).toBe('ok');
    expect(calls).toHaveLength(0);
  });

  it('passes the runtime info argument through to the handler', async () => {
    const { fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });
    const wrapped = glitchport.withGlitchPort(
      (_req, info) => new Response(JSON.stringify(info)),
    );

    const response = await wrapped(new Request('https://example.com/info'), {
      region: 'us-east-1',
    });

    await expect(response.json()).resolves.toEqual({ region: 'us-east-1' });
  });
});

describe('request scopes', () => {
  it('keeps concurrent request scopes separate', async () => {
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });

    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = () => resolve();
    });

    const wrapped = glitchport.withGlitchPort(async (req) => {
      const requestScope = glitchport.scope(req);
      const path = new URL(req.url).pathname;
      requestScope.setTag('request.path', path);
      requestScope.setContext('request.info', { path });
      await gate;
      requestScope.captureException(new Error(`boom ${path}`));
      return new Response('ok');
    });

    const first = wrapped(new Request('https://example.com/alpha?token=a'));
    const second = wrapped(new Request('https://example.com/beta?token=b'));
    release();
    const responses = await Promise.all([first, second]);
    expect(responses.map((response) => response.status)).toEqual([200, 200]);
    await glitchport.flush();

    expect(calls).toHaveLength(2);
    const events = sentEvents(calls);
    const byPath = new Map(
      events.map((event) => [event.tags?.['request.path'], event]),
    );

    expect(byPath.get('/alpha')?.tags).toMatchObject({
      'request.path': '/alpha',
      'http.method': 'GET',
    });
    expect(byPath.get('/beta')?.tags).toMatchObject({
      'request.path': '/beta',
      'http.method': 'GET',
    });
    expect(byPath.get('/alpha')?.contexts?.['request.info']).toEqual({
      path: '/alpha',
    });
    expect(byPath.get('/beta')?.contexts?.['request.info']).toEqual({
      path: '/beta',
    });
    expect(byPath.get('/alpha')?.exception?.[0].value).toBe('boom /alpha');
    expect(byPath.get('/beta')?.exception?.[0].value).toBe('boom /beta');
  });

  it('applies every request scope method to request-scoped captures', async () => {
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });

    const wrapped = glitchport.withGlitchPort((req) => {
      const requestScope = glitchport.scope(req);
      requestScope.setTag('route', 'hello');
      requestScope.setUser({ id: 'user-1', username: 'player' });
      requestScope.setContext('custom', { attempt: 1 });
      requestScope.addBreadcrumb({ category: 'test', message: 'before capture' });
      return new Response(requestScope.captureMessage('request message'));
    });

    const response = await wrapped(new Request('https://example.com/scope'));
    const eventId = await response.text();
    await glitchport.flush();

    const [event] = sentEvents(calls);
    expect(event.event_id).toBe(eventId);
    expect(event.level).toBe('info');
    expect(event.message).toBe('request message');
    expect(event.tags).toMatchObject({ route: 'hello', 'http.method': 'GET' });
    expect(event.user).toEqual({ id: 'user-1', username: 'player' });
    expect(event.contexts?.['custom']).toEqual({ attempt: 1 });
    expect(event.breadcrumbs?.[0].message).toBe('before capture');
  });

  it('keeps module-level helpers on the global scope inside a handler', async () => {
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });

    const wrapped = glitchport.withGlitchPort((req) => {
      glitchport.scope(req).setTag('request.only', 'yes');
      glitchport.setTag('global.only', 'yes');
      glitchport.captureMessage('global capture');
      return new Response('ok');
    });
    await wrapped(new Request('https://example.com/global'));
    await glitchport.flush();

    const [event] = sentEvents(calls);
    expect(event.message).toBe('global capture');
    expect(event.tags).toMatchObject({ 'global.only': 'yes' });
    expect(event.tags).not.toHaveProperty('request.only');
    expect(event.tags).not.toHaveProperty('http.method');
  });

  it('throws for a request that is not inside withGlitchPort', () => {
    const request = new Request('https://example.com/outside');

    expect(() => glitchport.scope(request)).toThrow(/withGlitchPort/);
  });

  it('forgets the request scope once the handler has finished', async () => {
    const { fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });
    let handledRequest: Request | undefined;
    const wrapped = glitchport.withGlitchPort((req) => {
      handledRequest = req;
      return new Response('ok');
    });
    const request = new Request('https://example.com/finished');

    await wrapped(request);

    expect(handledRequest).toBe(request);
    expect(() => glitchport.scope(request)).toThrow(/withGlitchPort/);
  });
});

describe('flush before responding', () => {
  it('hands the flush promise to EdgeRuntime.waitUntil when available', async () => {
    const waitUntil = vi.fn();
    vi.stubGlobal('EdgeRuntime', { waitUntil });
    const pendingResponse = deferred<Response>();
    const { calls, fetchImpl } = fakeFetch(() => pendingResponse.promise);
    glitchport.init({ dsn: DSN, fetchImpl, timeoutMs: 500 });

    const wrapped = glitchport.withGlitchPort(() => {
      throw new Error('edge boom');
    });
    const response = await wrapped(new Request('https://example.com/edge'));

    expect(response.status).toBe(500);
    expect(calls).toHaveLength(1);
    expect(waitUntil).toHaveBeenCalledTimes(1);

    const flushed = waitUntil.mock.calls[0]?.[0] as Promise<boolean>;
    expect(flushed).toBeInstanceOf(Promise);

    pendingResponse.resolve(new Response(null, { status: 202 }));
    await expect(flushed).resolves.toBe(true);
  });

  it('awaits flush before responding without EdgeRuntime.waitUntil', async () => {
    const pendingResponse = deferred<Response>();
    const { calls, fetchImpl } = fakeFetch(() => pendingResponse.promise);
    glitchport.init({ dsn: DSN, fetchImpl, timeoutMs: 500 });

    const wrapped = glitchport.withGlitchPort(() => {
      throw new Error('slow boom');
    });
    const pending = wrapped(new Request('https://example.com/slow'));
    let settled = false;
    void pending.then(() => {
      settled = true;
    });

    await Promise.resolve();
    expect(calls).toHaveLength(1);
    expect(settled).toBe(false);

    pendingResponse.resolve(new Response(null, { status: 202 }));
    const response = await pending;
    expect(response.status).toBe(500);
    expect(settled).toBe(true);
  });
});
