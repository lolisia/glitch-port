# glitch-port

glitch-port is a Sentry-like error and crash reporting platform specialised for
game engines (Unity, Unreal, Godot, …). Game clients and servers send
**protocol v1 structured events** — messages, structured exceptions with
inner-to-outer chains and stack frames, contexts, tags, breadcrumbs, user data
and grouping fingerprints — to a public Relay. The Processor groups the events
into issues, stores them idempotently in PostgreSQL and serves the Management
API that powers the dashboard.

The repository contains the complete MVP:

- the **protocol v1** event format and JSON schema (`sdk/protocol`);
- the **collector** backend: a public **Relay** that authenticates, validates
  and rate-limits events, and a stateless **Processor** that groups events into
  issues, stores them in PostgreSQL, applies retention and serves the
  Management API;
- **projects, public ingest keys and per-key rate limits**, created and managed
  through the Management API;
- the **dashboard** for browsing and triaging issues, events, projects, keys
  and the legacy message list;
- the **.NET SDK** (`GlitchPort.Core`), which captures events and sends them to
  the collector or to local terminal/file relays, plus the
  **ASP.NET Core integration** (`GlitchPort.AspNetCore`) with request
  middleware and `ILogger` forwarding and an opt-in **HTTP offline queue**;
- the **Unity UPM package** (`com.glitchport.unity`), which captures structured
  exceptions, Unity logs, contexts and breadcrumbs automatically;
- the **Supabase Edge Functions SDK** (`sdk/supabase`), a Deno SDK built around
  `withGlitchPort`;
- a **full local Docker Compose stack** (Relay, two Processors, PostgreSQL and
  the dashboard) and end-to-end scripts for every component;
- the **HelloClient** and **GameServerSample** samples.

See [Architecture](#architecture) for the data flow and
[Documentation](#documentation) for the published API and SDK guides.

## Architecture

```text
  Unity SDK                    .NET SDK                     Supabase SDK
  (com.glitchport.unity)       (GlitchPort.Core +           (Edge Functions)
                                GlitchPort.AspNetCore)
        │                          │                              │
        └──────────────────────────┼──────────────────────────────┘
                                   │  POST /api/{projectId}/events
                                   │  X-GlitchPort-Key: <public-key>
                                   ▼
  ┌────────────────────────────────────────────────────────────────────────┐
  │ Relay :5080 — N instances                                              │
  │ • public ingest: POST /api/{projectId}/events                          │
  │ • legacy: POST /api/{projectId}/messages                               │
  │ • validates project + public key; project and per-key rate limits      │
  │ • no database access; caches project config from the Processor         │
  │ • forwards through the Transport abstraction (HTTP today)              │
  └───────────────────────────────────┬────────────────────────────────────┘
                                      │  POST /internal/envelopes
                                      │  X-GlitchPort-Internal: <secret>
                                      ▼
  ┌────────────────────────────────────────────────────────────────────────┐
  │ Processor :5090 (host) / :5091, :5092 (full profile)                   │
  │ • N stateless instances, no sticky sessions                            │
  │ • groups events into issues; idempotent storage by event_id            │
  │ • advisory-locked migrations and retention                             │
  │ • Management API: Authorization: Bearer <admin-token>                  │
  └───────────────┬────────────────────────────────────────▲───────────────┘
                  │                                        │ Management API
                  ▼                                        │
  ┌────────────────────────────────┐            ┌──────────────────────────┐
  │ PostgreSQL :55432              │            │ Dashboard :5173          │
  │ (container port 5432)          │            │ issues, projects, keys   │
  └────────────────────────────────┘            └──────────────────────────┘
```

- The Relay never touches PostgreSQL. It validates an event and forwards it to
  the Processor through a transport abstraction (`GlitchPort.Transport.Http`
  sends the envelopes today).
- The Processor stores events idempotently by `event_id`, so several Processor
  instances can share one database and retries are safe. Migrations and the
  retention job take PostgreSQL advisory locks, so only one instance performs
  each of those tasks at a time.
- In the SDK each relay has its own bounded queue and worker, so a slow or
  failing relay never blocks the game thread or the other relays.
- Public ingest (`POST /api/{projectId}/events`) authenticates with the
  project's public key in `X-GlitchPort-Key`. The Relay-to-Processor hop uses
  `POST /internal/envelopes` with `X-GlitchPort-Internal`. The Management API
  below `/api/manage` requires `Authorization: Bearer <Processor:AdminToken>`.
- The dashboard only talks to the Processor's Management API; it never accesses
  PostgreSQL directly.

In short, the data path is SDK → Relay `:5080` → Processor `:5090` (host) or
`:5091`/`:5092` (full profile) → PostgreSQL `:55432`; the dashboard `:5173`
reads that data through the Processor's Management API.

## Components and ports

| Component                 | Path                                      | Host port                | Notes                                                                 |
| ------------------------- | ----------------------------------------- | ------------------------ | --------------------------------------------------------------------- |
| Relay                     | `collector/src/GlitchPort.Relay`          | `5080`                   | Public ingest (`POST /api/{projectId}/events`), legacy messages, `GET /healthz`. No database access. |
| Processor                 | `collector/src/GlitchPort.Processor`      | `5090` (host), `5091`/`5092` (full profile) | Internal ingest (`POST /internal/envelopes`), Management API (`/api/manage`), retention and migrations. |
| PostgreSQL                | `docker-compose.yml`                      | `55432`                  | Container port `5432`; user, password and database are `glitchport`.   |
| Dashboard                 | `dashboard`                               | `5173`                   | React 19 + Vite UI for issues, projects, keys and messages.           |
| .NET SDK core             | `sdk/dotnet/src/GlitchPort.Core`          | –                        | `GlitchPortClient`, protocol v1 events, `http`/`terminal`/`file` relays. `netstandard2.1`, no package dependencies. |
| ASP.NET Core integration  | `sdk/dotnet/src/GlitchPort.AspNetCore`    | –                        | `AddGlitchPort`, `UseGlitchPort`, request middleware and `ILogger` forwarding. `net10.0`. |
| Terminal renderer         | `sdk/dotnet/tools/GlitchPort.Terminal`    | –                        | `glitchport-terminal` console renderer started by the `terminal` relay. |
| Unity package             | `sdk/unity/com.glitchport.unity`          | –                        | UPM package `com.glitchport.unity` with automatic initialization, structured exceptions, contexts, breadcrumbs and offline queue. |
| Unity test project        | `sdk/unity/TestProject`                   | –                        | Unity 6000.6.8f1 project used by `scripts/unity-test.sh`, `scripts/e2e-unity.sh` and `scripts/e2e-unity-http.sh`. |
| Supabase SDK              | `sdk/supabase`                            | –                        | Deno/TypeScript SDK for Supabase Edge Functions (`withGlitchPort`).    |
| HelloClient sample        | `sdk/dotnet/samples/HelloClient`          | –                        | Console sample used by the end-to-end checks and as a relay playground. |
| GameServerSample          | `sdk/dotnet/samples/GameServerSample`     | `5070` (e2e)             | ASP.NET Core sample showing `AddGlitchPort`/`UseGlitchPort`, `/boom` and `/log-error`. |
| Protocol v1               | `sdk/protocol`                            | –                        | `event.v1.schema.json`, the single source of truth for the event body, plus examples. |
| Collector contracts       | `collector/src/GlitchPort.Collector.Contracts` | –                   | Envelope contracts and the `IEnvelopePublisher` transport abstraction. |
| HTTP transport            | `collector/src/GlitchPort.Transport.Http` | –                        | Relay → Processor HTTP publishing with rotation, failover and the internal shared secret. |

## Prerequisites

- **.NET 10 SDK** — collector, .NET SDK, samples and tests.
- **Node.js 22 with npm** — dashboard build/tests and the Supabase SDK
  typecheck/tests.
- **Docker with Compose v2** — the collector stack, PostgreSQL and the
  Docker-backed end-to-end scripts.
- **Unity 6000.6.8f1** — optional. Only `scripts/unity-test.sh`,
  `scripts/e2e-unity.sh` and `scripts/e2e-unity-http.sh` need it; they skip
  with exit code 0 when the editor recorded in
  `sdk/unity/TestProject/ProjectSettings/ProjectVersion.txt` is not installed.
- **bash** — every script is a bash script. On Windows use Git Bash.

## Quick start: full local stack

The `full` Docker Compose profile starts the whole platform: PostgreSQL, two
Processors, the Relay and the dashboard. The first build can take several
minutes.

```bash
docker compose --profile full up -d --build --wait
```

Then open the dashboard at <http://localhost:5173> and check the health
endpoints:

```bash
curl -s http://localhost:5080/healthz   # Relay
curl -s http://localhost:5091/healthz   # Processor 1
curl -s http://localhost:5092/healthz   # Processor 2
```

The profile is **development-only**. Its fixed credentials are:

| Value               | Development default                                              |
| ------------------- | ---------------------------------------------------------------- |
| Admin token         | `dev-admin-token` (`Processor:AdminToken`)                       |
| Internal secret     | `dev-internal-secret` (`Processor:InternalSharedSecret`, `Relay:InternalSharedSecret`) |
| Seeded project      | `1` (`Default`, slug `default`)                                  |
| Seeded public key   | `dev-public-key`                                                 |
| Seeded project DSN  | `http://dev-public-key@localhost:5080/1`                         |
| PostgreSQL          | `localhost:55432`, database/user/password `glitchport`           |

Create another project and read its DSN from the response:

```bash
curl -s -X POST "http://localhost:5091/api/manage/projects" \
  -H "Authorization: Bearer dev-admin-token" \
  -H "Content-Type: application/json" \
  --data-binary '{"name":"Star Raiders","platform":"unity"}'
```

PowerShell:

```powershell
Invoke-RestMethod -Method Post -Uri "http://localhost:5091/api/manage/projects" `
  -Headers @{ Authorization = "Bearer dev-admin-token" } `
  -ContentType "application/json" `
  -Body '{"name":"Star Raiders","platform":"unity"}'
```

The `201` response contains the new project's `dsn`, for example
`http://<public-key>@localhost:5080/<project-id>`. The seeded project `1`
already has the fixed DSN `http://dev-public-key@localhost:5080/1`.

Send one event through the .NET SDK sample (the full stack must be running):

```bash
GLITCHPORT_DSN="http://dev-public-key@localhost:5080/1" \
  dotnet run --project sdk/dotnet/samples/HelloClient -- "hello"
```

PowerShell:

```powershell
$env:GLITCHPORT_DSN = "http://dev-public-key@localhost:5080/1"
dotnet run --project sdk/dotnet/samples/HelloClient -- "hello"
Remove-Item Env:GLITCHPORT_DSN
```

Open <http://localhost:5173/#/issues> (project 1) to see the event grouped
into an issue.

Run the ASP.NET Core sample against the same stack, then exercise its error
paths. In bash:

```bash
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=http://localhost:5070 \
GlitchPort__Dsn="http://dev-public-key@localhost:5080/1" \
  dotnet run --project sdk/dotnet/samples/GameServerSample --no-launch-profile
```

In another terminal:

```bash
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5070/boom
curl -s http://localhost:5070/log-error
```

PowerShell:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:5070"
$env:GlitchPort__Dsn = "http://dev-public-key@localhost:5080/1"
dotnet run --project sdk/dotnet/samples/GameServerSample --no-launch-profile

# In a second PowerShell window:
curl.exe -s -o NUL -w "%{http_code}\n" http://localhost:5070/boom
curl.exe -s http://localhost:5070/log-error
```

`GET /boom` throws an `InvalidOperationException` with an inner
`ArgumentException`; `UseGlitchPort` captures and rethrows it, so the response
is the usual `500`. `GET /log-error` writes one `ILogger.LogError`, which the
provider registered by `AddGlitchPort` forwards as an event.

Stop the stack with:

```bash
docker compose --profile full down -v
```

`-v` also removes the anonymous PostgreSQL volume; the local stack has no named
volume and its data is disposable. Use `docker compose --profile full down`
without `-v` to remove the containers but leave the anonymous PostgreSQL volume
in place.

## Sending events with the SDK sample

```bash
dotnet run --project sdk/dotnet/samples/HelloClient -- [message] [--level <debug|info|warning|error|fatal>] [--count <n>] [--hold <seconds>] [--exception]
```

- `message` defaults to `hello`, `--level` to `info`, `--count` to `1` and
  `--hold` to `0`.
- `--exception` captures a sample `InvalidOperationException` (with an inner
  `ArgumentException`) through `CaptureExceptionAsync` instead of capturing a
  message.
- `--hold` keeps the process alive after the flush, for example to read the
  terminal window; the window closes when the sample exits.

The relays come from `GLITCHPORT_DSN` (see
[GLITCHPORT_DSN](#glitchport_dsn)). Without it the sample sends to the local
collector at `http://dev-public-key@localhost:5080/1`, which matches the
development Relay configuration — start the Processor and Relay first, or use
the full compose profile above.

The sample prints `event_id: <guid>` per event, `terminal_pid: <pid>` for each
terminal renderer that started, and one
`relay <name> (<type>): delivered=<n> failed=<n> dropped=<n>` line per relay. It
exits with `0` only when every relay succeeded; otherwise it prints
`error: ...` and exits with `1`.

### Local relays: terminal window and log files

The local relays need no collector, network or Docker.

bash (Git Bash):

```bash
GLITCHPORT_DSN="terminal:?window=new;file:?dir=logs/glitchport" \
  dotnet run --project sdk/dotnet/samples/HelloClient -- "hello" --hold 10
```

PowerShell (from the repository root):

```powershell
$env:GLITCHPORT_DSN = "terminal:?window=new;file:?dir=logs/glitchport"
dotnet run --project sdk/dotnet/samples/HelloClient -- "hello" --hold 10
Remove-Item Env:GLITCHPORT_DSN
```

- `terminal` starts `glitchport-terminal` as a child process the first time an
  event is captured and streams events to it over stdin. With `window=new` it
  opens its own console window on Windows; `window=inherit` prints to the
  current console (Linux/macOS always inherit). The renderer exits when the
  sample exits — also when the sample crashes or is killed.
- `file` writes one JSON event per line to
  `logs/glitchport/glitchport-<yyyyMMdd-HHmmss>-<pid>.ndjson`, rotating and
  pruning old files automatically. `logs/` is git-ignored.

Several relays can be combined, for example local files plus the collector:

```bash
GLITCHPORT_DSN="file:?dir=logs/glitchport;http://dev-public-key@localhost:5080/1?min_level=error"
```

## SDK examples

### .NET SDK

```csharp
using GlitchPort;

var options = new GlitchPortOptions();
options.Relays.Add(Relay.Http("http://dev-public-key@localhost:5080/1", o => o.MinLevel = Level.Error));
options.Relays.Add(Relay.File(o => o.Directory = "logs/glitchport"));

using var client = new GlitchPortClient(options);   // or new GlitchPortClient(GlitchPortOptions.FromEnvironment())
await client.CaptureMessageAsync("hello", Level.Warning);
DeliveryReport report = await client.FlushAsync();
```

`CaptureMessageAsync` only enqueues the event and returns its id; it does not
throw on delivery failures. `FlushAsync()` waits for the relays and returns
per-relay results. The same client can capture exceptions with
`CaptureExceptionAsync`, custom protocol v1 events with `CaptureEventAsync`,
and attach scopes, tags, breadcrumbs, release and environment.

The HTTP relay can enable the opt-in offline queue, which persists
undeliverable events to a directory and resends them later:

```csharp
options.Relays.Add(Relay.Http("http://dev-public-key@localhost:5080/1", http =>
{
    http.OfflineQueue.Directory = "logs/glitchport-queue";
    http.OfflineQueue.MaxEvents = 500;            // default
    http.OfflineQueue.MaxAgeHours = 72;           // default, 0 disables the age limit
    http.OfflineQueue.RetryIntervalSeconds = 30;  // default
}));
```

Every option is documented in [.NET SDK](documents/sdk-dotnet.md) and
[.NET SDK relays](documents/sdk-dotnet-relays.md).

### ASP.NET Core integration

Add a `GlitchPort` section to `appsettings.json`:

```json
{
  "GlitchPort": {
    "Dsn": "http://dev-public-key@localhost:5080/1"
  }
}
```

Register and use the integration:

```csharp
using GlitchPort.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGlitchPort(builder.Configuration);

var app = builder.Build();

app.UseDeveloperExceptionPage(); // before UseGlitchPort
app.UseRouting();
app.UseGlitchPort();             // after UseRouting (and UseAuthentication)

app.MapGet("/ok", () => Results.Ok(new { status = "ok" }));
app.Run();
```

`AddGlitchPort` binds the `GlitchPort` section (including `GlitchPort:Dsn`),
registers the client, the request middleware and the `ILogger` provider.
`UseGlitchPort` pushes one scope per request, records `contexts.request`, and
captures unhandled request exceptions before rethrowing them. When
`GlitchPort:Dsn` is absent or blank, `GLITCHPORT_DSN` is used instead. See the
[ASP.NET Core integration](documents/sdk-dotnet.md#aspnet-core-integration)
section of the .NET SDK guide, and the
[GameServerSample](sdk/dotnet/samples/GameServerSample/README.md) for a
runnable host.

### Unity SDK

Install the UPM package from `sdk/unity/com.glitchport.unity`, then create
`Assets/GlitchPort/Resources/GlitchPortSettings.asset` (Project Settings >
Glitch Port creates it) and set:

- `Dsn` — `http://<publicKey>@<host>[:port]/<projectId>`, for example
  `http://dev-public-key@localhost:5080/1`;
- `OfflineQueue` — on by default; events the HTTP relay cannot deliver are
  kept in `Application.persistentDataPath/glitchport/queue` and resent later.
  `OfflineQueueMaxEvents` (default `500`) caps the queue.

No game code is required: the SDK initializes before the first scene loads,
captures Unity errors, asserts and exceptions (with structured exception
chains and stack frames), adds `os`/`device`/`gpu`/`runtime`/`app`/`game`
contexts and lifecycle breadcrumbs, and flushes on quit. In the editor and
development builds the file relay is enabled too, and on Windows the terminal
window opens when its executable was prepared with
`scripts/build-unity-plugins.sh`. `GLITCHPORT_DSN` overrides the asset
settings, which is how `scripts/e2e-unity.sh` runs a real player against a file
relay.

See [Unity SDK](documents/sdk-unity.md) for installation, every setting and
default, the relay rules per editor/development/release/platform, the manual
`GlitchPortUnity` API and the constraints (WebGL is unsupported; IL2CPP,
mobile and console builds are unverified).

### Supabase Edge Functions SDK

```ts
import * as GlitchPort from './glitchport.ts';

GlitchPort.init({
  dsn: Deno.env.get('GLITCHPORT_DSN')!,
});

Deno.serve(
  GlitchPort.withGlitchPort(async (req) => {
    if (new URL(req.url).searchParams.get('fail') === '1') {
      throw new Error('intentional failure');
    }

    return new Response('ok');
  }),
);
```

`withGlitchPort` wraps a normal `Deno.serve` handler: a handler that returns a
response behaves exactly as before, while a handler that throws is captured as
one `error` event and answered with the standard glitch-port 500 JSON response
that contains the `event_id`. The SDK has no runtime dependencies and is
shipped as TypeScript source. See
[Supabase Edge Functions SDK](documents/sdk-supabase.md) for installation,
options, request context, scopes and protocol limits.

## GLITCHPORT_DSN

`GlitchPortOptions.FromEnvironment()` reads `GLITCHPORT_DSN`, a `;`-separated
list of relay specifications. Whitespace around an item and empty items are
ignored, and every item becomes an entry in `Relays`:

```bash
export GLITCHPORT_DSN="http://public-key@relay.example.com:5080/1?min_level=error&name=main;terminal:?window=inherit&format=compact&min_level=debug&name=term;file:?dir=logs/glitchport&format=ndjson&min_level=info&name=file"
```

| Type       | Example `GLITCHPORT_DSN` entry                                         | Destination                                     |
| ---------- | ---------------------------------------------------------------------- | ----------------------------------------------- |
| `http`     | `http://<key>@<host>:<port>/<projectId>?min_level=error&offline_dir=<dir>` | The collector Relay (`POST /api/{projectId}/events`) |
| `terminal` | `terminal:?window=new&format=pretty`                                   | `glitchport-terminal` child process             |
| `file`     | `file:?dir=logs/glitchport&format=ndjson`                              | Rotating NDJSON or text files                   |

Parameter values are URI-decoded, so percent-encode characters that would
otherwise be structural (`%20` for a space, `%26` for `&`, `%3B` for `;`). The
`file` relay's `dir` and the HTTP relay's `offline_dir` expand `~` and
environment variables in `%NAME%`, `$NAME` and `${NAME}` form. Unknown relay
types, unknown parameters and malformed values make `FromEnvironment()` throw
a `FormatException` that names the failing item.

The complete grammar, defaults, delivery semantics and offline queue file
format are in [.NET SDK relays](documents/sdk-dotnet-relays.md).

## Dashboard

```bash
cd dashboard
npm ci        # first time only: installs vite and the other dependencies
npm run dev
```

The dashboard is served at <http://localhost:5173>. It needs a running
Processor (and PostgreSQL); with the development Processor on port `5090` it
lists issues, projects, keys and the legacy message list from
`http://localhost:5090/api/manage` using the development admin token
(`dev-admin-token`). Send an event with the sample to see data. The screens
are `#/issues`, `#/issues/<id>`, `#/projects`, `#/projects/<id>` and
`#/messages`.

The API base and token can be overridden at build/dev time:

- `VITE_API_BASE` — Processor base URL (default `http://localhost:5090`; the
  dashboard appends `/api/manage`).
- `VITE_ADMIN_TOKEN` — bearer token for the Management API (default
  `dev-admin-token`). An empty value omits the header so a same-origin
  authenticated gateway can supply authentication.
- `VITE_PROJECT_ID` — initial project id when the browser has no stored
  selection (default `1`).

The full compose profile builds the dashboard with
`VITE_API_BASE=http://localhost:5091` and `VITE_ADMIN_TOKEN=dev-admin-token`,
because the browser runs on the host and talks to the first Processor through
its published port. See [Dashboard](documents/dashboard.md) for every screen,
the URL filters, error handling and the Management API calls.

## Verification and end-to-end checks

`scripts/verify.sh` is the repository verification entry point:

```bash
bash scripts/verify.sh
```

The repository and Unity package scripts are:

| Script                          | What it proves                                                                                                                             | Needs                              |
| ------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------ | ---------------------------------- |
| `scripts/verify.sh`             | Builds every `.csproj` under `sdk/` and `collector/` in Release (the Unity-owned projects under `sdk/unity/` are handled by the Unity scripts), runs every `.Tests.csproj`, builds and tests the dashboard and Supabase SDK, checks the bundled Unity Core DLL hash and the protocol JSON, and runs the `scripts/verify.d/` hooks. | .NET 10, Node.js 22; Unity optional for the Unity tests. |
| `scripts/unity-test.sh`         | Runs the Unity package's EditMode and PlayMode suites in batch mode; skips with exit code 0 when the editor is not installed.              | Unity 6000.6.8f1 (optional).       |
| `scripts/build-unity-plugins.sh` | Rebuilds the precompiled `GlitchPort.Core.dll` (and on Windows the native `glitchport-terminal` executable) inside the Unity package; `--check` only verifies the checked-in source hash. | .NET 10; native terminal publish on Windows. |

The end-to-end scripts are separate because they need fixed ports and start
real services:

| Script                     | What it proves                                                                                                                             | Needs                              |
| -------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------ | ---------------------------------- |
| `scripts/e2e-hello.sh`     | HelloClient → Relay → Processor → Management API; the stored event is listed with the Relay endpoint.                                      | Docker + Compose v2, .NET 10.      |
| `scripts/e2e-events.sh`    | Protocol v1 example events group into one issue (count 2), resolving it and sending a third event reopens it (count 3), and the legacy message endpoint shares the list. | Docker, .NET 10. |
| `scripts/e2e-projects.sh`  | Create a project through the Management API, wait for the Relay's key refresh, revoke a key (401), enforce a per-key rate limit (429 + `Retry-After`), delete a project (404). | Docker, .NET 10. |
| `scripts/e2e-local-relays.sh` | Terminal and file relays render/write the event, report `failed=0 dropped=0`, and the terminal child exits when the parent is force-killed. | .NET 10 only; no Docker or network. |
| `scripts/e2e-supabase.sh`  | The Supabase SDK runs under Node 22, throws through `withGlitchPort`, returns the 500 response and stores the event and issue in the collector. | Docker, .NET 10, Node.js 22.       |
| `scripts/e2e-dotnet-server.sh` | GameServerSample's `/boom` (captured unhandled exception with inner-first chain, request context and an `in_app` frame) and `/log-error` (ILogger event) reach PostgreSQL. | Docker, .NET 10, Node.js.          |
| `scripts/e2e-compose.sh`   | The complete `full` compose profile: both Processors share one database, the Relay distributes envelopes to both instances, and the dashboard serves HTML. | Docker + Compose v2. |
| `scripts/e2e-unity.sh`     | A real Windows development player auto-initializes from the settings/DSN, captures logs, a manual message, a nested exception and contexts through the file relay. | Windows Git Bash + Unity 6000.6.8f1; no Docker. |
| `scripts/e2e-unity-http.sh` | The same Unity player delivers through the Relay to the Processor and the issue is visible through the Management API. | Windows Git Bash, Docker, .NET 10, Unity. |
| `scripts/e2e-all.sh`       | Runs every `scripts/e2e-*.sh` sequentially and reports a PASS/FAIL/SKIP summary.                                                          | Everything the individual scripts need. |

The collector-backed e2e scripts share fixed development ports (`5080`, `5090`,
`5091`, `5092`, `5070`, `5173` and `55432`) and Docker Compose projects, so
they must not run concurrently; `scripts/e2e-unity.sh` itself adds no fixed
ports. `scripts/e2e-all.sh` is therefore deliberately sequential:

```bash
bash scripts/e2e-all.sh          # run every scripts/e2e-*.sh in order
bash scripts/e2e-all.sh --list   # print the scripts that would run
```

It prints one PASS/FAIL/SKIP result per script (SKIP is an exit-0 script whose
output contains `(skipped)`), prints a summary table, and exits non-zero when
any script failed. Each script's combined output is written to
`.artifact/e2e-all/<script name>.log`; the individual scripts also write their
own scratch files below `.artifact/e2e*/`, which is git-ignored. The runner
needs Docker, the .NET 10 SDK and Node.js 22 for the scripts that need them, and
reports a Docker-based script as FAIL (never pre-skips) when Docker is
unavailable.

Every end-to-end script works from any directory, is non-interactive, tears
down the processes and containers it started (including the Compose PostgreSQL
container and its volume), and exits non-zero as soon as a step fails.
`scripts/e2e-all.sh` deliberately continues with the remaining scripts after a
failure so the summary is complete.

## Collector configuration

Development values live in the environment-specific app settings:

- `collector/src/GlitchPort.Relay/appsettings.Development.json`
- `collector/src/GlitchPort.Processor/appsettings.Development.json`

| Key                                      | Read by   | Development value / default                                                          | Purpose                                                                 |
| ---------------------------------------- | --------- | ------------------------------------------------------------------------------------ | ----------------------------------------------------------------------- |
| `ConnectionStrings:Postgres`              | Processor | `Host=localhost;Port=55432;Database=glitchport;Username=glitchport;Password=glitchport` | PostgreSQL connection used for storage, migrations and retention. Required. |
| `Relay:Projects`                          | Relay     | `{ "1": [ "dev-public-key" ] }`                                                      | Static public ingest keys per project id. Used before the first project-config refresh and for keys that are not in PostgreSQL; a key listed here stays valid even after revocation in the database. |
| `Relay:ProcessorEndpoints`                | Relay     | `[ "http://localhost:5090" ]`                                                        | Processor base URLs. Publishing rotates through them and fails over on retryable errors. |
| `Relay:InternalSharedSecret`              | Relay     | `dev-internal-secret`                                                                | Sent to the Processor as `X-GlitchPort-Internal`; must match the Processor. |
| `Relay:Timeout`                           | Relay     | `10` seconds                                                                         | Per-attempt HTTP timeout to the Processor.                              |
| `Relay:ConfigRefreshSeconds`              | Relay     | `60` (the full profile sets `5`)                                                     | Seconds between project/key refreshes. Values below `1` fall back to `60`. |
| `Relay:UnknownKeyCacheSeconds`            | Relay     | `30`                                                                                 | Lifetime of the negative cache for unknown `(project id, key)` pairs. `0` disables it. |
| `Relay:RateLimit:PerProjectPerMinute`     | Relay     | `6000`                                                                               | Requests per minute across all keys of a project. `0` means unlimited. Per-key limits (`rate_limit_per_minute` in the Management API) are enforced in addition. |
| `Processor:AdminToken`                    | Processor | `dev-admin-token`                                                                    | Bearer token required by every `/api/manage` endpoint.                 |
| `Processor:InternalSharedSecret`          | Processor | `dev-internal-secret`                                                                | Expected `X-GlitchPort-Internal` value on `/internal/*` endpoints.     |
| `Processor:PublicRelayUrl`                | Processor | `http://localhost:5080`                                                              | Public Relay base URL used to build the DSNs returned by the Management API. |
| `Processor:CorsOrigins`                   | Processor | `[ "http://localhost:5173" ]`                                                        | Origins allowed by the dashboard CORS policy.                          |
| `Processor:Bootstrap:Projects`            | Processor | project `1` — `Default`/`default` with key `dev-public-key`                          | Projects and keys created at startup when missing; never updates or deletes existing rows. |
| `Processor:Retention:Days`                | Processor | `30`                                                                                 | Deletes events and legacy messages older than this many days. `0` disables retention. |
| `Processor:Retention:IntervalMinutes`     | Processor | `60`                                                                                 | Minutes between retention runs.                                        |
| `Processor:Retention:BatchSize`           | Processor | `10000`                                                                              | Maximum rows deleted per transaction.                                  |

Any key can be overridden with standard .NET configuration sources.
Environment variables use `__` for the section separator and are the usual way
to configure containers:

```bash
export ConnectionStrings__Postgres="Host=postgres;Port=5432;Database=glitchport;Username=glitchport;Password=glitchport"
export Relay__ProcessorEndpoints__0="http://processor-1:8080"
export Relay__ProcessorEndpoints__1="http://processor-2:8080"
export Processor__Retention__Days=14
```

The values above are development defaults only — do not reuse them in
production. See [Operations](documents/operations.md) for retention behaviour,
multiple Processor instances, the full local stack and backup/restore.

## Deployment

The single-host deployment lives in
[deploy/lightsail/README.md](deploy/lightsail/README.md). It runs the Relay,
one Processor, PostgreSQL and the dashboard on a Lightsail host alongside an
existing RMD stack:

- the dashboard and Relay share **HTTPS port 8443**; ports 80/443 stay with
  RMD, and the gateway reuses RMD's Caddy certificate volume;
- the dashboard requires **HTTP Basic authentication** over HTTPS, with the
  htpasswd file under `secrets/`;
- the Processor and PostgreSQL publish **no host ports**;
- configuration is a **mode-600 `.env`** with `GLITCHPORT_IMAGE_TAG`,
  `GLITCHPORT_HOST`, `POSTGRES_PASSWORD`, `INTERNAL_SHARED_SECRET`,
  `PROCESSOR_ADMIN_TOKEN` and `INGEST_PUBLIC_KEY`, using independent random
  secrets instead of development defaults;
- PostgreSQL data persists in the named volume `glitch-port_postgres-data`;
  `docker compose -f compose.yml down` preserves it, so never add `-v` on the
  production host.

Retention, multiple Processor instances and logical backup/restore
(`pg_dump`/`pg_restore`) are covered in
[documents/operations.md](documents/operations.md).

## Repository layout

```text
collector/                  Relay, Processor, shared contracts and HTTP transport (.NET 10)
dashboard/                  React + Vite dashboard
deploy/lightsail/           Single-host Lightsail stack (compose, Dockerfiles, nginx)
deploy/local/               Local dashboard image for the full compose profile
documents/                  Published SDK and API documentation
scripts/                    verify, build and end-to-end scripts
sdk/dotnet/src/             GlitchPort.Core and GlitchPort.AspNetCore
sdk/dotnet/tools/           GlitchPort.Terminal (glitchport-terminal)
sdk/dotnet/samples/         HelloClient and GameServerSample
sdk/dotnet/tests/           SDK and renderer tests
sdk/protocol/               Protocol v1 JSON schema and examples
sdk/supabase/               Supabase Edge Functions SDK
sdk/unity/                  com.glitchport.unity UPM package and the TestProject
docker-compose.yml          Full local stack (profile full) and PostgreSQL
```

## Documentation

- [Dashboard](documents/dashboard.md) — screens, URL filters, `VITE_*`
  configuration and Management API calls.
- [Event ingest API (protocol v1)](documents/http-api-events.md) — public
  ingest endpoints, authentication, rate limits, event bodies and responses.
- [Management API](documents/management-api.md) — projects, keys, issues,
  events, labels, statistics, retention status and pagination.
- [Operations](documents/operations.md) — retention, multiple Processors, the
  full local stack and backup/restore.
- [.NET SDK](documents/sdk-dotnet.md) — `GlitchPortClient`, events, scopes,
  sampling, ASP.NET Core integration and `ILogger` forwarding.
- [.NET SDK relays](documents/sdk-dotnet-relays.md) — HTTP, terminal and file
  relays, `GLITCHPORT_DSN`, delivery reports and the offline queue.
- [Supabase Edge Functions SDK](documents/sdk-supabase.md) — installation,
  `withGlitchPort`, scopes, transport behaviour and limits.
- [Unity SDK](documents/sdk-unity.md) — installation, settings, automatic
  capture, contexts, breadcrumbs, structured exceptions and offline queue.

Additional component guides:
[Protocol v1](sdk/protocol/README.md),
[HelloClient](sdk/dotnet/samples/HelloClient/README.md),
[GameServerSample](sdk/dotnet/samples/GameServerSample/README.md) and the
[Lightsail deployment](deploy/lightsail/README.md).
