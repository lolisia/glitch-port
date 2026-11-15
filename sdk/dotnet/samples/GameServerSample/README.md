# GameServerSample

Minimal ASP.NET Core host that shows the `GlitchPort.AspNetCore` integration:

- `GET /ok` returns `200` with `{"status":"ok"}` and is used as a readiness
  probe.
- `GET /boom` throws an `InvalidOperationException` (`boom from
  GameServerSample`) with an inner `ArgumentException` (`inner argument`).
  `UseGlitchPort()` captures the unhandled request exception and rethrows it, so
  the response is the usual `500` (with the developer exception page in
  Development).
- `GET /log-error` writes one `ILogger.LogError` (`log error from
  GameServerSample`). The provider registered by `AddGlitchPort()` forwards it
  as an event without an exception.

Every captured event also carries `contexts.request` (`method`, `url`, `route`
and a few headers) because the middleware pushes a scope per request.

## Usage

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5070 \
  dotnet run --project sdk/dotnet/samples/GameServerSample --no-launch-profile
```

`Properties/launchSettings.json` uses `http://localhost:5070` as well, so a
plain `dotnet run` listens on the same port.

## Configuration

`appsettings.Development.json` points the SDK at the local collector:

```json
"GlitchPort": { "Dsn": "http://dev-public-key@localhost:5080/1" }
```

The `GlitchPort` section is bound by
`builder.Services.AddGlitchPort(builder.Configuration)`. Any valid relay list
works there, for example a file relay:
`file:?dir=.artifact/glitchport&format=ndjson`.

## End-to-end check

`scripts/e2e-dotnet-server.sh` starts PostgreSQL, the Relay, the Processor and
this sample, calls the three endpoints and asserts what the Processor stored
(the exception chain, `contexts.request.method`, an `in_app` frame and the
log-error issue).
