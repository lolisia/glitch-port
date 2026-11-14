# glitch-port

glitch-port is an error and crash reporting platform specialised for game
engines (Unreal, Unity, Godot, …).

The repository currently contains:

- the **collector** backend: a public **Relay** that accepts events and an
  internal **Processor** that stores them in PostgreSQL and serves the
  management API;
- the **.NET SDK** (`GlitchPort.Core`), which sends every event to one or more
  **relays** — the remote collector (`http`), a live console window
  (`terminal`) and local log files (`file`);
- a minimal **dashboard** that lists the received messages;
- the **HelloClient** sample and end-to-end scripts for both paths.

## Architecture

```text
                        ┌──────────────────────── .NET SDK (GlitchPort.Core) ────────────────────────┐
HelloClient / game ───▶ │ CaptureMessageAsync ─▶ relay fan-out ─┬─▶ http      ──────────────────────┼──▶ Relay :5080
                        │                                       ├─▶ terminal  ─▶ glitchport-terminal │    (child process window)
                        │                                       └─▶ file      ─▶ logs/glitchport/*.ndjson
                        └────────────────────────────────────────────────────────────────────────────┘

Relay :5080
   │  POST /api/{projectId}/messages     X-GlitchPort-Key: dev-public-key       (from the SDK)
   │  POST /internal/envelopes           X-GlitchPort-Internal: dev-internal-secret
   ▼
Processor :5090 ──────► PostgreSQL :55432
   ▲
   │  GET /api/manage/messages           Authorization: Bearer dev-admin-token
Dashboard :5173
```

- The Relay never touches the database; it validates the event and forwards it
  to the Processor through a transport abstraction (direct HTTP today).
- The Processor stores events idempotently by `event_id`, so several Processor
  instances can share one database.
- In the SDK each relay has its own queue and worker, so a slow or failing relay
  never blocks the game thread or the other relays.

## Components and ports

| Component          | Path                                     | Host port | Notes                                                        |
| ------------------ | ---------------------------------------- | --------- | ------------------------------------------------------------ |
| Relay              | `collector/src/GlitchPort.Relay`         | `5080`    | Public ingest API (`/api/{projectId}/messages`).             |
| Processor          | `collector/src/GlitchPort.Processor`     | `5090`    | Internal ingest (`/internal/envelopes`) and management API.  |
| PostgreSQL         | `docker-compose.yml`                     | `55432`   | Container port `5432`, database/user/password `glitchport`.  |
| Dashboard          | `dashboard`                              | `5173`    | Vite dev server for the React UI.                            |
| SDK core           | `sdk/dotnet/src/GlitchPort.Core`         | –         | `GlitchPortClient`, relays (`http`, `terminal`, `file`), formatting. `netstandard2.1`, no package dependencies. |
| Terminal renderer  | `sdk/dotnet/tools/GlitchPort.Terminal`   | –         | `glitchport-terminal` executable started by the `terminal` relay. |
| Sample             | `sdk/dotnet/samples/HelloClient`         | –         | Console app used by both end-to-end checks.                  |

## Prerequisites

- .NET 10 SDK
- Node.js 22 (with npm)
- Docker with Compose v2 (Docker Desktop on Windows/macOS) — only for the
  collector path (`scripts/e2e-hello.sh`)

The scripts are bash scripts; on Windows run them from Git Bash. Command
examples below are given for bash and, where environment variables are
involved, for PowerShell as well.

## Verify and end-to-end checks

```bash
# Builds every project under sdk/ and collector/, runs all tests and builds the dashboard.
bash scripts/verify.sh

# Collector path: boots PostgreSQL, Processor and Relay, sends "hello" through
# the SDK sample and asserts that the management API lists it. Needs Docker.
bash scripts/e2e-hello.sh

# Local relays: sends an event through the terminal and file relays, checks the
# rendered output and the NDJSON file, then force-kills the sample and asserts
# that the terminal child process exits too. No Docker or network needed.
bash scripts/e2e-local-relays.sh
```

All scripts work from any directory and exit non-zero on the first failure.

`scripts/e2e-hello.sh` starts PostgreSQL with `docker compose up -d --wait
postgres`, builds the Release configuration, runs the Processor and Relay with
`ASPNETCORE_ENVIRONMENT=Development`, waits for `http://localhost:5090/healthz`
and `http://localhost:5080/healthz`, and then runs the HelloClient sample.
Server output is written to `.artifact/e2e/processor.log` and
`.artifact/e2e/relay.log` (git-ignored); the paths are printed when the script
fails. The script always stops the background processes and runs `docker
compose down -v`, so it never leaves containers or ports behind.

## Dashboard

```bash
cd dashboard
npm ci        # first time only: installs vite and the other dependencies
npm run dev
```

The dashboard is served at <http://localhost:5173> and lists the stored
messages (received time, endpoint, message) from the management API at
<http://localhost:5090> using the development admin token (`dev-admin-token`).
It needs a running Processor (and PostgreSQL); send a message with the sample to
see a row. Both values can be overridden at build/dev time:

- `VITE_API_BASE` — Processor base URL (default `http://localhost:5090`)
- `VITE_ADMIN_TOKEN` — bearer token for `/api/manage/messages`

## Sending events with the SDK sample

```bash
dotnet run --project sdk/dotnet/samples/HelloClient -- [message] [--level <debug|info|warning|error|fatal>] [--count <n>] [--hold <seconds>]
```

- `message` defaults to `hello`, `--level` to `info`, `--count` to `1`.
- `--hold` keeps the process alive after the flush, for example to read the
  terminal window; the window closes when the sample exits.

The relays come from `GLITCHPORT_DSN` (see below). Without it the sample sends
to the local collector at `http://dev-public-key@localhost:5080/1`, which
matches the Relay's development configuration — start the Processor and Relay
first (for example as `scripts/e2e-hello.sh` does).

The sample prints `event_id: <guid>` per event, `terminal_pid: <pid>` when a
terminal renderer started, and one
`relay <name> (<type>): delivered=<n> failed=<n> dropped=<n>` line per relay. It
exits with `0` only when every relay succeeded; otherwise it prints
`error: ...` and exits with `1`.

### Local relays: terminal window and log files

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

Several relays can be combined, e.g. local files plus the collector:

```bash
GLITCHPORT_DSN="file:?dir=logs/glitchport;http://dev-public-key@localhost:5080/1?min_level=error"
```

## SDK relay configuration

Relays are configured in code or through `GLITCHPORT_DSN`, a `;`-separated list:

```csharp
var options = new GlitchPortOptions();
options.Relays.Add(Relay.Http("http://dev-public-key@localhost:5080/1", o => o.MinLevel = Level.Error));
options.Relays.Add(Relay.Terminal(o => o.Window = TerminalWindow.New));
options.Relays.Add(Relay.File(o => o.Directory = "logs/glitchport"));

using var client = new GlitchPortClient(options);   // or new GlitchPortClient(GlitchPortOptions.FromEnvironment())
await client.CaptureMessageAsync("hello", Level.Warning);
DeliveryReport report = await client.FlushAsync();
```

| Type       | `GLITCHPORT_DSN` entry                           | Destination                                  |
| ---------- | ------------------------------------------------ | -------------------------------------------- |
| `http`     | `http://<key>@<host>:<port>/<projectId>?min_level=error` | The collector Relay                  |
| `terminal` | `terminal:?window=new&format=pretty`             | `glitchport-terminal` child process          |
| `file`     | `file:?dir=logs/glitchport&format=ndjson`        | Rotating NDJSON or text files                |

`CaptureMessageAsync` only enqueues the event and returns its id; it does not
throw on delivery failures. Use `FlushAsync()` to get per-relay results. Every
option, parameter, default and the terminal/file behaviour in detail are
documented in [documents/sdk-dotnet-relays.md](documents/sdk-dotnet-relays.md).

## Collector configuration

Development values live in the environment-specific app settings:

- `collector/src/GlitchPort.Relay/appsettings.Development.json`
- `collector/src/GlitchPort.Processor/appsettings.Development.json`

| Key                               | Read by   | Development value                                                    | Purpose                                                                 |
| --------------------------------- | --------- | -------------------------------------------------------------------- | ----------------------------------------------------------------------- |
| `Relay:Projects`                  | Relay     | `{ "1": [ "dev-public-key" ] }`                                      | Accepted public ingest keys (`X-GlitchPort-Key`) per project id.        |
| `Relay:ProcessorEndpoints`        | Relay     | `[ "http://localhost:5090" ]`                                        | Processor base URLs that receive the forwarded envelopes.               |
| `Relay:InternalSharedSecret`      | Relay     | `dev-internal-secret`                                                | Secret sent to the Processor as `X-GlitchPort-Internal`.                |
| `Processor:AdminToken`            | Processor | `dev-admin-token`                                                    | Bearer token required by the management API.                            |
| `Processor:InternalSharedSecret`  | Processor | `dev-internal-secret`                                                | Expected `X-GlitchPort-Internal` value; must match the Relay's secret.  |
| `ConnectionStrings:Postgres`      | Processor | `Host=localhost;Port=55432;Database=glitchport;Username=glitchport;Password=glitchport` | PostgreSQL connection used for storage and migrations. |

Any key can be overridden with standard .NET configuration sources, for example
environment variables (`Processor__AdminToken`, `ConnectionStrings__Postgres`) or
command-line arguments. The values above are development defaults only — do not
reuse them in production.

## Repository layout

```text
collector/          Relay, Processor and shared transport/contracts (.NET 10)
dashboard/          React + Vite dashboard
documents/          Published SDK/API documentation
scripts/            verify.sh, e2e-hello.sh, e2e-local-relays.sh
sdk/dotnet/src/     GlitchPort.Core (SDK core and relays)
sdk/dotnet/tools/   GlitchPort.Terminal (glitchport-terminal renderer)
sdk/dotnet/samples/ HelloClient sample
sdk/dotnet/tests/   SDK and renderer tests
docker-compose.yml  Local PostgreSQL for development and e2e
```
