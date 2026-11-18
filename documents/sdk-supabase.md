# Supabase Edge Functions SDK

`sdk/supabase` is the glitch-port SDK for Supabase Edge Functions. It runs on
Deno, has no runtime dependencies (standard web APIs only) and turns unhandled
handler exceptions and manual captures into glitch-port events. It is shipped
as TypeScript source: there is no npm, JSR or package registry release, so you
either copy the source into your function or import it from the repository's
raw GitHub URL.

`withGlitchPort` wraps a standard `Deno.serve` handler. A handler that returns a
response behaves exactly as before and sends nothing. A handler that throws is
captured as one `error` event and answered with the standard glitch-port 500
JSON response, so the client that triggered the failure can quote the
`event_id` when reporting the problem.

The SDK reports `sdk.name = "glitchport.supabase"`, `sdk.version = "0.1.0"` and
`platform = "javascript"` on every event. Protocol v1 requires a platform, so
it is always sent and cannot be configured to another value.

## Requirements

- A Supabase Edge Function project (Deno runtime). The SDK needs `fetch`,
  `Request`, `Response`, `URL`, `AbortController` and `crypto`, all of which are
  available in Supabase's Deno runtime.
- A glitch-port project DSN in the form
  `https://<publicKey>@<host>[:port]/<projectId>` (`http` is also accepted).
- Node.js 22 is only needed to run this repository's typecheck and unit tests;
  it is not part of the SDK runtime.

## Installation

### Option 1: copy the source into the function (recommended)

Copy the whole `sdk/supabase/src` directory into the function directory, for
example `supabase/functions/hello/`:

```bash
mkdir -p supabase/functions/hello
cp -R sdk/supabase/src/. supabase/functions/hello/
```

The function directory then looks like this; every file is required because the
modules import each other with relative `.ts` specifiers:

```text
supabase/functions/hello/
├── index.ts
├── glitchport.ts
├── dsn.ts
├── event.ts
├── limits.ts
├── scope.ts
├── stack.ts
├── transport.ts
└── types.ts
```

Import the entry point from `index.ts` with the explicit `.ts` extension that
Deno requires:

```ts
import * as GlitchPort from './glitchport.ts';
```

The `deno.d.ts` file in `src/` only exists so this repository can typecheck the
example without `@types/deno`. Omit it from the deployed function when your
project supplies Deno's own type definitions; it is not loaded at runtime.

### Option 2: import the raw GitHub URL

Deno resolves relative imports inside a remote module against that module's
URL, so a single import is enough:

```ts
import * as GlitchPort from 'https://raw.githubusercontent.com/lolisia/glitch-port/main/sdk/supabase/src/glitchport.ts';
```

Deno fetches `dsn.ts`, `event.ts`, `limits.ts`, `scope.ts`, `stack.ts`,
`transport.ts` and `types.ts` from the same `src/` directory automatically.

This URL tracks the `main` branch and is therefore not version pinned. For
production functions, replace `main` with a release tag or a commit SHA:

```ts
import * as GlitchPort from 'https://raw.githubusercontent.com/lolisia/glitch-port/<commit-sha>/sdk/supabase/src/glitchport.ts';
```

Supabase caches remote modules per deployment, so changing the URL (or the
pinned commit) is what makes a new SDK version take effect.

### Set the DSN as a Supabase secret

`GLITCHPORT_DSN` must be available to the deployed function. With the Supabase
CLI:

```bash
supabase secrets set GLITCHPORT_DSN='https://public-key@glitchport.example.com/42'
```

Add `--env-file` for local development, for example with
`supabase functions serve hello --env-file supabase/.env.local`, because
`supabase secrets` only applies to deployed functions. Never commit the DSN
secret to the repository.

## Quick start

`index.ts` in your function directory:

```ts
import * as GlitchPort from './glitchport.ts';

GlitchPort.init({
  dsn: Deno.env.get('GLITCHPORT_DSN')!,
  environment: 'production',
  release: 'api@1.2.0',
});

Deno.serve(
  GlitchPort.withGlitchPort(async (req) => {
    const url = new URL(req.url);

    // Throw an error on purpose so the wrapper captures it and returns the 500.
    if (url.searchParams.get('fail') === '1') {
      throw new Error('intentional failure from supabase edge function');
    }

    return new Response('ok');
  }),
);
```

Deploy it with `supabase functions deploy hello`. A normal request returns
`ok`; a request with `?fail=1` produces the automatic error response described
below and a captured event in glitch-port.

The complete runnable example lives at
`sdk/supabase/examples/edge-function/index.ts`. It imports
`../../src/glitchport.ts` because it is part of this repository; change that to
`./glitchport.ts` after copying the source into a function directory.

## Initialization

```ts
GlitchPort.init(options: GlitchPortOptions): void
```

`init` parses the DSN, creates the HTTP transport and installs the global
scope. Call it once, at module top level, before `Deno.serve` is called. Edge
Functions boot one module instance per isolate, so every isolate initializes
its own client.

- `dsn` is the only required option: the DSN format is
  `https://<publicKey>@<host>[:port]/<projectId>`. A malformed value throws an
  `Error` whose message starts with `Invalid DSN:`. Because `init` runs at
  module top level, a missing `GLITCHPORT_DSN` secret fails the function at
  boot instead of silently dropping reports.
- A successful `init` replaces any previous client and global scope in the
  isolate. There is no `close`; the isolate and its client end together.
- Before a successful `init` (for example when the first DSN was invalid)
  capture calls are safe no-ops: they return a valid event ID but send nothing,
  and `flush` resolves `true`.
- `init` never throws for the reporting path; only DSN validation errors
  escape.

## Handling requests with withGlitchPort

```ts
GlitchPort.withGlitchPort(
  handler: (req: Request, info?: unknown) => Response | Promise<Response>,
  opts?: { rethrow?: boolean },
): (req: Request, info?: unknown) => Promise<Response>
```

`withGlitchPort` returns a handler that can be passed straight to
`Deno.serve`. For every request it:

1. clones the current global scope, so the request starts with the tags, user,
   contexts and breadcrumbs configured so far;
2. adds the automatic request context, runtime context and `http.method` tag
   described below;
3. runs the supplied handler and returns its response unchanged when it
   succeeds; nothing is sent for successful responses.

### Unhandled exceptions

When the handler throws, the wrapper:

1. captures the thrown value on the request scope at level `error`, including
   the `Error.cause` chain (root cause first) and the parsed stack trace;
2. waits for the pending send;
3. returns a response with status `500`, header
   `content-type: application/json` and this exact body shape (key order is
   stable, `event_id` is the id of the captured event):

```json
{"error":"Internal Server Error","event_id":"9f8c1c1d2a3b4c5d6e7f8091a2b3c4d5"}
```

Waiting for the send uses `EdgeRuntime.waitUntil(flush)` when the Edge runtime
exposes that function: the 500 is returned immediately and the runtime keeps
the isolate alive until the send settles. On other runtimes the wrapper awaits
`GlitchPort.flush(timeoutMs)` before returning the 500.

To let the error reach Supabase's own error handling instead of answering with
the 500, pass `rethrow`:

```ts
Deno.serve(
  GlitchPort.withGlitchPort(
    async (req) => handleRequest(req),
    { rethrow: true },
  ),
);
```

The event is still captured and flushed; the original thrown value is then
rethrown unchanged.

### Automatic request context

Every event captured through the request scope carries:

| Event field | Value |
| ----------- | ----- |
| `contexts.request.method` | The HTTP method, uppercased (`GET`, `POST`, ...). |
| `contexts.request.url` | Protocol, host and path of the request URL. The query string and fragment are removed, so tokens in query parameters are never reported. |
| `contexts.request.headers` | Only the `user-agent` and `content-type` headers, each present only when the request sent it. `Authorization`, `Cookie`, `X-Forwarded-For` and every other header are deliberately excluded. |
| `contexts.runtime` | `{ "name": "deno", "version": "<version>" }` when `Deno.version.deno` is a string; omitted otherwise. |
| `tags["http.method"]` | The HTTP method, uppercased. |

### Per-request state with scope(req)

```ts
GlitchPort.scope(req: Request): RequestScope
```

`withGlitchPort` creates an independent scope for each request.
`GlitchPort.scope(req)` returns that request's scope while the handler is
running. The `RequestScope` object has the same mutators and capture methods as
the module-level API:

```ts
Deno.serve(
  GlitchPort.withGlitchPort(async (req) => {
    const request = GlitchPort.scope(req);
    request.setTag('route', 'checkout');
    request.setUser({ id: 'player-42' });
    request.setContext('cart', { items: 3, currency: 'USD' });
    request.addBreadcrumb({ category: 'db', message: 'loaded inventory' });

    try {
      return await handleCheckout(req);
    } catch (error) {
      request.captureException(error);
      await GlitchPort.flush(); // manual captures are not flushed automatically
      return new Response('checkout failed', { status: 400 });
    }
  }),
);
```

Rules for `scope(req)`:

- Pass the exact `Request` object the wrapped handler received. Any other
  object, a request handled outside `withGlitchPort`, or a request whose handler
  has already finished makes `scope(req)` throw an error that explains the
  restriction.
- `scope(req)` is valid only while the handler runs. Do not keep the scope (or
  start background work that uses it) after returning a response.
- The module-level functions (`GlitchPort.captureException`,
  `GlitchPort.setTag`, ...) always operate on the global scope, even inside a
  handler. They do not see a request's automatic request context, tags or user.
- The reason is runtime capability, not preference: Supabase's Deno runtime has
  no `AsyncLocalStorage`, so there is no ambient storage that would let a
  module-level function discover the current request. Passing the request (or
  the scope) explicitly is the only way to associate state with one request.
- Concurrent requests never share a scope. Each request clones the global scope
  at dispatch time, and changes made through one request's scope do not affect
  another request or the global scope.

## Manual API

All capture methods return the event ID as 32 lowercase hexadecimal characters,
and never throw: before `init`, when an event is sampled away, when
`beforeSend` returns `null`, or when anything inside the reporting path fails,
the caller still receives a valid ID and the function keeps running.

| Function | Signature | Description |
| -------- | --------- | ----------- |
| `captureException` | `(error: unknown, hint?: { level?: Level; tags?: Record<string, string>; extra?: Record<string, unknown> }) => string` | Capture an exception on the global scope. Default level `error`; `Error.cause` chains are reported root cause first. Non-`Error` values are captured as an `Error` with their string form. |
| `captureMessage` | `(message: string, level?: Level) => string` | Capture a message on the global scope. Default level `info`. |
| `setTag` | `(key: string, value: string) => void` | Set a tag on the global scope. |
| `setUser` | `(user: GlitchPortUser \| null) => void` | Attach the user to the global scope, or clear it with `null`. |
| `setContext` | `(name: string, context: Record<string, unknown> \| null) => void` | Attach a named context to the global scope, or remove it with `null`. |
| `addBreadcrumb` | `(breadcrumb: GlitchPortBreadcrumbInput) => void` | Record a breadcrumb on the global scope. |
| `flush` | `(timeoutMs?: number) => Promise<boolean>` | Wait for in-flight sends. Resolves `true` when they settled in time, `false` on timeout. Uses `timeoutMs` from the options when omitted; resolves `true` before `init`. |
| `scope` | `(req: Request) => RequestScope` | Return the request scope created by `withGlitchPort`; see above. |

`Level` is one of `'debug'`, `'info'`, `'warning'`, `'error'` or `'fatal'`;
any other value is normalized to `'error'` when the event is built.

`GlitchPortUser` accepts `id`, `username`, `email` and `ip_address`.
`GlitchPortBreadcrumbInput` accepts `category`, `message`, `level` (default
`info`), `type` and `data`. The SDK adds the ISO-8601 `timestamp` at capture
time. Captures use the same sampling, `beforeSend` and transport path as the
automatic wrapper path.

At the module level:

```ts
GlitchPort.setTag('function', 'hello');
GlitchPort.setUser({ id: 'player-42' });
GlitchPort.setContext('deployment', { region: 'eu-west-1' });
GlitchPort.addBreadcrumb({ category: 'startup', message: 'client initialized' });

GlitchPort.captureMessage('import finished', 'info');
GlitchPort.captureException(new Error('import failed'), {
  tags: { stage: 'parse' },
  extra: { rows: 1200 },
});

await GlitchPort.flush(); // waits for both events
```

Inside a handler, use the request scope instead, and flush yourself when the
capture is not the automatic exception path:

```ts
const request = GlitchPort.scope(req);
request.captureMessage('cache miss', 'warning');
await GlitchPort.flush();
```

## Options

`GlitchPort.init` accepts these options (only `dsn` is required):

| Option | Default | Description |
| ------ | ------- | ----------- |
| `dsn` | required | Project DSN: `https://<publicKey>@<host>[:port]/<projectId>`. Malformed values throw `Invalid DSN: ...`. |
| `environment` | none | Environment name attached to every event, for example `production`. |
| `release` | none | Release identifier attached to every event, for example `api@1.2.0`. |
| `serverName` | none | Value reported as `server_name` on every event. The SDK does not guess the Supabase region; set it when you want one. |
| `sampleRate` | `1` | Fraction of events to send, clamped to 0..1. `0` drops every event, `1` keeps every event. Dropped events still return an event ID. |
| `maxBreadcrumbs` | `50` | Breadcrumbs retained per scope and sent per event, clamped to 0..100. |
| `beforeSend` | none | `(event) => event \| null`, called before every send. Return `null` to drop the event. A hook that throws drops that event. |
| `inAppInclude` | none | String prefixes/substrings that mark stack frames as in-app. When set, this replaces the default `in_app` rule described below. |
| `fetchImpl` | `globalThis.fetch` | `fetch` implementation used by the transport, useful for tests. |
| `debug` | `false` | Log transport diagnostics (send, skip, timeout, flush) through `console.debug` with the `[glitchport]` prefix. |
| `timeoutMs` | `3000` | Per-send timeout in milliseconds; also the default `flush` timeout. Values below 1 are raised to 1. |

For example:

```ts
GlitchPort.init({
  dsn: Deno.env.get('GLITCHPORT_DSN')!,
  environment: 'production',
  release: 'api@1.2.0',
  serverName: 'hello-function',
  sampleRate: 0.5,
  maxBreadcrumbs: 25,
  timeoutMs: 5000,
  beforeSend: (event) => (event.level === 'debug' ? null : event),
});
```

## in_app stack frame rule

`in_app` tells the dashboard which frames belong to your function. Without
`inAppInclude`, a frame is in-app when its path contains `/functions/` or is a
local file, and the path does not contain a dependency marker.

- Local paths start with `file://`, `/`, `./`, `../`, a Windows drive letter
  (`C:/` or `C:\`), or a UNC prefix (`\\`).
- The dependency markers, compared case-insensitively, are `node_modules`,
  `deno.land`, `esm.sh`, `jsr.io` and `jsr:`. These are never in-app.
- Frames from Supabase's function directory, which contains `/functions/`, are
  in-app even when the runtime reports them as a remote URL.

Setting `inAppInclude` overrides the default rule completely: a frame is in-app
only when at least one non-empty entry is a substring of the frame's `filename`
or of its decoded absolute path. An empty array therefore marks no frame as
in-app. This is useful when your code is loaded from a URL that does not
contain `/functions/`:

```ts
GlitchPort.init({
  dsn: Deno.env.get('GLITCHPORT_DSN')!,
  inAppInclude: ['/functions/v1/checkout/'],
});
```

## Transport and flush behaviour

Each event is sent as one HTTP request:

```text
POST <dsn origin>/api/<projectId>/events
X-GlitchPort-Key: <publicKey>
Content-Type: application/json
```

- The only success status is `202 Accepted`. Any other status, a network
  failure, an unparseable event or a timeout counts as a failed delivery, and
  `send` itself never throws or rejects.
- Requests time out after `timeoutMs` (default 3000 ms). The timeout aborts the
  request through `AbortController`; the event is dropped.
- On `429 Too Many Requests` or `503 Service Unavailable` the transport reads
  `Retry-After` (seconds or an HTTP date; missing or invalid values mean 5
  seconds) and opens a retry window. Events captured during that window are
  dropped immediately without issuing a request. There is no queued retry.
- Other error statuses and network failures are dropped; the SDK does not retry.
- `flush(timeoutMs?)` waits for every in-flight send. It resolves `true` when
  they all settle within the timeout (or nothing was pending) and `false` on
  timeout. Before `init` it resolves `true`.
- The automatic unhandled-exception path flushes before the 500 leaves the
  function: `EdgeRuntime.waitUntil(flush)` when that Supabase Edge Runtime
  helper is available, otherwise an awaited `GlitchPort.flush(timeoutMs)`.
- Manual captures are not flushed automatically. Await
  `GlitchPort.flush()` before returning when losing the event matters, or hand
  the promise to the runtime with `EdgeRuntime.waitUntil(GlitchPort.flush())`
  when available.

## Protocol limits

The SDK normalizes every event to the collector's protocol limits before it
is serialized, so an oversized payload is truncated instead of rejected:

| Item | Limit |
| ---- | ----- |
| Exceptions per event | 10, root cause first |
| Stack frames per exception | 250, keeping the frames closest to the throw site |
| Breadcrumbs per event | 100 (a scope retains `maxBreadcrumbs`, default 50) |
| Tags per event | 50; when a scope would exceed 50 keys, the oldest key is dropped |
| Fingerprint components | 10 |
| `event.message` | 8192 characters |
| Exception value | 8192 characters |
| Frame strings (`function`, `filename`, `module`, `abs_path`) | 1024 characters each |
| `contexts` | 64 KiB of serialized JSON; trailing entries are dropped |
| `extra` | 64 KiB of serialized JSON; trailing entries are dropped |

Additional field caps: tag keys 32 and tag values 200 characters, exception
type and fingerprint component 200 characters, release 200, environment 64,
`server_name` 200 and logger 128 characters.

## Local verification (repository only)

The SDK ships an end-to-end example that runs against a real local collector
instead of mocks:

```bash
bash scripts/e2e-supabase.sh
```

The script is integration-only: it needs Docker (Compose v2) for PostgreSQL and
the .NET 10 SDK for the collector, uses the fixed host ports 5080 (Relay), 5090
(Processor) and 55432 (PostgreSQL), and is not part of `scripts/verify.sh`. Do
not run it at the same time as the other fixed-port end-to-end scripts
(`scripts/e2e-hello.sh`, `scripts/e2e-events.sh`, ...). The script starts
PostgreSQL, the Processor and the Relay, waits for both `/healthz` endpoints,
runs the example and always tears the stack down again.

`sdk/supabase/examples/node-e2e.ts` runs on Node.js 22 with
`--experimental-strip-types` and imports the SDK source directly through
`../src/glitchport.ts`. That relative `.ts` specifier is the same form Deno
requires, so the example needs no build step and no package install. It:

- initializes the SDK with `environment: 'e2e'` and
  `release: 'supabase-e2e@0.0.0'` and invokes a handler wrapped with
  `withGlitchPort` on a request with `?fail=1`, so the handler throws on
  purpose;
- asserts the automatic response: status `500`, a `content-type` starting with
  `application/json`, and a body with `error: "Internal Server Error"` and a
  32-character lowercase hexadecimal `event_id`;
- reads `GET /api/manage/events/{eventId}` (dashed UUID form) until the stored
  event is visible and checks that it has platform `javascript` and
  `payload.exception[0].type === "Error"`;
- polls `GET /api/manage/projects/{projectId}/issues?status=all&limit=100`
  until the generated issue exists with platform `javascript`, a title
  containing `Error: intentional failure from supabase edge function` and
  `event_count >= 1`.

The example reads these environment variables:

| Variable | Default | Description |
| -------- | ------- | ----------- |
| `GLITCHPORT_DSN` | required | Project DSN used by the SDK, for example `http://dev-public-key@localhost:5080/1`. |
| `GLITCHPORT_MANAGE_URL` | `http://localhost:5090/api/manage` | Management API base URL. |
| `GLITCHPORT_ADMIN_TOKEN` | `dev-admin-token` | `Authorization: Bearer` token for the Management API. |

`GLITCHPORT_PROJECT_ID` optionally overrides the project id that is otherwise
taken from the last non-empty segment of the DSN path.

## Limitations

- **Manual captures are not flushed automatically.** Only the automatic
  unhandled-exception path waits for the send. Call `GlitchPort.flush()` (or
  `EdgeRuntime.waitUntil(GlitchPort.flush())`) yourself when you capture
  manually inside a handler.
- **No source maps.** Stack frames point at the modules Deno evaluates in the
  Edge runtime; line and column numbers are not mapped back to your original
  source formatting.
- **No offline queue and no retries.** Events that fail (network error,
  non-202, timeout) are dropped. `429`/`503` only pause sending for the
  `Retry-After` window.
- **The raw URL import is not version pinned.** Importing from `main` follows
  the branch; pin a commit SHA or tag for production functions.
- **No runtime dependencies.** The SDK imports no npm, JSR or Node built-in
  modules; it uses standard web APIs only. The `devDependencies` in
  `sdk/supabase/package.json` exist only for typechecking and tests.
- **Request scopes are not ambient.** There is no `AsyncLocalStorage` in the
  Edge runtime, so module-level helpers always use the global scope and
  request-local state is only reachable through `GlitchPort.scope(req)` while
  the handler runs.
- **One client per isolate.** `init` installs module state for the JavaScript
  realm. Supabase may run several isolates; each one initializes and reports
  independently, and a global scope is never shared between isolates.
