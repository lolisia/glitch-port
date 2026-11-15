/**
 * End-to-end check of the glitch-port Supabase SDK against a running collector.
 *
 * `scripts/e2e-supabase.sh` starts PostgreSQL, the Processor and the Relay and
 * then runs this file with Node.js 22:
 *
 *     node --experimental-strip-types sdk/supabase/examples/node-e2e.ts
 *
 * The example imports the SDK source with the explicit `.ts` specifier Deno
 * uses, so no npm install or build step is involved. It has no Node imports and
 * reaches the environment through a narrow `globalThis` cast, which keeps it
 * typechecking under the SDK's `types: []` tsconfig.
 *
 * It initializes the SDK, invokes a `withGlitchPort` handler with `?fail=1`
 * (the handler throws on purpose) and verifies:
 *
 *   1. the automatic 500 response has a JSON content-type and a body with
 *      `error: "Internal Server Error"` and a 32-character lowercase
 *      hexadecimal `event_id`;
 *   2. the stored event (`GET /api/manage/events/{eventId}`, dashed UUID form)
 *      has platform `javascript` and `payload.exception[0].type === "Error"`;
 *   3. the generated issue (`GET /api/manage/projects/{projectId}/issues`) has
 *      platform `javascript`, a title containing `Error: intentional failure
 *      from supabase edge function` and `event_count >= 1`.
 *
 * Environment variables:
 *
 * - `GLITCHPORT_DSN` (required) — for example
 *   `http://dev-public-key@localhost:5080/1`.
 * - `GLITCHPORT_MANAGE_URL` — Management API base URL, default
 *   `http://localhost:5090/api/manage`.
 * - `GLITCHPORT_ADMIN_TOKEN` — Management API bearer token, default
 *   `dev-admin-token`.
 * - `GLITCHPORT_PROJECT_ID` — overrides the project id taken from the last
 *   non-empty DSN path segment.
 *
 * The script prints `node-e2e: ok` and exits 0 on success; on any failure it
 * prints a readable diagnostic and exits non-zero.
 */

import * as GlitchPort from '../src/glitchport.ts';

/** The subset of Node's `process` the example needs; Node types are absent. */
interface NodeProcessLike {
  env?: Record<string, string | undefined>;
  exitCode?: number;
}

/** Node's `process` when running on Node, `undefined` on other runtimes. */
const nodeProcess = (globalThis as unknown as { process?: NodeProcessLike })
  .process;

const DEFAULT_MANAGE_URL = 'http://localhost:5090/api/manage';
const DEFAULT_ADMIN_TOKEN = 'dev-admin-token';
const FAILURE_MESSAGE = 'intentional failure from supabase edge function';
const ISSUE_TITLE = `Error: ${FAILURE_MESSAGE}`;
const EVENT_ID_PATTERN = /^[0-9a-f]{32}$/;
const POLL_DEADLINE_MS = 15_000;
const POLL_INTERVAL_MS = 250;

/** Read one environment variable, treating an empty string as unset. */
function readEnv(name: string): string | undefined {
  const value = nodeProcess?.env?.[name];
  return value === undefined || value.length === 0 ? undefined : value;
}

function delay(ms: number): Promise<void> {
  return new Promise<void>((resolve) => {
    setTimeout(() => {
      resolve();
    }, ms);
  });
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function errorMessage(error: unknown): string {
  if (error instanceof Error) {
    return error.message;
  }
  try {
    return String(error);
  } catch {
    return 'unknown error';
  }
}

/** Serialize a value for a diagnostic without ever throwing. */
function formatJson(value: unknown): string {
  try {
    return JSON.stringify(value) ?? String(value);
  } catch {
    return '<unserializable value>';
  }
}

/** Project id: the last non-empty segment of the DSN path. */
function projectIdFromDsn(dsn: string): string {
  let url: URL;
  try {
    url = new URL(dsn);
  } catch {
    throw new Error(`GLITCHPORT_DSN is not a valid URL: ${dsn}`);
  }

  const segments = url.pathname
    .split('/')
    .filter((segment) => segment.length > 0);
  const projectId = segments[segments.length - 1];
  if (projectId === undefined) {
    throw new Error(
      `GLITCHPORT_DSN has no project id in its path: ${dsn}. ` +
        'Use https://<publicKey>@<host>/<projectId>.',
    );
  }
  return projectId;
}

/** Convert the SDK's 32-character event id to the dashed UUID form. */
function dashedEventId(eventId: string): string {
  return [
    eventId.slice(0, 8),
    eventId.slice(8, 12),
    eventId.slice(12, 16),
    eventId.slice(16, 20),
    eventId.slice(20),
  ].join('-');
}

interface JsonResponse {
  status: number;
  body: unknown;
}

/** GET a Management API resource and decode its JSON body when possible. */
async function getJson(
  url: string,
  adminToken: string,
): Promise<JsonResponse> {
  const response = await fetch(url, {
    headers: { Authorization: `Bearer ${adminToken}` },
  });

  let body: unknown;
  try {
    body = (await response.json()) as unknown;
  } catch {
    body = undefined;
  }

  return { status: response.status, body };
}

/**
 * Poll a Management API resource until it answers 200 and `accept` reports no
 * problem. Transient errors (connection refused, 404 before the event lands)
 * keep polling until the 15 s deadline; a 200 that fails `accept` is an
 * immediate, descriptive failure because the resource can no longer change.
 */
async function pollJson(
  description: string,
  url: string,
  adminToken: string,
  accept: (body: unknown) => string | undefined,
): Promise<unknown> {
  const deadline = Date.now() + POLL_DEADLINE_MS;
  let lastStatus = 0;
  let lastBody: unknown;

  for (;;) {
    let response: JsonResponse | undefined;
    try {
      response = await getJson(url, adminToken);
      lastStatus = response.status;
      lastBody = response.body;
    } catch (error) {
      lastBody = errorMessage(error);
    }

    if (response !== undefined && response.status === 200) {
      const problem = accept(response.body);
      if (problem === undefined) {
        return response.body;
      }
      throw new Error(
        `${description} returned an unexpected resource: ${problem}. ` +
          `Body: ${formatJson(response.body)}`,
      );
    }

    if (Date.now() >= deadline) {
      throw new Error(
        `${description} was not readable within ${POLL_DEADLINE_MS / 1000}s. ` +
          `GET ${url} returned ${lastStatus === 0 ? 'no response' : lastStatus}: ` +
          formatJson(lastBody),
      );
    }

    await delay(POLL_INTERVAL_MS);
  }
}

/** Validate an event detail resource returned by the Management API. */
function eventProblem(body: unknown): string | undefined {
  if (!isRecord(body)) {
    return 'the response body is not an object';
  }
  if (body.platform !== 'javascript') {
    return `platform is ${formatJson(body.platform)}, expected "javascript"`;
  }

  const payload = body.payload;
  if (!isRecord(payload)) {
    return 'payload is not an object';
  }
  const exceptions = payload.exception;
  if (!Array.isArray(exceptions) || exceptions.length === 0) {
    return 'payload.exception is missing or empty';
  }
  const first = exceptions[0] as unknown;
  if (!isRecord(first)) {
    return 'payload.exception[0] is not an object';
  }
  if (first.type !== 'Error') {
    return `payload.exception[0].type is ${formatJson(first.type)}, expected "Error"`;
  }
  return undefined;
}

/** Validate the issue list returned by the Management API. */
function issueListProblem(body: unknown): string | undefined {
  if (!isRecord(body)) {
    return 'the response body is not an object';
  }
  const items = body.items;
  if (!Array.isArray(items)) {
    return 'items is not an array';
  }

  const match = items.find(
    (item) =>
      isRecord(item) &&
      item.platform === 'javascript' &&
      typeof item.title === 'string' &&
      item.title.includes(ISSUE_TITLE) &&
      typeof item.event_count === 'number' &&
      item.event_count >= 1,
  );

  return match === undefined
    ? `no issue with platform "javascript", a title containing ` +
        `${formatJson(ISSUE_TITLE)} and event_count >= 1`
    : undefined;
}

/** Read a JSON response body, failing with the raw text when it is invalid. */
async function readJson(response: Response): Promise<unknown> {
  const text = await response.text();
  try {
    return JSON.parse(text) as unknown;
  } catch {
    throw new Error(`the response body is not valid JSON: ${text}`);
  }
}

async function main(): Promise<void> {
  if (nodeProcess === undefined) {
    throw new Error(
      'node-e2e must run on Node.js 22 with --experimental-strip-types',
    );
  }

  const dsn = readEnv('GLITCHPORT_DSN');
  if (dsn === undefined) {
    throw new Error(
      'GLITCHPORT_DSN is required, for example ' +
        "GLITCHPORT_DSN='http://dev-public-key@localhost:5080/1'.",
    );
  }

  const manageUrl = (
    readEnv('GLITCHPORT_MANAGE_URL') ?? DEFAULT_MANAGE_URL
  ).replace(/\/+$/, '');
  const adminToken = readEnv('GLITCHPORT_ADMIN_TOKEN') ?? DEFAULT_ADMIN_TOKEN;
  const projectId = readEnv('GLITCHPORT_PROJECT_ID') ?? projectIdFromDsn(dsn);

  GlitchPort.init({
    dsn,
    environment: 'e2e',
    release: 'supabase-e2e@0.0.0',
    timeoutMs: 5000,
  });

  const handler = GlitchPort.withGlitchPort(async (req: Request) => {
    const url = new URL(req.url);

    if (url.searchParams.get('fail') === '1') {
      throw new Error(FAILURE_MESSAGE);
    }

    return new Response('ok');
  });

  console.log('node-e2e: invoking the wrapped handler with ?fail=1');
  const response = await handler(new Request('http://localhost/fail?fail=1'));

  if (response.status !== 500) {
    throw new Error(
      `expected HTTP 500 from withGlitchPort, got ${response.status}`,
    );
  }

  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.toLowerCase().startsWith('application/json')) {
    throw new Error(
      `expected a JSON content-type, got ${formatJson(contentType)}`,
    );
  }

  const body = await readJson(response);
  if (!isRecord(body)) {
    throw new Error(`expected a JSON object body, got ${formatJson(body)}`);
  }
  if (body.error !== 'Internal Server Error') {
    throw new Error(
      `expected error "Internal Server Error", got ${formatJson(body.error)}`,
    );
  }
  const eventId = body.event_id;
  if (typeof eventId !== 'string' || !EVENT_ID_PATTERN.test(eventId)) {
    throw new Error(
      'expected a 32-character lowercase hexadecimal event_id, got ' +
        formatJson(eventId),
    );
  }
  console.log(`node-e2e: captured event ${eventId} (HTTP 500)`);

  const eventUrl = `${manageUrl}/events/${dashedEventId(eventId)}`;
  await pollJson(
    `event ${dashedEventId(eventId)}`,
    eventUrl,
    adminToken,
    eventProblem,
  );
  console.log(
    'node-e2e: the stored event has platform "javascript" and exception type "Error"',
  );

  const issuesUrl =
    `${manageUrl}/projects/${encodeURIComponent(projectId)}/issues` +
    '?status=all&limit=100';
  await pollJson(
    `the issue list of project ${projectId}`,
    issuesUrl,
    adminToken,
    issueListProblem,
  );
  console.log(`node-e2e: the generated issue contains "${ISSUE_TITLE}"`);

  console.log('node-e2e: ok');
}

main().catch((error: unknown) => {
  console.error(`node-e2e: ${errorMessage(error)}`);
  if (nodeProcess === undefined) {
    // Re-throw on runtimes without `process` so the failure still propagates.
    throw error;
  }
  nodeProcess.exitCode = 1;
});
