import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DEFAULT_TIMEOUT_MS } from '../src/limits.ts';
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

/** Build a `fetch` stand-in that records every call and answers with a 202. */
function fakeFetch(
  respond: () => Response = () => new Response(null, { status: 202 }),
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

describe('init', () => {
  it('throws on an invalid DSN', () => {
    expect(() => glitchport.init({ dsn: 'not-a-dsn' })).toThrow(/Invalid DSN/);
    expect(() => glitchport.init({ dsn: '' })).toThrow(/Invalid DSN/);
    expect(() => glitchport.init({ dsn: 'https://glitchport.example.com/42' })).toThrow(
      /Invalid DSN/,
    );
  });

  it('stays uninitialized when the DSN is invalid', async () => {
    const { calls, fetchImpl } = fakeFetch();
    vi.stubGlobal('fetch', fetchImpl);

    expect(() => glitchport.init({ dsn: 'ftp://public-key@glitchport.example.com/42' })).toThrow(
      /Invalid DSN/,
    );

    expect(glitchport.captureMessage('still safe')).toMatch(EVENT_ID_PATTERN);
    await expect(glitchport.flush()).resolves.toBe(true);
    expect(calls).toHaveLength(0);
  });

  it('replaces the global scope', async () => {
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });
    glitchport.setTag('stale', 'yes');

    glitchport.init({ dsn: DSN, fetchImpl });
    glitchport.captureMessage('fresh');
    await glitchport.flush();

    const [event] = sentEvents(calls);
    expect(event.tags).toBeUndefined();
  });
});

describe('capture before init', () => {
  it('returns valid event ids without throwing or sending', async () => {
    const { calls, fetchImpl } = fakeFetch();
    vi.stubGlobal('fetch', fetchImpl);

    let exceptionId = '';
    let messageId = '';
    expect(() => {
      exceptionId = glitchport.captureException(new Error('before init'));
    }).not.toThrow();
    expect(() => {
      messageId = glitchport.captureMessage('before init');
    }).not.toThrow();

    expect(exceptionId).toMatch(EVENT_ID_PATTERN);
    expect(messageId).toMatch(EVENT_ID_PATTERN);
    await expect(glitchport.flush()).resolves.toBe(true);
    expect(calls).toHaveLength(0);
  });
});

describe('captureException and captureMessage', () => {
  it('uses level error by default and stores the configured metadata', async () => {
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({
      dsn: DSN,
      fetchImpl,
      environment: 'production',
      release: 'api@1.2.0',
      serverName: 'edge-1',
    });

    const id = glitchport.captureException(new Error('boom'));
    await glitchport.flush();

    expect(calls).toHaveLength(1);
    const [event] = sentEvents(calls);
    expect(event.event_id).toBe(id);
    expect(event.level).toBe('error');
    expect(event.environment).toBe('production');
    expect(event.release).toBe('api@1.2.0');
    expect(event.server_name).toBe('edge-1');
    expect(event.exception?.[0]).toMatchObject({ type: 'Error', value: 'boom' });
    expect(event.exception?.[0].stacktrace.frames.length).toBeGreaterThan(0);
  });

  it('uses level info by default for messages', async () => {
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });

    const id = glitchport.captureMessage('hello');
    await glitchport.flush();

    const [event] = sentEvents(calls);
    expect(event.event_id).toBe(id);
    expect(event.level).toBe('info');
    expect(event.message).toBe('hello');
    expect(event.exception).toBeUndefined();
  });

  it('honors explicit levels from the hint and the message argument', async () => {
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });

    glitchport.captureException(new Error('warning'), { level: 'warning' });
    await glitchport.flush();
    glitchport.captureMessage('fatal message', 'fatal');
    await glitchport.flush();

    const [exceptionEvent, messageEvent] = sentEvents(calls);
    expect(exceptionEvent.level).toBe('warning');
    expect(messageEvent.level).toBe('fatal');
  });

  it('merges scope tags, user, contexts and breadcrumbs with capture hints', async () => {
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });

    glitchport.setTag('service', 'edge');
    glitchport.setTag('shared', 'scope');
    glitchport.setUser({
      id: 'user-1',
      username: 'player',
      email: 'player@example.com',
    });
    glitchport.setContext('game', { name: 'demo', level: 3 });
    glitchport.addBreadcrumb({
      category: 'ui',
      message: 'start clicked',
      level: 'info',
      data: { button: 'start' },
    });

    const id = glitchport.captureException(new Error('boom'), {
      tags: { shared: 'hint', captureOnly: 'yes' },
      extra: { requestId: 'request-1' },
    });
    await glitchport.flush();

    const [event] = sentEvents(calls);
    expect(event.event_id).toBe(id);
    expect(event.tags).toEqual({
      service: 'edge',
      shared: 'hint',
      captureOnly: 'yes',
    });
    expect(event.user).toEqual({
      id: 'user-1',
      username: 'player',
      email: 'player@example.com',
    });
    expect(event.contexts).toEqual({ game: { name: 'demo', level: 3 } });
    expect(event.breadcrumbs).toEqual([
      expect.objectContaining({
        category: 'ui',
        message: 'start clicked',
        level: 'info',
        data: { button: 'start' },
      }),
    ]);
    expect(event.extra).toEqual({ requestId: 'request-1' });
  });

  it('captures plain values and follows the error cause chain', async () => {
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });

    const root = new Error('root cause');
    const top = new Error('top level', { cause: root });
    glitchport.captureException(top);
    await glitchport.flush();
    glitchport.captureException('plain failure');
    await glitchport.flush();

    const [chainedEvent, plainEvent] = sentEvents(calls);
    expect(chainedEvent.exception?.map((exception) => exception.value)).toEqual([
      'root cause',
      'top level',
    ]);
    expect(plainEvent.exception?.[0]).toMatchObject({
      type: 'Error',
      value: 'plain failure',
    });
  });

  it('returns a fresh 32 character lowercase hexadecimal event id per capture', async () => {
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });

    const ids = new Set<string>();
    for (let index = 0; index < 5; index += 1) {
      ids.add(glitchport.captureMessage(`message ${index}`));
    }
    await glitchport.flush();

    expect(ids.size).toBe(5);
    for (const id of ids) {
      expect(id).toMatch(EVENT_ID_PATTERN);
    }
    const events = sentEvents(calls);
    expect(new Set(events.map((event) => event.event_id))).toEqual(ids);
  });
});

describe('beforeSend', () => {
  it('drops the event without fetching when it returns null', async () => {
    const beforeSend = vi.fn(() => null);
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl, beforeSend });

    const id = glitchport.captureException(new Error('dropped'));
    await glitchport.flush();

    expect(id).toMatch(EVENT_ID_PATTERN);
    expect(beforeSend).toHaveBeenCalledTimes(1);
    expect(calls).toHaveLength(0);
  });

  it('sends the event returned by the hook', async () => {
    const beforeSend = vi.fn((event: GlitchPortEvent) => ({
      ...event,
      tags: { ...event.tags, processed: 'yes' },
    }));
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl, beforeSend });

    glitchport.captureMessage('processed');
    await glitchport.flush();

    const [event] = sentEvents(calls);
    expect(event.tags).toEqual({ processed: 'yes' });
  });

  it('drops the event instead of throwing when the hook throws', async () => {
    const beforeSend = vi.fn((): GlitchPortEvent | null => {
      throw new Error('hook failure');
    });
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl, beforeSend });

    let id = '';
    expect(() => {
      id = glitchport.captureException(new Error('boom'));
    }).not.toThrow();
    await glitchport.flush();

    expect(id).toMatch(EVENT_ID_PATTERN);
    expect(calls).toHaveLength(0);
  });
});

describe('sampleRate', () => {
  it('drops an event at or above the sample rate without fetching', async () => {
    vi.spyOn(Math, 'random').mockReturnValue(0.9);
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl, sampleRate: 0.5 });

    const id = glitchport.captureMessage('dropped');
    await glitchport.flush();

    expect(id).toMatch(EVENT_ID_PATTERN);
    expect(calls).toHaveLength(0);
  });

  it('sends an event below the sample rate', async () => {
    vi.spyOn(Math, 'random').mockReturnValue(0.1);
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl, sampleRate: 0.5 });

    glitchport.captureMessage('kept');
    await glitchport.flush();

    expect(calls).toHaveLength(1);
  });

  it('always drops when sampleRate is 0', async () => {
    vi.spyOn(Math, 'random').mockReturnValue(0);
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl, sampleRate: 0 });

    const id = glitchport.captureException(new Error('dropped'));
    await glitchport.flush();

    expect(id).toMatch(EVENT_ID_PATTERN);
    expect(calls).toHaveLength(0);
  });

  it('returns an event id without sending when sampling throws', async () => {
    vi.spyOn(Math, 'random').mockImplementation(() => {
      throw new Error('random failure');
    });
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl, sampleRate: 0.5 });

    let id = '';
    expect(() => {
      id = glitchport.captureException(new Error('boom'));
    }).not.toThrow();
    await glitchport.flush();

    expect(id).toMatch(EVENT_ID_PATTERN);
    expect(calls).toHaveLength(0);
  });
});

describe('defaults', () => {
  it('sends every event by default (sampleRate 1)', async () => {
    vi.spyOn(Math, 'random').mockReturnValue(0.999_999);
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });

    glitchport.captureMessage('default sample rate');
    await glitchport.flush();

    expect(calls).toHaveLength(1);
  });

  it('clamps sampleRate into 0..1 and treats non-finite values as 1', async () => {
    vi.spyOn(Math, 'random').mockReturnValue(0.999_999);
    const { calls, fetchImpl } = fakeFetch();

    glitchport.init({ dsn: DSN, fetchImpl, sampleRate: 10 });
    const highId = glitchport.captureMessage('clamped high');
    await glitchport.flush();

    glitchport.init({ dsn: DSN, fetchImpl, sampleRate: Number.NaN });
    const nanId = glitchport.captureMessage('non-finite');
    await glitchport.flush();

    glitchport.init({ dsn: DSN, fetchImpl, sampleRate: -3 });
    const lowId = glitchport.captureMessage('clamped low');
    await glitchport.flush();

    expect(calls).toHaveLength(2);
    expect(highId).toMatch(EVENT_ID_PATTERN);
    expect(nanId).toMatch(EVENT_ID_PATTERN);
    expect(lowId).toMatch(EVENT_ID_PATTERN);
  });

  it('keeps the last 50 breadcrumbs when maxBreadcrumbs is omitted', async () => {
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });

    for (let index = 0; index < 60; index += 1) {
      glitchport.addBreadcrumb({ message: `crumb ${index}` });
    }
    glitchport.captureMessage('with breadcrumbs');
    await glitchport.flush();

    const [event] = sentEvents(calls);
    expect(event.breadcrumbs).toHaveLength(50);
    expect(event.breadcrumbs?.[0].message).toBe('crumb 10');
    expect(event.breadcrumbs?.[49].message).toBe('crumb 59');
  });

  it('clamps maxBreadcrumbs to 0..100', async () => {
    const { calls, fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl, maxBreadcrumbs: 1000 });

    for (let index = 0; index < 120; index += 1) {
      glitchport.addBreadcrumb({ message: `crumb ${index}` });
    }
    glitchport.captureMessage('capped');
    await glitchport.flush();

    expect(sentEvents(calls)[0].breadcrumbs).toHaveLength(100);
  });

  it('uses the default 3000 ms timeout when flushing', async () => {
    vi.useFakeTimers();
    const fetchImpl: typeof fetch = () => new Promise<Response>(() => {});
    glitchport.init({ dsn: DSN, fetchImpl });

    glitchport.captureMessage('pending');
    const flushed = glitchport.flush();
    let settled = false;
    void flushed.then(() => {
      settled = true;
    });

    await vi.advanceTimersByTimeAsync(DEFAULT_TIMEOUT_MS - 1);
    expect(settled).toBe(false);

    await vi.advanceTimersByTimeAsync(1);
    // The transport send times out at the same instant, so only assert that
    // the flush itself settles once the default 3000 ms window closes.
    expect(typeof (await flushed)).toBe('boolean');
    expect(settled).toBe(true);
  });
});

describe('flush', () => {
  it('resolves true when nothing is pending', async () => {
    const { fetchImpl } = fakeFetch();
    glitchport.init({ dsn: DSN, fetchImpl });

    await expect(glitchport.flush()).resolves.toBe(true);
  });

  it('accepts an explicit timeout', async () => {
    vi.useFakeTimers();
    const fetchImpl: typeof fetch = () => new Promise<Response>(() => {});
    glitchport.init({ dsn: DSN, fetchImpl, timeoutMs: 50_000 });

    glitchport.captureMessage('pending');
    const flushed = glitchport.flush(100);
    let settled = false;
    void flushed.then(() => {
      settled = true;
    });

    await vi.advanceTimersByTimeAsync(99);
    expect(settled).toBe(false);

    await vi.advanceTimersByTimeAsync(1);
    await expect(flushed).resolves.toBe(false);
  });
});

describe('failure safety', () => {
  it('never throws when the transport cannot be used', async () => {
    vi.stubGlobal('fetch', undefined);
    glitchport.init({ dsn: DSN });

    let id = '';
    expect(() => {
      id = glitchport.captureException(new Error('no fetch'));
    }).not.toThrow();
    await expect(glitchport.flush()).resolves.toBe(true);

    expect(id).toMatch(EVENT_ID_PATTERN);
  });
});
