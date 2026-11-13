# glitch-port

glitch-port is an error and crash reporting platform specialised for game
engines (Unreal, Unity, Godot, …). The repository currently contains the
vertical "hello" slice: a .NET SDK sample captures a message, the Relay accepts
it, the Processor stores it in PostgreSQL and the dashboard lists it.

## Hello slice

```text
HelloClient (.NET SDK)
        │  POST /api/1/messages        X-GlitchPort-Key: dev-public-key
        ▼
   Relay :5080
        │  POST /internal/envelopes    X-GlitchPort-Internal: dev-internal-secret
        ▼
 Processor :5090 ──────► PostgreSQL :55432
        ▲
        │  GET /api/manage/messages    Authorization: Bearer dev-admin-token
 Dashboard :5173
```

`bash scripts/e2e-hello.sh` exercises exactly this path end to end.

## Components and ports

| Component  | Path                                              | Host port | Notes                                            |
| ---------- | ------------------------------------------------- | --------- | ------------------------------------------------ |
| Relay      | `collector/src/GlitchPort.Relay`                  | `5080`    | Public ingest API (`/api/{projectId}/messages`). |
| Processor  | `collector/src/GlitchPort.Processor`              | `5090`    | Internal ingest (`/internal/envelopes`) and management API. |
| PostgreSQL | `docker-compose.yml`                              | `55432`   | Container port `5432`, database/user/password `glitchport`. |
| Dashboard  | `dashboard`                                       | `5173`    | Vite dev server for the React UI.                |
| SDK core   | `sdk/dotnet/src/GlitchPort.Core`                  | –         | `GlitchPortClient`, DSN parsing, message capture. |
| Sample     | `sdk/dotnet/samples/HelloClient`                  | –         | Console app used by the end-to-end check.        |

## Prerequisites

- .NET 10 SDK
- Node.js 22 (with npm)
- Docker with Compose v2 (Docker Desktop on Windows/macOS)

Commands below are written for bash. On Windows use Git Bash.

## Verify and end-to-end checks

```bash
# Builds every project under sdk/ and collector/, runs the tests and builds the dashboard.
bash scripts/verify.sh

# Boots PostgreSQL, Processor and Relay, sends "hello" through the SDK sample
# and asserts that the message is listed by the management API.
bash scripts/e2e-hello.sh
```

Both scripts run from the repository root (they also work from any other
directory) and exit non-zero on the first failure.

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
npm ci
npm run dev
```

The dashboard is served at <http://localhost:5173> and reads the management API
at <http://localhost:5090> with the development admin token
(`dev-admin-token`). Both can be overridden at build/dev time:

- `VITE_API_BASE` — Processor base URL (default `http://localhost:5090`)
- `VITE_ADMIN_TOKEN` — bearer token for `/api/manage/messages`

## Sending a message with the SDK

With the Processor and Relay running (for example started by hand or by the e2e
script), run the sample:

```bash
dotnet run --project sdk/dotnet/samples/HelloClient -- "hello"
```

The sample reads the DSN from `GLITCHPORT_DSN` and falls back to
`http://dev-public-key@localhost:5080/1`, which matches the development
configuration of the Relay:

```bash
GLITCHPORT_DSN=http://dev-public-key@localhost:5080/1 \
  dotnet run --project sdk/dotnet/samples/HelloClient -- "hello"
```

On success it prints `event_id: <guid>` and exits with `0`; on failure it prints
a single `error: <message>` line and exits with `1`.

## Configuration

Development values live in the environment-specific app settings:

- `collector/src/GlitchPort.Relay/appsettings.Development.json`
- `collector/src/GlitchPort.Processor/appsettings.Development.json`

| Key                               | Read by   | Development value                                                    | Purpose                                                                 |
| --------------------------------- | --------- | -------------------------------------------------------------------- | ----------------------------------------------------------------------- |
| `Relay:Projects`                  | Relay     | `{ "1": [ "dev-public-key" ] }`                                      | Accepted public ingest keys (`X-GlitchPort-Key`) per project id.        |
| `Relay:ProcessorEndpoints`        | Relay     | `[ "http://localhost:5090" ]`                                        | Processor base URLs that receive the forwarded envelopes.               |
| `Relay:InternalSharedSecret`      | Relay     | `dev-internal-secret`                                                | Secret sent to the Processor as `X-GlitchPort-Internal`.                |
| `Processor:AdminToken`            | Processor | `dev-admin-token`                                                     | Bearer token required by the management API.                            |
| `Processor:InternalSharedSecret`  | Processor | `dev-internal-secret`                                                | Expected `X-GlitchPort-Internal` value; must match the Relay's secret.  |
| `ConnectionStrings:Postgres`      | Processor | `Host=localhost;Port=55432;Database=glitchport;Username=glitchport;Password=glitchport` | PostgreSQL connection used for storage and migrations. |

Any key can be overridden with standard .NET configuration sources, for example
environment variables (`Processor__AdminToken`, `ConnectionStrings__Postgres`) or
command-line arguments. The values above are development defaults only — do not
reuse them in production.

## Repository layout

```text
collector/        Relay, Processor and shared transport/contracts (.NET 10)
dashboard/        React + Vite dashboard
documents/        Published API documentation
scripts/          verify.sh and e2e-hello.sh
sdk/dotnet/       .NET SDK core and HelloClient sample
docker-compose.yml  Local PostgreSQL for development and e2e
```
