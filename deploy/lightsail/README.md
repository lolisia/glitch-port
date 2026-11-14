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
can read messages; ingest requires the separate project 1 public key.

Data persists in `glitch-port_postgres-data`. Back up with `pg_dump` before
upgrades. `docker compose -f compose.yml down` preserves data; do not add `-v`.
Each service has a CPU and memory limit (576 MiB total); co-hosting still consumes
resources and does not preserve RMD's previously measured maximum capacity.
The current collector has no retention job: monitor disk growth.
