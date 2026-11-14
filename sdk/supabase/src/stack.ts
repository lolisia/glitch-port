/**
 * V8/Deno stack trace parsing and `Error.cause` chain building.
 *
 * The GlitchPort protocol orders exception frames from the outermost caller to
 * the throw site (the reverse of the V8 stack order) and exception items from
 * the root cause to the top-level error. This module converts the runtime stack
 * string into that shape and follows `Error.cause` without ever throwing, so a
 * malformed cause chain can never lose an error report.
 *
 * Runtime code uses only standard JavaScript: the SDK targets Deno/Supabase
 * Edge Functions and must not depend on Node built-ins.
 */

import { MAX_EXCEPTIONS, MAX_FRAMES } from './limits.ts';
import type { GlitchPortException, GlitchPortStackFrame } from './types.ts';

/** Matches a V8 frame line and captures everything after the leading `at`. */
const FRAME_LINE = /^\s*at\s+(.+?)\s*$/;

/** Matches a parenthesized location: `function (location)`. */
const PARENTHESIZED_FRAME = /^(.*?)\((.*)\)$/;

/** Matches a trailing `:line:col` (greedy prefix keeps colons in the path). */
const LOCATION_WITH_COLUMN = /^(.*):(\d+):(\d+)$/;

/** Matches a trailing `:line` (used when the column is missing). */
const LOCATION_WITH_LINE = /^(.*):(\d+)$/;

/** Strips the `async`/`new` markers V8 puts in front of a function name. */
const FRAME_MODIFIER = /^(?:async|new)\s+/;

/** A Windows drive prefix such as `C:/` or `C:\`. */
const WINDOWS_DRIVE = /^[A-Za-z]:[\\/]/;

/** Path fragments that always mean "third-party/dependency, not in-app". */
const THIRD_PARTY_MARKERS = ['node_modules', 'deno.land', 'esm.sh', 'jsr.io', 'jsr:'];

/** Remove the `async`/`new` prefix from a V8 function name. */
function stripFrameModifier(name: string): string {
  return name.replace(FRAME_MODIFIER, '');
}

/**
 * Whether a bare (non-parenthesized) frame text looks like a location.
 *
 * Anything starting with `at` is treated as a frame, but lines such as
 * `at something went wrong` are not locations and must be ignored instead of
 * being reported as a file named "something went wrong".
 */
function looksLikeLocation(text: string): boolean {
  if (text.length === 0) {
    return false;
  }
  if (text === 'native' || text === 'unknown location' || text === '<anonymous>') {
    return true;
  }
  if (text.includes('/') || text.includes('\\')) {
    return true;
  }
  if (WINDOWS_DRIVE.test(text)) {
    return true;
  }
  if (/^(?:node|data|wasm|blob):/.test(text)) {
    return true;
  }
  return /:\d+(?::\d+)?$/.test(text);
}

/**
 * Split a location into the file name as written and its integer line/column.
 *
 * V8 renders locations as `file:line:col`; a location without a trailing
 * `:line:col` (for example `native`) keeps the whole string as the file name
 * and reports line and column 0.
 */
function splitLocation(location: string): {
  filename: string;
  lineno: number;
  colno: number;
} {
  const withColumn = LOCATION_WITH_COLUMN.exec(location);
  if (withColumn) {
    return {
      filename: withColumn[1],
      lineno: Number.parseInt(withColumn[2], 10),
      colno: Number.parseInt(withColumn[3], 10),
    };
  }

  const withLine = LOCATION_WITH_LINE.exec(location);
  if (withLine) {
    return {
      filename: withLine[1],
      lineno: Number.parseInt(withLine[2], 10),
      colno: 0,
    };
  }

  return { filename: location, lineno: 0, colno: 0 };
}

/**
 * Decode the path of a `file://` URL and drop the scheme.
 *
 * `file:///home/deno/functions/a%20b.ts` becomes
 * `/home/deno/functions/a b.ts`, and the Windows form `file:///C:/x.ts`
 * becomes `C:/x.ts`. Non-file locations never get an absolute path.
 */
function deriveAbsPath(filename: string): string | undefined {
  if (!filename.startsWith('file://')) {
    return undefined;
  }

  let rawPath: string;
  try {
    rawPath = new URL(filename).pathname;
  } catch {
    // Fall back to plain prefix removal when the URL is not parseable.
    rawPath = filename.slice('file://'.length);
  }

  let decodedPath: string;
  try {
    decodedPath = decodeURIComponent(rawPath);
  } catch {
    // Keep the encoded path when it contains invalid percent-encoding.
    decodedPath = rawPath;
  }

  return decodedPath.replace(/^\/([A-Za-z]:[\\/])/, '$1');
}

/** Whether a path points at a local source file rather than a remote module. */
function isLocalPath(path: string): boolean {
  return (
    path.startsWith('file://') ||
    path.startsWith('/') ||
    path.startsWith('./') ||
    path.startsWith('../') ||
    path.startsWith('.\\') ||
    path.startsWith('..\\') ||
    path.startsWith('\\\\') ||
    WINDOWS_DRIVE.test(path)
  );
}

/**
 * Decide whether a frame belongs to the application.
 *
 * With an explicit `inAppInclude`, a frame is in-app when any non-empty entry
 * is contained in its file name or decoded absolute path (this fully overrides
 * the default rule). Otherwise frames in a `/functions/` directory and local
 * files are in-app unless they come from a dependency location.
 */
function computeInApp(
  filename: string,
  absPath: string | undefined,
  inAppInclude: string[] | undefined,
): boolean {
  if (inAppInclude !== undefined) {
    return inAppInclude.some(
      (entry) =>
        entry.length > 0 &&
        (filename.includes(entry) || (absPath !== undefined && absPath.includes(entry))),
    );
  }

  const normalizedPath = absPath ?? filename;
  const lowerPath = normalizedPath.toLowerCase();
  if (THIRD_PARTY_MARKERS.some((marker) => lowerPath.includes(marker))) {
    return false;
  }
  if (lowerPath.includes('/functions/')) {
    return true;
  }
  return isLocalPath(normalizedPath);
}

/** Parse one V8 stack line into a protocol frame, or ignore it. */
function parseFrame(
  line: string,
  inAppInclude: string[] | undefined,
): GlitchPortStackFrame | undefined {
  const lineMatch = FRAME_LINE.exec(line);
  if (!lineMatch) {
    return undefined;
  }

  const frameText = lineMatch[1].trim();
  const parenthesized = PARENTHESIZED_FRAME.exec(frameText);

  let functionName: string;
  let location: string;

  if (parenthesized) {
    functionName = stripFrameModifier(parenthesized[1].trim());
    location = parenthesized[2].trim();
    if (location.length === 0) {
      return undefined;
    }
  } else {
    // A bare location (no function parenthesis) is an anonymous frame.
    location = stripFrameModifier(frameText);
    if (!looksLikeLocation(location)) {
      return undefined;
    }
    functionName = '';
  }

  if (functionName.length === 0) {
    functionName = '<anonymous>';
  }

  const { filename, lineno, colno } = splitLocation(location);
  const absPath = deriveAbsPath(filename);

  const frame: GlitchPortStackFrame = {
    function: functionName,
    filename,
    lineno,
    colno,
    in_app: computeInApp(filename, absPath, inAppInclude),
  };

  const lastDot = functionName.lastIndexOf('.');
  if (lastDot > 0) {
    frame.module = functionName.slice(0, lastDot);
  }
  if (absPath !== undefined) {
    frame.abs_path = absPath;
  }

  return frame;
}

/**
 * Parse a V8/Deno stack trace into protocol frames.
 *
 * V8 lists the throw site first; the returned array is reversed so the
 * outermost caller comes first and the throw site last. When a trace has more
 * than {@link MAX_FRAMES} frames the extra outermost frames are dropped, which
 * keeps the throw-site side of the trace.
 */
export function parseStack(
  stack: string | undefined,
  inAppInclude?: string[],
): GlitchPortStackFrame[] {
  if (typeof stack !== 'string' || stack.length === 0) {
    return [];
  }

  const frames: GlitchPortStackFrame[] = [];
  for (const line of stack.split('\n')) {
    const frame = parseFrame(line, inAppInclude);
    if (frame !== undefined) {
      frames.push(frame);
    }
  }

  frames.reverse();
  return frames.length > MAX_FRAMES ? frames.slice(-MAX_FRAMES) : frames;
}

/** Whether a value can be tracked in the cause-chain visited set. */
function isReference(value: unknown): value is object {
  return (typeof value === 'object' && value !== null) || typeof value === 'function';
}

/**
 * Read `error.cause` without letting an exotic getter abort the chain walk.
 *
 * A throwing accessor is treated as a missing cause so an unusual error object
 * still produces a report for the error itself.
 */
function readCause(error: Error): unknown {
  try {
    return error.cause;
  } catch {
    return undefined;
  }
}

/** Convert an arbitrary value to a string without ever throwing. */
function stringifyValue(value: unknown): string {
  try {
    return String(value);
  } catch {
    try {
      return Object.prototype.toString.call(value);
    } catch {
      return 'Unknown error';
    }
  }
}

/**
 * Resolve the protocol exception type for an Error.
 *
 * `error.name` is used as-is, except for the base `Error` name where the
 * concrete constructor name is more informative (`ValidationError` rather than
 * `Error`).
 */
function exceptionType(error: Error): string {
  const name = typeof error.name === 'string' ? error.name : '';
  if (name.length > 0 && name !== 'Error') {
    return name;
  }

  const constructorName = (error.constructor as { name?: unknown } | undefined)?.name;
  if (typeof constructorName === 'string' && constructorName.length > 0) {
    return constructorName;
  }

  return 'Error';
}

/** Build a protocol exception item for one value in the cause chain. */
function toException(value: unknown, inAppInclude: string[] | undefined): GlitchPortException {
  if (value instanceof Error) {
    const message = value.message;
    return {
      type: exceptionType(value),
      value: typeof message === 'string' ? message : stringifyValue(message),
      stacktrace: { frames: parseStack(value.stack, inAppInclude) },
    };
  }

  return {
    type: 'Error',
    value: stringifyValue(value),
    stacktrace: { frames: [] },
  };
}

/**
 * Build the protocol exception array for a captured value.
 *
 * The returned array follows `Error.cause` (using a visited set so cyclic
 * chains terminate) and is ordered root cause first, top-level error last. It
 * is capped at {@link MAX_EXCEPTIONS} by keeping the first items in that
 * root-first order. Non-Error values become `type: "Error"` items with
 * `String(value)` and no frames; `undefined`, `null` and cyclic causes are
 * handled without throwing.
 */
export function buildExceptionChain(
  error: unknown,
  inAppInclude?: string[],
): GlitchPortException[] {
  const chain: GlitchPortException[] = [];
  const visited = new Set<unknown>();
  let current: unknown = error;

  while (true) {
    if (isReference(current)) {
      if (visited.has(current)) {
        break;
      }
      visited.add(current);
    }

    chain.push(toException(current, inAppInclude));

    if (!(current instanceof Error)) {
      break;
    }

    const cause = readCause(current);
    // A missing (or null) cause has no further chain.
    if (cause === undefined || cause === null) {
      break;
    }
    current = cause;
  }

  chain.reverse();
  return chain.slice(0, MAX_EXCEPTIONS);
}
