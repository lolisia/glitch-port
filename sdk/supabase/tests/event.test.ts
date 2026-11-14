import { describe, expect, it, vi } from 'vitest';
import { createEvent, createEventId, type CreateEventInput } from '../src/event.ts';
import { Scope } from '../src/scope.ts';
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
} from '../src/limits.ts';
import type {
  GlitchPortBreadcrumb,
  GlitchPortException,
  GlitchPortStackFrame,
  Level,
  ScopeSnapshot,
} from '../src/types.ts';

const EVENT_ID_PATTERN = /^[0-9a-f]{32}$/;
const ISO_TIMESTAMP_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/;

function emptyScope(): ScopeSnapshot {
  return new Scope().snapshot();
}

function makeFrame(
  index: number,
  overrides: Partial<GlitchPortStackFrame> = {},
): GlitchPortStackFrame {
  return {
    function: `fn${index}`,
    filename: `file${index}.ts`,
    lineno: index,
    colno: 1,
    in_app: true,
    ...overrides,
  };
}

function makeException(overrides: Partial<GlitchPortException> = {}): GlitchPortException {
  return {
    type: 'Error',
    value: 'boom',
    stacktrace: { frames: [makeFrame(0)] },
    ...overrides,
  };
}

function makeBreadcrumb(index: number): GlitchPortBreadcrumb {
  return {
    category: 'test',
    message: `crumb ${index}`,
    level: 'info',
    timestamp: new Date(Date.UTC(2026, 0, 1, 0, 0, index)).toISOString(),
  };
}

describe('createEventId', () => {
  it('generates 32 lowercase hexadecimal characters', () => {
    const ids = new Set<string>();
    for (let index = 0; index < 25; index += 1) {
      const id = createEventId();
      expect(id).toMatch(EVENT_ID_PATTERN);
      expect(id).toBe(id.toLowerCase());
      ids.add(id);
    }
    expect(ids.size).toBeGreaterThan(1);
  });

  it('removes the dashes from crypto.randomUUID()', () => {
    vi.stubGlobal('crypto', {
      randomUUID: () => 'ABCDEF01-2345-6789-ABCD-EF0123456789',
    });
    try {
      expect(createEventId()).toBe('abcdef0123456789abcdef0123456789');
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('falls back to crypto.getRandomValues() without randomUUID', () => {
    vi.stubGlobal('crypto', {
      getRandomValues: (bytes: Uint8Array) => {
        bytes.fill(0xab);
        return bytes;
      },
    });
    try {
      expect(createEventId()).toBe('ab'.repeat(16));
    } finally {
      vi.unstubAllGlobals();
    }
  });
});

describe('Scope', () => {
  it('starts empty by default', () => {
    expect(new Scope().snapshot()).toEqual({ tags: {}, contexts: {}, breadcrumbs: [] });
  });

  it('keeps at most 50 tags and drops the oldest key', () => {
    const scope = new Scope();
    for (let index = 0; index < MAX_TAGS + 5; index += 1) {
      scope.setTag(`key-${index}`, `value-${index}`);
    }

    const { tags } = scope.snapshot();
    expect(Object.keys(tags)).toHaveLength(MAX_TAGS);
    expect(tags['key-4']).toBeUndefined();
    expect(tags['key-5']).toBe('value-5');
    expect(tags[`key-${MAX_TAGS + 4}`]).toBe(`value-${MAX_TAGS + 4}`);
  });

  it('updates an existing tag without dropping another one', () => {
    const scope = new Scope();
    for (let index = 0; index < MAX_TAGS; index += 1) {
      scope.setTag(`key-${index}`, `value-${index}`);
    }

    scope.setTag('key-0', 'updated');
    const { tags } = scope.snapshot();
    expect(Object.keys(tags)).toHaveLength(MAX_TAGS);
    expect(tags['key-0']).toBe('updated');
    expect(tags[`key-${MAX_TAGS - 1}`]).toBe(`value-${MAX_TAGS - 1}`);
  });

  it('clears the user and a context with null', () => {
    const scope = new Scope();
    scope.setUser({ id: 'user-1' });
    scope.setContext('request', { method: 'GET' });

    expect(scope.snapshot().user).toEqual({ id: 'user-1' });
    expect(scope.snapshot().contexts['request']).toEqual({ method: 'GET' });

    scope.setUser(null);
    scope.setContext('request', null);

    expect(scope.snapshot().user).toBeUndefined();
    expect(scope.snapshot().contexts).toEqual({});
  });

  it('fills in the breadcrumb timestamp and level', () => {
    const scope = new Scope();
    scope.addBreadcrumb({ message: 'hello' });

    const [breadcrumb] = scope.snapshot().breadcrumbs;
    expect(breadcrumb.message).toBe('hello');
    expect(breadcrumb.level).toBe('info');
    expect(breadcrumb.timestamp).toMatch(ISO_TIMESTAMP_PATTERN);
  });

  it('preserves an explicit breadcrumb level and timestamp', () => {
    const scope = new Scope();
    const breadcrumb = {
      category: 'http',
      message: 'request',
      level: 'warning' as Level,
      timestamp: '2026-01-02T03:04:05.000Z',
      data: { status: 500 },
    };
    scope.addBreadcrumb(breadcrumb);

    expect(scope.snapshot().breadcrumbs[0]).toEqual(breadcrumb);
  });

  it('keeps only the most recent breadcrumbs (default 50)', () => {
    const scope = new Scope();
    for (let index = 0; index < DEFAULT_MAX_BREADCRUMBS + 10; index += 1) {
      scope.addBreadcrumb({ message: `crumb ${index}` });
    }

    const { breadcrumbs } = scope.snapshot();
    expect(breadcrumbs).toHaveLength(DEFAULT_MAX_BREADCRUMBS);
    expect(breadcrumbs[0].message).toBe('crumb 10');
    expect(breadcrumbs[DEFAULT_MAX_BREADCRUMBS - 1].message).toBe(
      `crumb ${DEFAULT_MAX_BREADCRUMBS + 9}`,
    );
  });

  it('clamps maxBreadcrumbs to 0..100', () => {
    const capped = new Scope({}, { maxBreadcrumbs: 1000 });
    for (let index = 0; index < MAX_BREADCRUMBS + 20; index += 1) {
      capped.addBreadcrumb({ message: `crumb ${index}` });
    }
    const cappedBreadcrumbs = capped.snapshot().breadcrumbs;
    expect(cappedBreadcrumbs).toHaveLength(MAX_BREADCRUMBS);
    expect(cappedBreadcrumbs[0].message).toBe('crumb 20');

    const disabled = new Scope({}, { maxBreadcrumbs: -10 });
    disabled.addBreadcrumb({ message: 'ignored' });
    expect(disabled.snapshot().breadcrumbs).toEqual([]);

    const small = new Scope({}, { maxBreadcrumbs: 3 });
    for (let index = 0; index < 5; index += 1) {
      small.addBreadcrumb({ message: `crumb ${index}` });
    }
    expect(small.snapshot().breadcrumbs.map((breadcrumb) => breadcrumb.message)).toEqual([
      'crumb 2',
      'crumb 3',
      'crumb 4',
    ]);
  });

  it('clones without sharing mutable state and preserves maxBreadcrumbs', () => {
    const original = new Scope(
      {
        tags: { a: '1' },
        user: { id: 'user-1' },
        contexts: { request: { method: 'GET' } },
        breadcrumbs: [makeBreadcrumb(0)],
      },
      { maxBreadcrumbs: 3 },
    );
    const before = original.snapshot();
    const clone = original.clone();

    clone.setTag('a', 'changed');
    clone.setTag('b', '2');
    clone.setUser({ id: 'user-2' });
    clone.setContext('request', { method: 'POST' });
    clone.setContext('extra', { ok: true });
    for (let index = 0; index < 10; index += 1) {
      clone.addBreadcrumb({ message: `clone ${index}` });
    }

    expect(original.snapshot()).toEqual(before);

    const cloned = clone.snapshot();
    expect(cloned.tags).toEqual({ a: 'changed', b: '2' });
    expect(cloned.user).toEqual({ id: 'user-2' });
    expect(cloned.contexts).toEqual({ request: { method: 'POST' }, extra: { ok: true } });
    expect(cloned.breadcrumbs).toHaveLength(3);
    expect(cloned.breadcrumbs.map((breadcrumb) => breadcrumb.message)).toEqual([
      'clone 7',
      'clone 8',
      'clone 9',
    ]);
  });

  it('returns snapshots that cannot mutate the scope', () => {
    const scope = new Scope({
      tags: { a: '1' },
      user: { id: 'user-1' },
      contexts: { request: { method: 'GET' } },
      breadcrumbs: [makeBreadcrumb(0)],
    });

    const snapshot = scope.snapshot();
    snapshot.tags['a'] = 'changed';
    snapshot.contexts['request'] = { method: 'POST' };
    snapshot.breadcrumbs[0].message = 'changed';
    snapshot.user!.id = 'user-2';

    const fresh = scope.snapshot();
    expect(fresh.tags['a']).toBe('1');
    expect(fresh.contexts['request']).toEqual({ method: 'GET' });
    expect(fresh.breadcrumbs[0].message).toBe('crumb 0');
    expect(fresh.user).toEqual({ id: 'user-1' });
  });
});

describe('createEvent', () => {
  it('builds a protocol v1 event with the required snake_case fields', () => {
    const event = createEvent({ level: 'error', message: 'boom', scope: emptyScope() });

    expect(event.event_id).toMatch(EVENT_ID_PATTERN);
    expect(event.timestamp).toMatch(ISO_TIMESTAMP_PATTERN);
    expect(event.level).toBe('error');
    expect(event.platform).toBe('javascript');
    expect(event.sdk).toEqual({ name: SDK_NAME, version: SDK_VERSION });
    expect(event.sdk.name).toBe('glitchport.supabase');
    expect(event.sdk.version).toBe('0.1.0');
    expect(event.message).toBe('boom');

    expect(Object.keys(event).sort()).toEqual([
      'event_id',
      'level',
      'message',
      'platform',
      'sdk',
      'timestamp',
    ]);
    for (const key of Object.keys(event)) {
      expect(key).toMatch(/^[a-z][a-z0-9_]*$/);
    }
  });

  it('uses the provided event ID and timestamp and copies optional fields', () => {
    const event = createEvent({
      level: 'info',
      message: 'hello',
      scope: emptyScope(),
      eventId: '0123456789abcdef0123456789abcdef',
      timestamp: '2026-01-02T03:04:05.000Z',
      logger: 'edge-function',
      options: {
        environment: 'production',
        release: 'abc123',
        serverName: 'edge-1',
      },
      tags: { region: 'us-east-1' },
      contexts: { runtime: { name: 'deno' } },
      extra: { requestId: 'request-1' },
    });

    expect(event.event_id).toBe('0123456789abcdef0123456789abcdef');
    expect(event.timestamp).toBe('2026-01-02T03:04:05.000Z');
    expect(event.logger).toBe('edge-function');
    expect(event.environment).toBe('production');
    expect(event.release).toBe('abc123');
    expect(event.server_name).toBe('edge-1');
    expect(event.tags).toEqual({ region: 'us-east-1' });
    expect(event.contexts).toEqual({ runtime: { name: 'deno' } });
    expect(event.extra).toEqual({ requestId: 'request-1' });
  });

  it('falls back to error for an invalid level', () => {
    const event = createEvent({
      level: 'verbose' as Level,
      message: 'boom',
      scope: emptyScope(),
    });
    expect(event.level).toBe('error');
  });

  it('keeps an explicitly provided empty message', () => {
    const event = createEvent({ level: 'info', message: '', scope: emptyScope() });
    expect(event.message).toBe('');
  });

  it('throws only when there is no message and no exception', () => {
    const scope = emptyScope();

    expect(() => createEvent({ level: 'error', scope })).toThrow(Error);
    expect(() => createEvent({ level: 'error', scope, exceptions: [] })).toThrow(
      /message or at least one exception/,
    );

    expect(() => createEvent({ level: 'error', scope, message: '' })).not.toThrow();
    expect(() =>
      createEvent({ level: 'error', scope, exceptions: [makeException()] }),
    ).not.toThrow();
  });

  it('truncates message and exception strings to protocol limits', () => {
    const longMessage = 'm'.repeat(MAX_MESSAGE_LENGTH + 25);
    const longType = 't'.repeat(MAX_EXCEPTION_TYPE_LENGTH + 25);
    const longValue = 'v'.repeat(MAX_VALUE_LENGTH + 25);
    const longModule = 'o'.repeat(MAX_EXCEPTION_TYPE_LENGTH + 25);
    const longFunction = 'f'.repeat(MAX_FRAME_STRING_LENGTH + 25);
    const longFilename = 'n'.repeat(MAX_FRAME_STRING_LENGTH + 25);
    const longModuleFrame = 'd'.repeat(MAX_FRAME_STRING_LENGTH + 25);
    const longAbsPath = 'p'.repeat(MAX_FRAME_STRING_LENGTH + 25);

    const event = createEvent({
      level: 'error',
      message: longMessage,
      scope: emptyScope(),
      exceptions: [
        {
          type: longType,
          value: longValue,
          module: longModule,
          stacktrace: {
            frames: [
              makeFrame(0, {
                function: longFunction,
                filename: longFilename,
                module: longModuleFrame,
                abs_path: longAbsPath,
              }),
            ],
          },
        },
      ],
    });

    expect(event.message).toHaveLength(MAX_MESSAGE_LENGTH);
    const exception = event.exception![0];
    expect(exception.type).toHaveLength(MAX_EXCEPTION_TYPE_LENGTH);
    expect(exception.value).toHaveLength(MAX_VALUE_LENGTH);
    expect(exception.module).toHaveLength(MAX_EXCEPTION_TYPE_LENGTH);
    const frame = exception.stacktrace.frames[0];
    expect(frame.function).toHaveLength(MAX_FRAME_STRING_LENGTH);
    expect(frame.filename).toHaveLength(MAX_FRAME_STRING_LENGTH);
    expect(frame.module).toHaveLength(MAX_FRAME_STRING_LENGTH);
    expect(frame.abs_path).toHaveLength(MAX_FRAME_STRING_LENGTH);
  });

  it('truncates logger, release, environment and server name', () => {
    const event = createEvent({
      level: 'info',
      message: 'boom',
      scope: emptyScope(),
      logger: 'l'.repeat(MAX_LOGGER_LENGTH + 10),
      options: {
        release: 'r'.repeat(MAX_RELEASE_LENGTH + 10),
        environment: 'e'.repeat(MAX_ENVIRONMENT_LENGTH + 10),
        serverName: 's'.repeat(MAX_SERVER_NAME_LENGTH + 10),
      },
    });

    expect(event.logger).toHaveLength(MAX_LOGGER_LENGTH);
    expect(event.release).toHaveLength(MAX_RELEASE_LENGTH);
    expect(event.environment).toHaveLength(MAX_ENVIRONMENT_LENGTH);
    expect(event.server_name).toHaveLength(MAX_SERVER_NAME_LENGTH);
  });

  it('keeps at most 10 exceptions from the root side', () => {
    const exceptions = Array.from({ length: MAX_EXCEPTIONS + 5 }, (_, index) =>
      makeException({ type: `Error${index}`, value: `exception ${index}` }),
    );

    const event = createEvent({ level: 'error', scope: emptyScope(), exceptions });

    expect(event.exception).toHaveLength(MAX_EXCEPTIONS);
    expect(event.exception!.map((exception) => exception.value)).toEqual(
      exceptions.slice(0, MAX_EXCEPTIONS).map((exception) => exception.value),
    );
    expect(event.exception![MAX_EXCEPTIONS - 1].value).toBe(
      `exception ${MAX_EXCEPTIONS - 1}`,
    );
  });

  it('keeps at most 250 frames per exception, from the throw-site side', () => {
    const frames = Array.from({ length: MAX_FRAMES + 50 }, (_, index) => makeFrame(index));

    const event = createEvent({
      level: 'error',
      message: 'boom',
      scope: emptyScope(),
      exceptions: [makeException({ stacktrace: { frames } })],
    });

    const kept = event.exception![0].stacktrace.frames;
    expect(kept).toHaveLength(MAX_FRAMES);
    expect(kept[0].lineno).toBe(50);
    expect(kept[MAX_FRAMES - 1].lineno).toBe(MAX_FRAMES + 49);
  });

  it('merges scope and capture tags with capture precedence', () => {
    const scope = new Scope({
      tags: { shared: 'scope', scopeOnly: 'scope-only' },
    }).snapshot();

    const event = createEvent({
      level: 'info',
      message: 'boom',
      scope,
      tags: { shared: 'capture', captureOnly: 'capture-only' },
    });

    expect(event.tags).toEqual({
      shared: 'capture',
      scopeOnly: 'scope-only',
      captureOnly: 'capture-only',
    });
    expect(Object.keys(event.tags!)).toEqual(['shared', 'scopeOnly', 'captureOnly']);
  });

  it('keeps the first 50 tags and truncates tag keys and values', () => {
    const tags: Record<string, string> = {};
    for (let index = 0; index < MAX_TAGS + 10; index += 1) {
      tags[`tag-${index}`] = `value-${index}`;
    }

    const event = createEvent({
      level: 'info',
      message: 'boom',
      scope: emptyScope(),
      tags,
    });

    expect(Object.keys(event.tags!)).toHaveLength(MAX_TAGS);
    expect(event.tags!['tag-0']).toBe('value-0');
    expect(event.tags![`tag-${MAX_TAGS - 1}`]).toBe(`value-${MAX_TAGS - 1}`);
    expect(event.tags![`tag-${MAX_TAGS}`]).toBeUndefined();

    const truncated = createEvent({
      level: 'info',
      message: 'boom',
      scope: emptyScope(),
      tags: {
        ['k'.repeat(MAX_TAG_KEY_LENGTH + 5)]: 'v'.repeat(MAX_TAG_VALUE_LENGTH + 5),
      },
    });
    const [truncatedKey] = Object.keys(truncated.tags!);
    expect(truncatedKey).toHaveLength(MAX_TAG_KEY_LENGTH);
    expect(truncated.tags![truncatedKey]).toHaveLength(MAX_TAG_VALUE_LENGTH);
  });

  it('keeps at most 10 fingerprint components and truncates each', () => {
    const fingerprint = Array.from(
      { length: MAX_FINGERPRINT + 5 },
      (_, index) => `part-${index}`,
    );
    fingerprint[0] = 'f'.repeat(MAX_FINGERPRINT_LENGTH + 5);

    const event = createEvent({
      level: 'info',
      message: 'boom',
      scope: emptyScope(),
      fingerprint,
    });

    expect(event.fingerprint).toHaveLength(MAX_FINGERPRINT);
    expect(event.fingerprint![0]).toHaveLength(MAX_FINGERPRINT_LENGTH);
    expect(event.fingerprint![1]).toBe('part-1');
  });

  it('keeps the last 50 breadcrumbs by default', () => {
    const breadcrumbs = Array.from({ length: 80 }, (_, index) => makeBreadcrumb(index));

    const event = createEvent({
      level: 'info',
      message: 'boom',
      scope: emptyScope(),
      breadcrumbs,
    });

    expect(event.breadcrumbs).toHaveLength(DEFAULT_MAX_BREADCRUMBS);
    expect(event.breadcrumbs![0].message).toBe('crumb 30');
    expect(event.breadcrumbs![DEFAULT_MAX_BREADCRUMBS - 1].message).toBe('crumb 79');
  });

  it('honors maxBreadcrumbs and its hard cap', () => {
    const breadcrumbs = Array.from({ length: 150 }, (_, index) => makeBreadcrumb(index));

    const limited = createEvent({
      level: 'info',
      message: 'boom',
      scope: emptyScope(),
      breadcrumbs,
      maxBreadcrumbs: 75,
    });
    expect(limited.breadcrumbs).toHaveLength(75);
    expect(limited.breadcrumbs![0].message).toBe('crumb 75');

    const capped = createEvent({
      level: 'info',
      message: 'boom',
      scope: emptyScope(),
      breadcrumbs,
      maxBreadcrumbs: 1000,
    });
    expect(capped.breadcrumbs).toHaveLength(MAX_BREADCRUMBS);
    expect(capped.breadcrumbs![0].message).toBe('crumb 50');
  });

  it('falls back to scope breadcrumbs when capture breadcrumbs are omitted', () => {
    const scope = new Scope();
    scope.addBreadcrumb({ message: 'scope crumb' });

    const event = createEvent({ level: 'info', message: 'boom', scope: scope.snapshot() });
    expect(event.breadcrumbs).toHaveLength(1);
    expect(event.breadcrumbs![0].message).toBe('scope crumb');
  });

  it('merges contexts with capture precedence', () => {
    const scope = new Scope({
      contexts: { shared: { from: 'scope' }, scopeOnly: { value: 1 } },
    }).snapshot();

    const event = createEvent({
      level: 'info',
      message: 'boom',
      scope,
      contexts: { shared: { from: 'capture' }, captureOnly: { value: 2 } },
    });

    expect(event.contexts).toEqual({
      shared: { from: 'capture' },
      scopeOnly: { value: 1 },
      captureOnly: { value: 2 },
    });
    expect(Object.keys(event.contexts!)).toEqual(['shared', 'scopeOnly', 'captureOnly']);
  });

  it('prefers the capture user and falls back to the scope user', () => {
    const scope = new Scope({ user: { id: 'scope-user' } }).snapshot();

    const fallback = createEvent({ level: 'info', message: 'boom', scope });
    expect(fallback.user).toEqual({ id: 'scope-user' });

    const override = createEvent({
      level: 'info',
      message: 'boom',
      scope,
      user: { id: 'capture-user' },
    });
    expect(override.user).toEqual({ id: 'capture-user' });
  });

  it('omits empty collections and undefined optional fields', () => {
    const event = createEvent({
      level: 'info',
      message: 'boom',
      scope: emptyScope(),
      tags: {},
      contexts: {},
      extra: {},
      breadcrumbs: [],
      fingerprint: [],
      exceptions: [],
    });

    expect(event).not.toHaveProperty('logger');
    expect(event).not.toHaveProperty('release');
    expect(event).not.toHaveProperty('environment');
    expect(event).not.toHaveProperty('server_name');
    expect(event).not.toHaveProperty('exception');
    expect(event).not.toHaveProperty('tags');
    expect(event).not.toHaveProperty('user');
    expect(event).not.toHaveProperty('contexts');
    expect(event).not.toHaveProperty('breadcrumbs');
    expect(event).not.toHaveProperty('fingerprint');
    expect(event).not.toHaveProperty('extra');
  });

  it('drops context and extra keys from the end to stay within 64 KiB', () => {
    const huge = 'x'.repeat(MAX_CONTEXTS_BYTES);

    const event = createEvent({
      level: 'info',
      message: 'boom',
      scope: emptyScope(),
      contexts: { small: { ok: true }, huge },
      extra: { keep: 1, drop: 'y'.repeat(MAX_EXTRA_BYTES) },
    });

    expect(event.contexts).toEqual({ small: { ok: true } });
    expect(event.extra).toEqual({ keep: 1 });
    expect(JSON.stringify(event.contexts)!.length).toBeLessThanOrEqual(MAX_CONTEXTS_BYTES);
    expect(JSON.stringify(event.extra)!.length).toBeLessThanOrEqual(MAX_EXTRA_BYTES);
  });

  it('never throws for cyclic contexts or extra', () => {
    const cyclic: Record<string, unknown> = { name: 'cycle' };
    cyclic['self'] = cyclic;

    let event: ReturnType<typeof createEvent> | undefined;
    expect(() => {
      event = createEvent({
        level: 'info',
        message: 'boom',
        scope: emptyScope(),
        contexts: { safe: { ok: true }, cyclic },
        extra: { cyclic },
      });
    }).not.toThrow();

    expect(event!.contexts).toEqual({ safe: { ok: true } });
    expect(event!.extra).toBeUndefined();
  });

  it('does not apply sampleRate or beforeSend', () => {
    const beforeSend = vi.fn(() => null);
    const input = {
      level: 'info' as Level,
      message: 'still built',
      scope: emptyScope(),
      sampleRate: 0,
      beforeSend,
    } as unknown as CreateEventInput;

    const event = createEvent(input);
    expect(event.message).toBe('still built');
    expect(beforeSend).not.toHaveBeenCalled();
  });
});
