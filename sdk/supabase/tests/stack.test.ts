import { describe, expect, it } from 'vitest';
import { MAX_EXCEPTIONS, MAX_FRAMES } from '../src/limits.ts';
import { buildExceptionChain, parseStack } from '../src/stack.ts';

/**
 * Realistic Deno/V8 stacks. Deno prints `file://` URLs and V8 lists the throw
 * site first, so the parser has to reverse the frames.
 */
const NAMED_DENO_STACK = [
  'TypeError: Cannot read properties of undefined (reading "name")',
  '    at loadUser (file:///home/deno/functions/hello/index.ts:12:9)',
  '    at handler (file:///home/deno/functions/hello/index.ts:20:3)',
  '    at async file:///home/deno/functions/hello/index.ts:25:1',
].join('\n');

const ANONYMOUS_DENO_STACK = [
  'Error: boom',
  '    at file:///home/deno/functions/hello/index.ts:5:3',
  '    at Object.<anonymous> (file:///home/deno/functions/hello/index.ts:9:1)',
  '    at Module._evaluate (node:internal/modules/esm/module_job:263:25)',
].join('\n');

const ASYNC_DENO_STACK = [
  'Error: rejected',
  '    at async loadUser (file:///home/deno/functions/users.ts:42:11)',
  '    at async file:///home/deno/functions/index.ts:8:1',
].join('\n');

describe('parseStack', () => {
  it('parses a named Deno stack with the caller first and the throw site last', () => {
    const frames = parseStack(NAMED_DENO_STACK);

    expect(frames).toEqual([
      {
        function: '<anonymous>',
        filename: 'file:///home/deno/functions/hello/index.ts',
        abs_path: '/home/deno/functions/hello/index.ts',
        lineno: 25,
        colno: 1,
        in_app: true,
      },
      {
        function: 'handler',
        filename: 'file:///home/deno/functions/hello/index.ts',
        abs_path: '/home/deno/functions/hello/index.ts',
        lineno: 20,
        colno: 3,
        in_app: true,
      },
      {
        function: 'loadUser',
        filename: 'file:///home/deno/functions/hello/index.ts',
        abs_path: '/home/deno/functions/hello/index.ts',
        lineno: 12,
        colno: 9,
        in_app: true,
      },
    ]);
  });

  it('parses an anonymous Deno stack and keeps dotted names with their module', () => {
    const frames = parseStack(ANONYMOUS_DENO_STACK);

    expect(frames.map((frame) => frame.function)).toEqual([
      'Module._evaluate',
      'Object.<anonymous>',
      '<anonymous>',
    ]);

    const [moduleFrame, objectFrame, bareFrame] = frames;
    expect(moduleFrame.module).toBe('Module');
    expect(moduleFrame.filename).toBe('node:internal/modules/esm/module_job');
    expect(moduleFrame.abs_path).toBeUndefined();
    expect(moduleFrame.in_app).toBe(false);

    expect(objectFrame.module).toBe('Object');
    expect(objectFrame.filename).toBe('file:///home/deno/functions/hello/index.ts');
    expect(objectFrame.in_app).toBe(true);

    expect(bareFrame.function).toBe('<anonymous>');
    expect(bareFrame.module).toBeUndefined();
    expect(bareFrame).not.toHaveProperty('module');
  });

  it('parses async Deno frames and puts the throwing async frame last', () => {
    const frames = parseStack(ASYNC_DENO_STACK);

    expect(frames.map((frame) => frame.function)).toEqual(['<anonymous>', 'loadUser']);
    expect(frames[0].filename).toBe('file:///home/deno/functions/index.ts');
    expect(frames[0].lineno).toBe(8);
    expect(frames[0].colno).toBe(1);
    expect(frames[1].filename).toBe('file:///home/deno/functions/users.ts');
    expect(frames[1].lineno).toBe(42);
    expect(frames[1].colno).toBe(11);
  });

  it('parses CRLF stack lines from Windows tooling', () => {
    const frames = parseStack(
      [
        'Error: boom',
        '    at handler (file:///C:/app/functions/hello/index.ts:12:9)',
        '    at main (file:///C:/app/index.ts:3:1)',
      ].join('\r\n'),
    );

    expect(frames.map((frame) => frame.function)).toEqual(['main', 'handler']);
    expect(frames[1].filename).toBe('file:///C:/app/functions/hello/index.ts');
    expect(frames[1].abs_path).toBe('C:/app/functions/hello/index.ts');
    expect(frames[1].lineno).toBe(12);
    expect(frames[1].colno).toBe(9);
  });

  it('strips new and keeps multi-level dotted function names', () => {
    const frames = parseStack(
      [
        'Error: boom',
        '    at new Foo (file:///home/deno/functions/foo.ts:3:5)',
        '    at Object.foo (file:///home/deno/functions/a.ts:1:2)',
        '    at A.B.c (file:///home/deno/functions/b.ts:3:4)',
      ].join('\n'),
    );

    expect(frames.map((frame) => frame.function)).toEqual(['A.B.c', 'Object.foo', 'Foo']);
    expect(frames[0].module).toBe('A.B');
    expect(frames[1].module).toBe('Object');
    expect(frames[2].module).toBeUndefined();
    expect(frames[2]).not.toHaveProperty('module');
  });

  it('decodes file URLs into abs_path while keeping the filename as written', () => {
    const frames = parseStack(
      [
        'Error: boom',
        '    at handler (file:///home/deno/functions/my%20handler/index.ts:12:9)',
        '    at handler (file:///C:/Users/deno/functions/hello/index.ts:1:2)',
      ].join('\n'),
    );

    const [windowsFrame, encodedFrame] = frames;
    expect(encodedFrame.filename).toBe('file:///home/deno/functions/my%20handler/index.ts');
    expect(encodedFrame.abs_path).toBe('/home/deno/functions/my handler/index.ts');
    expect(encodedFrame.lineno).toBe(12);
    expect(encodedFrame.colno).toBe(9);

    expect(windowsFrame.filename).toBe('file:///C:/Users/deno/functions/hello/index.ts');
    expect(windowsFrame.abs_path).toBe('C:/Users/deno/functions/hello/index.ts');
  });

  it('defaults lineno and colno to 0 when the location has no line and column', () => {
    const frames = parseStack(
      [
        'Error: boom',
        '    at NativeMachine.execute (native)',
        '    at file:///home/deno/functions/hello/index.ts',
      ].join('\n'),
    );

    expect(frames).toHaveLength(2);

    const [bareFrame, nativeFrame] = frames;
    expect(bareFrame.function).toBe('<anonymous>');
    expect(bareFrame.filename).toBe('file:///home/deno/functions/hello/index.ts');
    expect(bareFrame.lineno).toBe(0);
    expect(bareFrame.colno).toBe(0);

    expect(nativeFrame.function).toBe('NativeMachine.execute');
    expect(nativeFrame.filename).toBe('native');
    expect(nativeFrame.lineno).toBe(0);
    expect(nativeFrame.colno).toBe(0);
    expect(nativeFrame.abs_path).toBeUndefined();
  });

  it('uses the default in_app rule with /functions/ and local files but excludes dependencies', () => {
    const frames = parseStack(
      [
        'Error: boom',
        '    at functionsFrame (file:///home/deno/functions/hello/index.ts:1:1)',
        '    at localFrame (file:///home/deno/app/index.ts:2:2)',
        '    at windowsFrame (file:///C:/app/index.ts:3:3)',
        '    at posixFrame (/srv/app/index.ts:4:4)',
        '    at functionsUrl (https://example.com/functions/hello/index.ts:5:5)',
        '    at denoLand (https://deno.land/x/mod.ts:6:6)',
        '    at esmSh (https://esm.sh/pkg@1/mod.ts:7:7)',
        '    at jsrIo (https://jsr.io/@scope/pkg/mod.ts:8:8)',
        '    at jsrSpecifier (jsr:@scope/pkg@1.0.0/mod.ts:9:9)',
        '    at nodeModules (file:///app/node_modules/pkg/index.js:10:10)',
        '    at remote (https://example.com/api/index.ts:11:11)',
      ].join('\n'),
    );

    const inAppByFunction = Object.fromEntries(
      frames.map((frame) => [frame.function, frame.in_app]),
    );

    expect(inAppByFunction).toEqual({
      functionsFrame: true,
      localFrame: true,
      windowsFrame: true,
      posixFrame: true,
      functionsUrl: true,
      denoLand: false,
      esmSh: false,
      jsrIo: false,
      jsrSpecifier: false,
      nodeModules: false,
      remote: false,
    });
  });

  it('uses inAppInclude instead of the default rule when provided', () => {
    const stack = [
      'Error: boom',
      '    at functionsFrame (file:///home/deno/functions/hello/index.ts:1:1)',
      '    at denoLand (https://deno.land/x/mod.ts:2:2)',
      '    at sourceFrame (file:///home/deno/app/src/foo.ts:3:3)',
      '    at decodedFrame (file:///home/deno/functions/my%20handler/index.ts:4:4)',
    ].join('\n');

    const frames = parseStack(stack, ['deno.land', 'src/', 'my handler']);
    const inAppByFunction = Object.fromEntries(
      frames.map((frame) => [frame.function, frame.in_app]),
    );

    expect(inAppByFunction).toEqual({
      functionsFrame: false,
      denoLand: true,
      sourceFrame: true,
      decodedFrame: true,
    });

    const ignoredEmptyEntry = parseStack(stack, ['']);
    expect(ignoredEmptyEntry.every((frame) => frame.in_app === false)).toBe(true);

    const emptyInclude = parseStack(stack, []);
    expect(emptyInclude.every((frame) => frame.in_app === false)).toBe(true);
  });

  it(`keeps the last ${MAX_FRAMES} frames on the throw-site side`, () => {
    const lines = ['Error: deep'];
    for (let index = 0; index < 300; index += 1) {
      lines.push(
        `    at frame${index} (file:///home/deno/functions/deep/index.ts:${index + 1}:1)`,
      );
    }

    const frames = parseStack(lines.join('\n'));

    expect(frames).toHaveLength(MAX_FRAMES);
    expect(frames[0].function).toBe('frame249');
    expect(frames[0].lineno).toBe(250);
    expect(frames[frames.length - 1].function).toBe('frame0');
    expect(frames[frames.length - 1].lineno).toBe(1);
    expect(frames.every((frame) => frame.in_app)).toBe(true);
  });

  it('returns no frames for empty or malformed stacks', () => {
    expect(parseStack(undefined)).toEqual([]);
    expect(parseStack('')).toEqual([]);
    expect(parseStack('   \n\t\n')).toEqual([]);
    expect(parseStack('TypeError: boom')).toEqual([]);
    expect(
      parseStack(
        [
          'TypeError: boom',
          'the error message is not a frame',
          '    at',
          '    at ',
          '    at ()',
          '    at something went wrong',
          '    not at anything',
        ].join('\n'),
      ),
    ).toEqual([]);
  });
});

describe('buildExceptionChain', () => {
  it('returns a three-deep cause chain root first and the top-level error last', () => {
    const root = new TypeError('root failure');
    const middle = new Error('middle failure', { cause: root });
    const outer = new RangeError('outer failure', { cause: middle });

    const chain = buildExceptionChain(outer);

    expect(chain).toHaveLength(3);
    expect(chain.map((item) => item.value)).toEqual([
      'root failure',
      'middle failure',
      'outer failure',
    ]);
    expect(chain.map((item) => item.type)).toEqual(['TypeError', 'Error', 'RangeError']);
    expect(chain[0]).not.toHaveProperty('module');
    expect(chain[0].stacktrace.frames).toEqual(parseStack(root.stack));
    expect(chain[2].stacktrace.frames).toEqual(parseStack(outer.stack));
  });

  it('uses error.name or the constructor name for the exception type', () => {
    class ValidationError extends Error {}

    expect(buildExceptionChain(new ValidationError('invalid'))[0].type).toBe('ValidationError');

    const named = new Error('named');
    named.name = 'CustomError';
    expect(buildExceptionChain(named)[0].type).toBe('CustomError');

    expect(buildExceptionChain(new Error('plain'))[0].type).toBe('Error');
  });

  it('passes inAppInclude through to every stack trace in the chain', () => {
    const root = new Error('root');
    root.stack = [
      'Error: root',
      '    at rootFn (file:///home/deno/functions/root.ts:1:1)',
      '    at rootLib (file:///home/deno/lib/root.ts:2:2)',
    ].join('\n');

    const outer = new Error('outer', { cause: root });
    outer.stack = [
      'Error: outer',
      '    at outerFn (file:///home/deno/app/outer.ts:3:3)',
    ].join('\n');

    const chain = buildExceptionChain(outer, ['/app/']);

    expect(chain[0].stacktrace.frames.map((frame) => frame.function)).toEqual([
      'rootLib',
      'rootFn',
    ]);
    expect(chain[0].stacktrace.frames.map((frame) => frame.in_app)).toEqual([false, false]);
    expect(chain[1].stacktrace.frames.map((frame) => frame.function)).toEqual(['outerFn']);
    expect(chain[1].stacktrace.frames.map((frame) => frame.in_app)).toEqual([true]);
  });

  it('turns a non-Error captured value into a single frame-less Error item', () => {
    expect(buildExceptionChain('boom')).toEqual([
      { type: 'Error', value: 'boom', stacktrace: { frames: [] } },
    ]);
    expect(buildExceptionChain(42)).toEqual([
      { type: 'Error', value: '42', stacktrace: { frames: [] } },
    ]);

    const [objectItem] = buildExceptionChain({ code: 'E_FAIL' });
    expect(objectItem.type).toBe('Error');
    expect(objectItem.value).toBe('[object Object]');
    expect(objectItem.stacktrace.frames).toEqual([]);
    expect(objectItem).not.toHaveProperty('module');
  });

  it('includes a non-Error cause as an Error item with String(value)', () => {
    const chain = buildExceptionChain(new Error('outer failure', { cause: 'database unavailable' }));

    expect(chain.map((item) => item.value)).toEqual(['database unavailable', 'outer failure']);
    expect(chain[0]).toEqual({
      type: 'Error',
      value: 'database unavailable',
      stacktrace: { frames: [] },
    });
    expect(chain[1].type).toBe('Error');
  });

  it('includes falsy non-Error causes instead of treating them as absent', () => {
    const expectChain = (cause: unknown, rootValue: string) => {
      const chain = buildExceptionChain(new Error('outer', { cause }));
      expect(chain.map((item) => item.value)).toEqual([rootValue, 'outer']);
      expect(chain[0].stacktrace.frames).toEqual([]);
    };

    expectChain(0, '0');
    expectChain('', '');
    expectChain(false, 'false');
  });

  it('stops at a cause accessor that throws instead of failing the report', () => {
    const error = new Error('outer');
    Object.defineProperty(error, 'cause', {
      configurable: true,
      get() {
        throw new Error('cause getter failed');
      },
    });

    expect(() => buildExceptionChain(error)).not.toThrow();
    expect(buildExceptionChain(error).map((item) => item.value)).toEqual(['outer']);
  });

  it(`keeps the first ${MAX_EXCEPTIONS} items in root-first order`, () => {
    let current: Error = new Error('cause0');
    for (let index = 1; index < 12; index += 1) {
      current = new Error(`cause${index}`, { cause: current });
    }

    const chain = buildExceptionChain(current);

    expect(chain).toHaveLength(MAX_EXCEPTIONS);
    expect(chain.map((item) => item.value)).toEqual([
      'cause0',
      'cause1',
      'cause2',
      'cause3',
      'cause4',
      'cause5',
      'cause6',
      'cause7',
      'cause8',
      'cause9',
    ]);
  });

  it('terminates on cyclic cause chains without throwing', () => {
    const first = new Error('first');
    const second = new Error('second', { cause: first });
    (first as Error & { cause?: unknown }).cause = second;

    expect(() => buildExceptionChain(first)).not.toThrow();
    expect(buildExceptionChain(first).map((item) => item.value)).toEqual(['second', 'first']);
  });

  it('handles undefined and null causes without throwing', () => {
    const undefinedCause = new Error('undefined cause', { cause: undefined });
    expect(() => buildExceptionChain(undefinedCause)).not.toThrow();
    expect(buildExceptionChain(undefinedCause).at(-1)?.value).toBe('undefined cause');

    const nullCause = new Error('null cause', { cause: null });
    expect(() => buildExceptionChain(nullCause)).not.toThrow();
    expect(buildExceptionChain(nullCause).at(-1)?.value).toBe('null cause');

    expect(buildExceptionChain(undefined).map((item) => item.value)).toEqual(['undefined']);
    expect(buildExceptionChain(null).map((item) => item.value)).toEqual(['null']);
  });

  it('builds an empty frame list when the error has no parseable stack', () => {
    const error = new Error('stackless');
    error.stack = undefined;

    expect(buildExceptionChain(error)).toEqual([
      { type: 'Error', value: 'stackless', stacktrace: { frames: [] } },
    ]);

    error.stack = 'not a stack trace';
    expect(buildExceptionChain(error)[0].stacktrace.frames).toEqual([]);
  });
});
