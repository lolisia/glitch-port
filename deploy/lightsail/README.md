# Lightsail deployment alongside RMD

This stack keeps RMD ports 80/443 unchanged. Dashboard and relay share HTTPS
port 8443. Processor and PostgreSQL have no published host ports. The gateway
uses RMD's existing Caddy certificate volume for the same hostname; reload nginx
daily to pick up certificate renewals (`docker compose exec -T dashboard nginx -s reload`).
Install the included service/timer in `/etc/systemd/system`, run
`systemctl daemon-reload`, then `systemctl enable --now glitch-port-certificate-reload.timer`.

Build from the repository root, choosing an immutable tag:

```powershell
docker build -f deploy/lightsail/Dockerfile.collector --build-arg COMPONENT=Relay -t glitch-port-relay:<tag> .
docker build -f deploy/lightsail/Dockerfile.collector --build-arg COMPONENT=Processor -t glitch-port-processor:<tag> .
docker build -f deploy/lightsail/Dockerfile.dashboard -t glitch-port-dashboard:<tag> .
```

Load the images on the host, copy `compose.yml` into `/opt/glitch-port`, and create
a mode-600 `.env` there containing `GLITCHPORT_IMAGE_TAG`, `GLITCHPORT_HOST`,
`POSTGRES_PASSWORD`, `INTERNAL_SHARED_SECRET`, `PROCESSOR_ADMIN_TOKEN`, and
`INGEST_PUBLIC_KEY`. Use independent cryptographically random values, never dev
defaults. Create `secrets/dashboard.htpasswd` with an SHA-512 crypt password hash.
The nginx worker must be able to read that file. Do not commit credentials.

Start with `docker compose -f compose.yml up -d`. Open TCP 8443 in Lightsail.
The dashboard requires HTTP Basic authentication over HTTPS. Its frontend build
has empty `VITE_API_BASE` and `VITE_ADMIN_TOKEN`: same-origin browser credentials
authenticate at the gateway, which adds the management bearer token internally.
The real admin token is never bundled in JavaScript. Only authenticated users
can read messages; ingest requires a project public key.

Projects and public ingest keys now live in PostgreSQL in the Processor. Create
them through the Management API with
`Authorization: Bearer $PROCESSOR_ADMIN_TOKEN`, for example
`POST /api/manage/projects` (the response contains the project DSN) or
`POST /api/manage/projects/{id}/keys` to add a key to an existing project. Call
it at `http://processor:8080/api/manage` from inside the compose network; the
public gateway does not expose the project endpoints. Set
`Processor__PublicRelayUrl` in the processor environment to the public Relay
URL, for example `https://<GLITCHPORT_HOST>:8443`, so generated DSNs of the
form `https://<public-key>@<host>:<port>/<project-id>` point at the public
Relay.

`Relay__Projects__*` (fed by `INGEST_PUBLIC_KEY` in `.env`) is only a static
bootstrap/fallback for keys that are not in the database yet. Remove those
entries once the project and its keys exist in PostgreSQL, otherwise the static
key stays valid forever and revoking it in the database has no effect.
`Processor__Bootstrap__Projects` is only for first-start seeding, for example
project 1 with `INGEST_PUBLIC_KEY` by setting
`Processor__Bootstrap__Projects__0__Id`, `__Name`, `__Slug` and
`__PublicKeys__0`; it creates missing projects and keys and never updates or
deletes them. Manage projects and keys through the Management API after the
first start.

Data persists in `glitch-port_postgres-data`. Back up with `pg_dump` before
upgrades. `docker compose -f compose.yml down` preserves data; do not add `-v`.
Each service has a CPU and memory limit (576 MiB total); co-hosting still consumes
resources and does not preserve RMD's previously measured maximum capacity.
Retention is enabled by default (`Processor:Retention:Days = 30`; set it to `0`
to disable retention): the Processor deletes events and legacy messages older
than that window every `Processor:Retention:IntervalMinutes` (default `60`).
See [the operations guide](../../documents/operations.md) for retention,
multiple Processor instances and backup/restore procedures.
