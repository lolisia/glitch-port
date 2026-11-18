# Operations guide

This guide is for operators of a glitch-port collector deployment. It covers
event retention, running more than one Processor, the full local Docker Compose
stack and logical backups. Request and response details for the HTTP APIs live
in the [Management API](management-api.md) and
[Event ingest API](http-api-events.md) documents; this guide links to them
instead of repeating them.

The examples use the local development stack (PostgreSQL `55432`, Relay `5080`,
Processor `5090`/`5091`/`5092`, dashboard `5173`). Substitute your own hostnames
and credentials in production. In container environments, .NET configuration
keys are passed as environment variables with a double underscore, for example
`Processor:Retention:Days` as `Processor__Retention__Days`.

## Retention

The Processor deletes old data in a background job configured by the
`Processor:Retention` section:

| Key | Environment variable | Default | Meaning |
| --- | --- | --- | --- |
| `Processor:Retention:Days` | `Processor__Retention__Days` | `30` | Keep events and legacy messages whose `received_at` falls inside this many days. `0` disables retention entirely. A negative value falls back to `30`. |
| `Processor:Retention:IntervalMinutes` | `Processor__Retention__IntervalMinutes` | `60` | Minutes between two runs. The first run happens one full interval after the host starts, not during startup. A non-positive value falls back to `60`. |
| `Processor:Retention:BatchSize` | `Processor__Retention__BatchSize` | `10000` | Maximum number of events or messages deleted per transaction. A non-positive value falls back to `10000`. |

### What a run does

A run takes its start time in UTC, computes `cutoff = started_at - Days`, and
then:

1. Deletes events whose `received_at` is older than the cutoff, oldest first,
   at most `BatchSize` rows per transaction. Each batch is committed before the
   next one starts, so batches that already committed stay deleted when a run is
   interrupted; the next run continues with the remaining rows.
2. After every committed batch, recomputes `issues.event_count` from the events
   that remain for each issue that lost rows in that batch. The affected issue
   ids are deduplicated and updated in ascending id order so concurrent writers
   take row locks in a stable order.
3. Once no expired event remains, deletes orphan issues: issues with
   `event_count = 0` whose `last_seen` is older than the cutoff. An issue whose
   last event is still inside the retention window is kept.
4. Deletes legacy rows from `messages` with the same `received_at < cutoff`
   rule, in separate transactions of at most `BatchSize` rows. `messages` is
   the older message-list table; it is not attached to issues.
5. Writes one `maintenance_runs` row with `kind = 'retention'`, the start and
   finish time, the window in days and the deletion counters. A run that fails
   before completion writes no row.

A disabled or skipped run deletes nothing and writes no `maintenance_runs` row.

### Single-runner guarantee

Retention is safe to enable on every Processor instance:

- Each run opens a dedicated connection and executes
  `select pg_try_advisory_lock(823652)`. This is a session-level, non-blocking
  PostgreSQL advisory lock. If another session already holds it, the run logs at
  debug level and returns without touching any data.
- The run releases the lock with `select pg_advisory_unlock(823652)` in a
  `finally` block. Closing the connection also releases the session lock, so a
  crashed process does not leave retention blocked.
- `823652` is reserved for retention. Schema migrations use the distinct
  advisory lock `823651`
  (`select pg_advisory_lock(823651)`/`pg_advisory_unlock(823651)`), so a
  Processor applying migrations and a Processor running retention never wait on
  or serialize with each other.

The startup project bootstrap briefly takes `823652` as well. A retention tick
that overlaps a Processor start can therefore be skipped; it retries on the next
interval.

### Logs

The start and completion messages are structured and contain the same field
names:

| Field | Meaning |
| --- | --- |
| `Days` | Retention window used by the run. |
| `BatchSize` | Maximum rows per delete transaction. |
| `DeletedEvents` | Expired events deleted by the run. |
| `DeletedIssues` | Empty, expired issues deleted by the run. |
| `DeletedMessages` | Expired legacy messages deleted by the run. |
| `DurationMs` | Elapsed milliseconds of the run. The start message logs `0` because the run has not finished yet. |

The start message logs zero counters; the completion message logs the final
counters. Example completion line:

```text
Retention run finished (Days 30, BatchSize 10000, DeletedEvents 128, DeletedIssues 4, DeletedMessages 12, DurationMs 842).
```

A failing run is logged with an error and the worker keeps running; the next
attempt happens after another `IntervalMinutes`. The worker logs
`Retention worker started; the first retention run happens in {IntervalMinutes}
minutes.` when it starts.

### Inspecting the last run

Send `GET /api/manage/maintenance/retention` to read the configured window and
the newest completed run. The endpoint uses the same bearer token as every
other management endpoint (`Processor:AdminToken`):

```bash
curl -H "Authorization: Bearer <admin-token>" http://localhost:5090/api/manage/maintenance/retention
```

```json
{
  "days": 30,
  "last_run_at": "2026-11-15T10:00:00+00:00",
  "last_deleted_events": 128,
  "last_deleted_issues": 4
}
```

| Response field | Meaning |
| --- | --- |
| `days` | Current `Processor:Retention:Days` value. `0` means retention is disabled. |
| `last_run_at` | Finish time of the newest completed retention run, or `null` when no run has completed yet. |
| `last_deleted_events` | Events deleted by that newest run; `0` when no run exists. |
| `last_deleted_issues` | Issues deleted by that newest run; `0` when no run exists. |

The `last_*` values describe only the newest run, not cumulative totals. A
skipped or failed run does not change them. The deleted legacy-message counter
is not part of the response; read it from the `maintenance_runs` table or the
completion log. Historical runs can be inspected directly:

```bash
docker compose exec -T postgres psql -U glitchport -d glitchport -c \
  "select finished_at, days, deleted_events, deleted_issues, deleted_messages
     from maintenance_runs order by finished_at desc limit 5;"
```

See [Management API](management-api.md#retention-status) for the full endpoint
reference, authentication rules and error responses.

## Alerts

The Processor evaluates per-project alert rules and delivers their
notifications over HTTP. Rules, the test delivery endpoint and the notification
history are managed through the
[Management API](management-api.md#alert-rules); this section covers the
delivery configuration and the multi-instance guarantees.

### `Processor:Alerts` configuration

| Key | Environment variable | Default | Meaning |
| --- | --- | --- | --- |
| `Processor:Alerts:Enabled` | `Processor__Alerts__Enabled` | `true` | Whether the evaluation and dispatch workers run. Notification queueing is not affected: a disabled Processor keeps building the backlog. |
| `Processor:Alerts:EvaluateIntervalSeconds` | `Processor__Alerts__EvaluateIntervalSeconds` | `60` | Seconds between two `event_frequency` evaluations. The first evaluation happens one full interval after the host starts. A non-positive value falls back to `60`. |
| `Processor:Alerts:DispatchIntervalSeconds` | `Processor__Alerts__DispatchIntervalSeconds` | `5` | Seconds between two dispatch runs. A non-positive value falls back to `5`. |
| `Processor:Alerts:MaxAttempts` | `Processor__Alerts__MaxAttempts` | `5` | Delivery attempts per notification before it is marked `failed`. A non-positive value falls back to `5`. |
| `Processor:Alerts:HttpTimeoutSeconds` | `Processor__Alerts__HttpTimeoutSeconds` | `10` | HTTP timeout of one delivery attempt. A non-positive value falls back to `10`. |
| `Processor:Alerts:DashboardBaseUrl` | `Processor__Alerts__DashboardBaseUrl` | empty | Base URL used to build `{base}#/issues/{issueId}` links in notifications; trailing slashes are removed. An empty value omits the link. |
| `Processor:Alerts:RetentionDays` | `Processor__Alerts__RetentionDays` | `30` | Days sent and failed notifications are kept before the evaluator deletes them. `0` disables the cleanup; a negative value falls back to `30`. |

The section is bound from the `Processor:Alerts` configuration key; in
containers pass it as the environment variables above. The dispatch HTTP client
uses `HttpTimeoutSeconds` as its timeout.

### Why multiple Processors do not duplicate alerts

Alert delivery is safe with any number of Processor instances against one
database:

1. **Queueing is part of the event-store transaction.** `new_issue` and
   `regression` notifications are inserted by the event store with its own
   connection and transaction, so the events, the issue update and the
   notification commit or roll back together. The insert is
   `on conflict (dedupe_key) do nothing` against a unique `dedupe_key`
   (`new_issue:{rule}:{issue}` or
   `regression:{rule}:{issue}:{regressionCount}`), so a retried or duplicated
   event batch inserts the notification once. A rolled-back batch leaves no
   notification behind.
2. **Frequency evaluation is single-runner.** Every evaluation opens a
   dedicated connection and runs
   `select pg_try_advisory_lock(823653)`. Only one Processor instance can hold
   the session lock; the others log at debug level and skip that tick. The
   evaluator releases the lock in a `finally` block with
   `select pg_advisory_unlock(823653)`, and closing the connection releases it
   even after a crash. `823653` is reserved for alert evaluation; migrations
   use `823651` and retention uses `823652`. The frequency dedupe key includes
   the cooldown bucket, so running the same window twice still queues at most
   one notification per issue and cooldown window.
3. **Dispatch claims rows with `for update skip locked`.** Each dispatch run
   claims up to 20 due notifications in one transaction with
   `for update skip locked`, marks them `sending` with a two-minute lease and
   increments `attempts`, then commits before making any HTTP request. A second
   instance skips those rows and can never send the same notification, and no
   database lock is held while a target is slow.

A 2xx response marks the notification `sent`. Any other result, including a
network failure, retries it with exponential backoff
(`min(2^attempts * 30s, 1h)`) while `attempts` is below `MaxAttempts`; a `429`
response honours its `Retry-After` value, and the notification is marked
`failed` once the limit is reached. Failures are logged and stored in
`alert_notifications.last_error` (truncated to 500 characters).

### Webhook URLs are secrets

`target_url` values are secrets. Slack and Discord webhook URLs embed their
credentials in the path, and any webhook URL may carry a token. The Management
API returns the value in rule list and detail responses, so protect the
`Processor:AdminToken` and the dashboard the same way you protect the database.
Do not commit target URLs in configuration files or logs; store them in a
secret manager and rotate a leaked webhook at the provider.

## Running several Processors

Processors are stateless. All durable state lives in PostgreSQL, so you can run
any number of instances against the same database and put them behind one or
more Relays.

### Shared PostgreSQL and advisory locks

- Every Processor uses the same database through the
  `ConnectionStrings:Postgres` configuration key (environment variable
  `ConnectionStrings__Postgres`). The key is required and has no built-in
  default; the Processor fails to start when it is missing.
- On startup each instance applies pending schema migrations. Migrations are
  serialized across instances by the session advisory lock `823651`
  (`pg_advisory_lock`), and each script is committed together with its
  `schema_migrations` row. The first instance migrates; the others see the
  recorded version and do nothing. No separate migration job is required.
- Retention runs on every instance, but only one run at a time succeeds in
  taking `823652`; the other instances skip that tick and retry after their next
  interval. No scheduler, leader election or shared memory is involved.
- Alert `event_frequency` evaluation likewise runs on every instance, but only
  one evaluation at a time succeeds in taking `823653`; the other instances skip
  that tick. Notification queueing and dispatch do not need the lock (see
  [Alerts](#alerts)).

### Sizing the connection pool

`Maximum Pool Size` in `ConnectionStrings:Postgres` is a per-process limit.
To estimate the possible connection ceiling, multiply the configured pool size
by the instance count; migrations, administration and `psql` add a few more
connections on top. Size PostgreSQL's `max_connections` above that total.

For example, four Processors with `Maximum Pool Size=20` can open up to 80
connections, so `max_connections` should be at least 100. The local `full`
profile gives each of its two Processors a pool of 10. The Lightsail compose
file runs one Processor with a pool of 10 and sets `max_connections=30`; adding
Processor instances there requires raising `max_connections` accordingly. The
Relay never connects to PostgreSQL and does not count against the pool.

### Relay forwarding

The Relay forwards accepted events to the endpoints listed in
`Relay:ProcessorEndpoints` (environment variables
`Relay__ProcessorEndpoints__0`, `Relay__ProcessorEndpoints__1`, …). The list is
empty by default, so configure at least one endpoint. The Relay does not use the
database itself.

`HttpEnvelopePublisher` implements the transport:

- Every publish advances a thread-safe rotating counter and starts at the next
  endpoint, then walks the endpoint array from there. With two endpoints,
  consecutive publishes alternate, so traffic is spread roughly evenly.
- If an attempt fails with a connection error (`HttpRequestException`), a
  per-attempt timeout, or an HTTP `5xx`, the publisher tries the next endpoint
  with the identical serialized batch. Other non-success responses (for example
  `401` or `400`) are configuration or protocol errors and fail immediately.
- `Relay:Timeout` (default `10` seconds) bounds each attempt. When every
  endpoint fails, the ingest request returns `503 Service Unavailable` and the
  client can retry.
- If the Relay has no reachable Processor, events are not silently dropped; the
  client sees the failure and can retry according to its SDK policy.

### Idempotent storage

The Processor stores events with `insert ... on conflict (id) do nothing`,
keyed by `event_id`. A retried batch, a duplicate delivery, or a failover after
the first Processor committed but its response was lost is therefore a no-op:
only newly inserted event ids update issue counters. This is what makes the
Relay's retry and failover behaviour safe.

### No sticky sessions

The Relay does not pin a client, SDK instance or event to a particular
Processor. Any instance can handle any ingest request, and the Dashboard or
automation can call the Management API on any instance. No sticky sessions or
shared memory are required; the per-Relay project-config cache is disposable and
is loaded only from the Processors. There is no static key fallback: until the
first configuration load succeeds, ingest requests return `503` with
`Retry-After: 5` and `{"error":"project configuration not loaded"}`, so clients
retry or queue instead of losing events.

Before the first successful project-config load, the Relay retries the Processor
with exponential backoff starting at 1s (1s, 2s, 4s, 8s, ...), capped at
`Relay:ConfigRefreshSeconds` (default 60 seconds), so a Relay started before its
Processor recovers within seconds instead of waiting a full refresh interval.
After the first success only the `Relay:ConfigRefreshSeconds` cadence applies,
and a failed refresh keeps the last snapshot and the processor health source.

## Full local stack

The root `docker-compose.yml` defines a `full` Docker Compose profile with the
complete local stack. PostgreSQL also remains available outside the profile, so
the host-run end-to-end scripts can still call
`docker compose up -d --wait postgres`.

| Service | Host port | Container port | Notes |
| --- | --- | --- | --- |
| `postgres` | `55432` | `5432` | `postgres:17-alpine`; user, password and database are all `glitchport`. |
| `processor-1` | `5091` | `8080` | Processor image; runs the Management API and internal ingest. |
| `processor-2` | `5092` | `8080` | Second Processor; shares the same PostgreSQL database. |
| `relay` | `5080` | `8080` | Public ingest; forwards round-robin to `processor-1:8080` and `processor-2:8080`. |
| `dashboard` | `5173` | `80` | Static nginx build of the React dashboard. |

Start the stack and wait for every healthcheck:

```bash
docker compose --profile full up -d --build --wait
```

The first build can take several minutes. Follow the Relay while it distributes
envelopes between the two Processors:

```bash
docker compose --profile full logs -f relay
```

Per-service logs work the same way, for example
`docker compose --profile full logs -f processor-1 processor-2`.

### Development-only credentials

Everything in this profile is for local development and must never be used in a
deployment:

- Admin token: `dev-admin-token` (`Processor__AdminToken`).
- Internal shared secret: `dev-internal-secret` (`Processor__InternalSharedSecret`
  and `Relay__InternalSharedSecret`).
- Public ingest key: `dev-public-key`, seeded as project `1` with slug
  `default` through `Processor__Bootstrap__Projects__0__*`.
- DSN: `http://dev-public-key@localhost:5080/1`.

The dashboard image is built with the arguments
`VITE_API_BASE=http://localhost:5091` and `VITE_ADMIN_TOKEN=dev-admin-token`.
Because the browser runs on the host, it talks to `processor-1` on published
port `5091`; the token is embedded in the JavaScript bundle, which is another
reason this profile is development-only. `processor-2` serves the same data
because both instances share one database.

Both Processors run with `Processor__Retention__Days=30` and
`Maximum Pool Size=10`.

### Checking the stack

```bash
curl -s http://localhost:5080/healthz
curl -s http://localhost:5091/healthz
curl -s http://localhost:5092/healthz
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5173/
curl -s -H "Authorization: Bearer dev-admin-token" \
  "http://localhost:5091/api/manage/projects/1/issues?status=all&limit=100"
```

The `full` profile sets
`Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics=Information` so the
Processor logs show `POST /internal/envelopes` requests. That is how the
end-to-end script proves that the Relay used both instances.

### Stopping and data loss

```bash
docker compose --profile full down -v
```

`down -v` removes the profile's containers and network and deletes the
anonymous PostgreSQL volume. The local stack declares no named volume, so its
data is disposable and is gone after this command. Take a dump first if you need
the data; see [Backup and restore](#backup-and-restore).

### Integration-only end-to-end script

`scripts/e2e-compose.sh` starts this profile, sends events through the Relay,
verifies the issue from both Processor ports, checks both Processor logs for
internal envelope requests and asserts that the dashboard returns HTML. It then
always tears the stack down with `down -v --remove-orphans`.

```bash
bash scripts/e2e-compose.sh
```

The script is integration-only because it uses the fixed host ports `5080`,
`5091`, `5092`, `5173` and `55432`. Do not run it concurrently with
`scripts/e2e-hello.sh`, `scripts/e2e-events.sh`, `scripts/e2e-projects.sh` or
another copy of itself. Logs and scratch files are written to
`.artifact/e2e-compose/`.

See [Management API](management-api.md) for project, issue, key and retention
endpoint details.

## Backup and restore

The data that matters is in PostgreSQL. Take a logical dump before schema
upgrades, before deleting a project (the project delete endpoint removes the
project's keys, issues, events and labels), and before any operation that
removes the database volume.

For the local stack, start the database first if it is not already running:

```bash
docker compose up -d --wait postgres
```

### Dumping

Custom-format dump (recommended, compressed and restorable with `pg_restore`):

```bash
docker compose exec -T postgres pg_dump -U glitchport -d glitchport -Fc > glitchport.dump
```

Plain SQL dump:

```bash
docker compose exec -T postgres pg_dump -U glitchport -d glitchport > glitchport.sql
```

`-T` disables the pseudo-TTY so the dump stream is not corrupted. Keep dumps
outside the repository or in `.artifact/`; they contain your event data.

### Restoring into an empty database

Create a separate empty database and restore the custom-format dump:

```bash
docker compose exec -T postgres createdb -U glitchport glitchport_restore
docker compose exec -T postgres pg_restore -U glitchport -d glitchport_restore \
  --no-owner < glitchport.dump
```

Or restore the plain SQL dump with `psql`:

```bash
docker compose exec -T postgres createdb -U glitchport glitchport_restore
docker compose exec -T postgres psql -U glitchport -d glitchport_restore \
  < glitchport.sql
```

Check the restored data:

```bash
docker compose exec -T postgres psql -U glitchport -d glitchport_restore \
  -c "select count(*) from events;"
```

To replace the live local database, stop the processes that write to it, drop
and recreate `glitchport`, restore, and start them again:

```bash
docker compose --profile full stop processor-1 processor-2 relay
docker compose exec -T postgres dropdb -U glitchport --if-exists glitchport
docker compose exec -T postgres createdb -U glitchport glitchport
docker compose exec -T postgres pg_restore -U glitchport -d glitchport \
  --no-owner < glitchport.dump
docker compose --profile full start processor-1 processor-2 relay
```

The restored database already contains `schema_migrations`, so the Processor's
startup migration check is a no-op.

### When to dump

- **Before a schema upgrade.** The Processor applies pending SQL migrations
  automatically at startup and the scripts are forward-only. Dump first so you
  can return to the previous database state.
- **Before deleting a project.** `DELETE /api/manage/projects/{projectId}`
  removes the project's keys, issues, events and labels. See the
  [Management API](management-api.md#projects-and-keys) for the request.
- **Before `docker compose --profile full down -v`** if the local data matters,
  because the anonymous volume is deleted.

### Production (Lightsail) volume

`deploy/lightsail/compose.yml` stores PostgreSQL data in the named volume
`glitch-port_postgres-data`. `docker compose -f compose.yml down` preserves that
volume; do not add `-v` on the production host. Dump from the host, for example
from `/opt/glitch-port`:

```bash
docker compose exec -T postgres pg_dump -U glitchport -d glitchport -Fc > glitchport.dump
```

Keep the dump, the mode-600 `.env` file and `secrets/dashboard.htpasswd`
together for a complete restore; the database dump does not contain the
deployment secrets.

In contrast, the local root stack has no named volume: its PostgreSQL data is
disposable and `docker compose --profile full down -v` destroys it. The local
stack is not a durable environment and must not be used as a backup target.

## See also

- [Management API](management-api.md) — projects, keys, issues, events, labels,
  releases, tags, alert rules and the retention status endpoint.
- [Event ingest API](http-api-events.md) — ingest protocol, keys, inbound
  filters and DSNs.
- [Lightsail deployment](../deploy/lightsail/README.md) — single-host
  production deployment.
