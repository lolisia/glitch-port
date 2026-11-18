# .NET SDK

`GlitchPort.Core` is the dependency-free .NET client of glitch-port. It captures
messages, exceptions and fully built **protocol v1 events** and copies each
capture to one or more independent **relays** (HTTP, terminal, file).
`GlitchPort.AspNetCore` adds the ASP.NET Core integration on top of it:
dependency injection, a per-request middleware and `ILogger` forwarding.

| Project                | Target framework  | References                                       | Purpose                                                              |
| ---------------------- | ----------------- | ------------------------------------------------ | -------------------------------------------------------------------- |
| `GlitchPort.Core`      | `netstandard2.1`  | none                                             | Client, scopes, v1 event model, HTTP/terminal/file relays            |
| `GlitchPort.AspNetCore`| `net10.0`         | `Microsoft.AspNetCore.App`, `GlitchPort.Core`    | `AddGlitchPort`, `UseGlitchPort`, `ILogger` provider                 |

Every relay receives the same compact JSON body that is documented in
[Event ingest API (protocol v1)](http-api-events.md): `snake_case` field names,
`event_id`, `timestamp`, `level`, `platform` and `sdk` always present, and at
least one of `message` or `exception`. Events report `platform` `dotnet`; set
the `Platform` option to report a different value.

## Installation

### Requirements

- To build from this repository: .NET 10 SDK (the `GlitchPort.AspNetCore`
  project targets `net10.0`).
- `GlitchPort.Core` targets `netstandard2.1`, so it can be consumed by .NET
  Core 3.0+, .NET 5+ and game engines that ship a .NET Standard 2.1 profile
  (the Unity package bundles the same assembly, see
  [Unity SDK](sdk-unity.md)).
- `GlitchPort.Core` has **no package dependencies**. `GlitchPort.AspNetCore`
  only uses `Microsoft.AspNetCore.App`, which every ASP.NET Core application
  already references.

### Project references

The SDK is consumed as source or as a built assembly; no NuGet package is
published. Add a project reference to `GlitchPort.Core`:

```xml
<ItemGroup>
  <!-- Adjust the relative path to your checkout of this repository. -->
  <ProjectReference Include="<checkout>/sdk/dotnet/src/GlitchPort.Core/GlitchPort.Core.csproj" />
</ItemGroup>
```

An ASP.NET Core application references the integration project instead;
`GlitchPort.AspNetCore` already references `GlitchPort.Core`:

```xml
<ItemGroup>
  <ProjectReference Include="<checkout>/sdk/dotnet/src/GlitchPort.AspNetCore/GlitchPort.AspNetCore.csproj" />
</ItemGroup>
```

The same can be done from the command line:

```bash
dotnet add MyGame.csproj reference <checkout>/sdk/dotnet/src/GlitchPort.Core/GlitchPort.Core.csproj
dotnet add MyApi.csproj reference <checkout>/sdk/dotnet/src/GlitchPort.AspNetCore/GlitchPort.AspNetCore.csproj
```

The relevant namespaces are:

```csharp
using GlitchPort;            // GlitchPortClient, GlitchPortOptions, Scope, events, relays
using GlitchPort.AspNetCore; // AddGlitchPort, UseGlitchPort (ASP.NET Core only)
```

## Initialization

A client is created from `GlitchPortOptions`:

```csharp
using GlitchPort;

var options = new GlitchPortOptions
{
    // One DSN adds an implicit http relay named "http".
    Dsn = Dsn.Parse("http://dev-public-key@localhost:5080/1"),
    Release = "star-raiders@1.2.3",
    Environment = "production",
};

using var client = new GlitchPortClient(options);

await client.CaptureMessageAsync("level 'arena_02' failed to load", Level.Warning);
```

- `Dsn.Parse` accepts `http(s)://{publicKey}@{host}[:{port}]/{projectId}`. The
  public key is the URI userinfo, the project id is the last path segment.
- A client needs at least one relay: either `Dsn` or an enabled entry in
  `Relays`. Otherwise the constructor throws `ArgumentException`.
- `using` is the normal lifetime: `Dispose` unsubscribes the process-wide
  handlers and flushes the relays for at most two seconds. The client is
  thread-safe, so one instance can be shared by a whole application.

### Reading the configuration from the environment

`GlitchPortOptions.FromEnvironment()` builds the options from three variables:

| Variable                 | Used as                                         |
| ------------------------ | ----------------------------------------------- |
| `GLITCHPORT_DSN`         | Semicolon separated relay list (`Relays`)        |
| `GLITCHPORT_RELEASE`     | `Release`                                        |
| `GLITCHPORT_ENVIRONMENT` | `Environment`                                    |

```csharp
var options = GlitchPortOptions.FromEnvironment();
using var client = new GlitchPortClient(options);
```

`FromEnvironment()` never sets `Dsn`; every relay, including the HTTP one, is an
explicit entry in `Relays` (for example
`http://key@host:5080/1;terminal:?format=compact`). Blank variables are
ignored, and an invalid relay throws `FormatException`. The relay syntax is
documented in [.NET SDK relays](sdk-dotnet-relays.md).

### Client options

| Option                        | Default                    | Meaning                                                                                          |
| ----------------------------- | -------------------------- | ------------------------------------------------------------------------------------------------ |
| `Dsn`                         | `null`                     | Adds one implicit `http` relay in front of `Relays`.                                              |
| `Relays`                      | empty                      | Additional relay definitions, created in list order.                                             |
| `SdkName`                     | `glitchport.dotnet`        | `sdk.name` reported in the event JSON.                                                            |
| `SdkVersion`                  | `null`                     | `sdk.version`; `null` uses the informational version of `GlitchPort.Core`.                        |
| `Release`                     | `null`                     | Release copied to every event, for example `mygame@1.2.3`.                                        |
| `Environment`                 | `null`                     | Deployment environment copied to every event, for example `production`.                           |
| `ServerName`                  | the machine name           | `server_name` copied to every event.                                                              |
| `Platform`                    | `dotnet`                   | `platform` reported in the event JSON.                                                            |
| `SampleRate`                  | `1`                        | Fraction of events that is sent, `0` to `1` inclusive.                                            |
| `MaxBreadcrumbs`              | `100`                      | Maximum number of breadcrumbs kept per event (the most recent ones win).                          |
| `BeforeSend`                  | `null`                     | Callback that can inspect, modify or drop an event.                                               |
| `InAppInclude`                | empty                      | Module prefixes that mark a stack frame as application code. When empty, `InAppExclude` decides. |
| `InAppExclude`                | `System.`, `Microsoft.`, `GlitchPort.`, `UnityEngine.`, `UnityEditor.`, `Unity.` | Module prefixes that mark a frame as engine/framework code. |
| `SendDefaultPii`              | `false`                    | Whether personally identifiable information may be sent.                                          |
| `CaptureUnhandledExceptions`  | `false`                    | Whether the client subscribes to the process-wide unhandled exception events.                     |

The client exposes the options it was created with as `Options` and the created
relay instances, in fan-out order, as `Relays`.

## Capturing events

All capture methods return `Task<Guid>` — the event id — and complete as soon
as the event is **queued**; they never wait for the network. The task is
faulted only for a `null` argument, a disposed client
(`ObjectDisposedException`) or an already cancelled token
(`TaskCanceledException`). Delivery failures never surface at the capture site;
use `FlushAsync` to get the per-relay `DeliveryReport` (see
[.NET SDK relays](sdk-dotnet-relays.md)).

### CaptureMessageAsync

```csharp
Task<Guid> CaptureMessageAsync(string message, CancellationToken ct = default);
Task<Guid> CaptureMessageAsync(string message, Level level, CancellationToken ct = default);
```

Creates a **protocol v1 message event**: `event_id`, `timestamp`, `level`
(default `Level.Info`), `platform`, `sdk`, `message`, plus everything merged
from the scopes, the options and the default contexts. The message must be
non-empty: v1 requires `message` or `exception`, so the Relay rejects an event
that has neither with `400`.

A capture with the default scope and options produces the following body (shown
pretty-printed; the SDK emits it compactly):

```json
{
  "event_id": "9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e",
  "timestamp": "2026-11-15T10:22:31.123Z",
  "level": "warning",
  "platform": "dotnet",
  "message": "level 'arena_02' failed to load",
  "release": "star-raiders@1.2.3",
  "environment": "production",
  "server_name": "PLAYER-PC",
  "sdk": { "name": "glitchport.dotnet", "version": "0.1.0" },
  "contexts": {
    "runtime": { "name": "dotnet", "version": "10.0.0", "framework": ".NET 10.0.0" },
    "os": { "name": "Microsoft Windows 10.0.26100", "description": "Microsoft Windows 10.0.26100" }
  }
}
```

The `runtime` and `os` contexts are added automatically; values the application
sets in the same namespaces win. The serializer applies every protocol limit
(8192-character messages, 50 tags, 100 breadcrumbs, 64 KB contexts and
`extra`, …) immediately before the event is sent, so an over-long value is
truncated instead of failing the request.

### CaptureExceptionAsync

```csharp
Task<Guid> CaptureExceptionAsync(Exception ex, Level level = Level.Error, CancellationToken ct = default);
```

Captures an exception and its inner exception chain. The exception list is
ordered **inner cause first, outermost observed exception last**;
`AggregateException` inner exceptions are flattened into the same linear list.
Stack frames are ordered the other way around: outermost caller first, crash
site last. `InAppInclude`/`InAppExclude` decide the `in_app` flag the Processor
prefers when grouping.

```csharp
try
{
    await LoadLevelAsync("arena_02");
}
catch (Exception ex)
{
    await client.CaptureExceptionAsync(ex);
}
```

```json
{
  "event_id": "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
  "timestamp": "2026-11-15T10:22:31.123Z",
  "level": "error",
  "platform": "dotnet",
  "sdk": { "name": "glitchport.dotnet", "version": "0.1.0" },
  "exception": [
    {
      "type": "System.IO.FileNotFoundException",
      "value": "arena_02",
      "module": "System.IO",
      "stacktrace": {
        "frames": [
          {
            "function": "LoadLevelAsync",
            "module": "StarRaiders.Levels.LevelLoader",
            "filename": "LevelLoader.cs",
            "abs_path": "D:\\Dev\\StarRaiders\\Levels\\LevelLoader.cs",
            "lineno": 42,
            "in_app": true
          }
        ]
      }
    }
  ]
}
```

If parsing an exception unexpectedly fails, the event still carries a
`message` in the form `System.InvalidOperationException: boom` so it satisfies
the protocol.

### CaptureEventAsync

```csharp
Task<Guid> CaptureEventAsync(GlitchPortEvent e, CancellationToken ct = default);
```

Captures a fully built `GlitchPortEvent`. An empty `EventId` or `Timestamp` is
filled in before queuing. Use this API for events that carry structured data
the convenience methods do not expose, or when the level comes from the event
itself.

```csharp
using GlitchPort;

var e = new GlitchPortEvent
{
    Level = Level.Fatal,
    Message = "the matchmaking backend connection was lost",
    Logger = "Matchmaking",
    Tags = { ["region"] = "eu-west", ["build"] = "steam" },
    User = new UserInfo { Id = "player-42" },
    Fingerprint = { "matchmaking", "connection-lost" },
};

e.Contexts["game"] = new Dictionary<string, object?>
{
    ["mode"] = "ranked",
    ["map"] = "orbital",
};

e.Extra["retry_count"] = 3;

await client.CaptureEventAsync(e);
```

The event model mirrors the protocol: `EventId`, `Timestamp`, `Level`,
`Platform`, `Logger`, `Message`, `Release`, `Environment`, `ServerName`,
`Exceptions`, `Tags`, `User`, `Contexts`, `Breadcrumbs`, `Fingerprint` and
`Extra`. The level of a `GlitchPortEvent` wins over a scope level.

### Levels

| `Level`   | JSON name | Meaning                                                |
| --------- | --------- | ------------------------------------------------------ |
| `Debug`   | `debug`   | Verbose diagnostic information.                        |
| `Info`    | `info`    | Informational message.                                 |
| `Warning` | `warning` | Something unexpected that does not stop the game.      |
| `Error`   | `error`   | A recoverable or handled error.                        |
| `Fatal`   | `fatal`   | A fatal error, typically a crash.                      |

### Flushing and disposal

`FlushAsync(TimeSpan? timeout = null)` waits for every relay to drain and
returns a `DeliveryReport` with per-relay counters. The default timeout is five
seconds. `Dispose` flushes for at most two seconds before releasing the relays,
and the client also flushes on `ProcessExit`. Queue sizes, per-relay levels and
the report shape are documented in [.NET SDK relays](sdk-dotnet-relays.md).

## Scopes

Every client owns one **global scope** and zero or more **pushed scopes**.
Scopes carry the state that is merged into every captured event: tags, the
affected user, context namespaces, breadcrumbs, a custom fingerprint and an
optional level.

```csharp
client.SetTag("scene", "arena_02");
client.SetUser(new UserInfo { Id = "player-42", Username = "alice" });
client.SetContext("game", new Dictionary<string, object?>
{
    ["mode"] = "ranked",
});
client.AddBreadcrumb(new Breadcrumb
{
    Category = "scene",
    Message = "loaded arena_02",
    Level = Level.Info,
});
```

| Method                              | Effect                                                                                              |
| ----------------------------------- | --------------------------------------------------------------------------------------------------- |
| `ConfigureScope(Action<Scope>)`     | Runs the callback against the innermost pushed scope, or the global scope when none is pushed.       |
| `SetTag(string key, string value)`  | Sets one tag; later values replace earlier ones with the same key.                                   |
| `SetUser(UserInfo? user)`           | Sets or clears the affected user.                                                                    |
| `SetContext(string key, IDictionary<string, object?> value)` | Replaces one context namespace.                                                   |
| `AddBreadcrumb(Breadcrumb breadcrumb)` | Appends a breadcrumb.                                                                            |
| `PushScope()`                       | Pushes a scope for the current asynchronous flow and returns an `IDisposable` that pops it.          |

A `Scope` exposes `Tags`, `User`, `Contexts`, `Breadcrumbs`, `Fingerprint` and
`Level` and has the same `SetTag`, `SetUser`, `SetContext` and `AddBreadcrumb`
helpers.

### Merge rules

Scopes are merged from the global scope through the outermost pushed scope to
the innermost one; afterwards the event itself is applied. For every value the
later source wins:

- **Tags** — merged per key; the event tag wins over every scope.
- **Contexts** — merged per namespace **and** per key inside the namespace, so
  a scope can add one field without replacing the rest of the namespace.
- **User** — the innermost scope user wins over outer users; an event `User`
  wins over all scopes. The event always owns a copy, so later filtering never
  mutates a caller's object.
- **Breadcrumbs** — concatenated global first, then pushed scopes outermost to
  innermost, then the event's own breadcrumbs. Only the most recent
  `MaxBreadcrumbs` entries (default `100`) are sent.
- **Fingerprint** — the innermost non-empty scope fingerprint applies unless the
  event already has one.
- **Level** — a scope `Level` overrides the level passed to
  `CaptureMessageAsync`/`CaptureExceptionAsync`, but never an explicit level on
  a `GlitchPortEvent` passed to `CaptureEventAsync`.

### PushScope and asynchronous flows

`PushScope` stores the current scope in an `AsyncLocal<Scope>` slot. The scope
therefore belongs to the **asynchronous flow**, not to the thread:

- A continuation after `await` runs with the same pushed scope, even when it
  resumes on a different thread.
- Parallel async flows (for example concurrent requests or `Task.Run` calls)
  each see their own scope; values never leak between them.
- Every pushed scope is merged on top of the global scope, so process-wide tags
  and contexts stay visible.
- Disposing the returned handle restores the previous scope. Disposing out of
  order only pops the caller's own scope, so a newer scope in the same flow is
  never discarded.
- `ConfigureScope`, `SetTag`, `SetUser`, `SetContext` and `AddBreadcrumb`
  always target the innermost pushed scope, or the global scope when the flow
  has none.

```csharp
using (client.PushScope())
{
    client.SetTag("match", matchId);
    client.SetUser(new UserInfo { Id = playerId });

    await client.CaptureMessageAsync("joined the match"); // carries both values
}

await client.CaptureMessageAsync("back outside");         // does not carry them
```

Scopes are plain mutable objects and are not synchronized. A pushed scope is
normally private to one flow; concurrent writes to the shared global scope are
the application's responsibility. In a server, prefer one pushed scope per
request — the ASP.NET Core middleware does exactly that.

## BeforeSend

`GlitchPortOptions.BeforeSend` is a
`Func<GlitchPortEvent, GlitchPortEvent?>` that runs for every event with
`SampleRate` acceptance, after the scopes, the option values and the default
contexts have been applied and before the event is serialized:

```csharp
var options = new GlitchPortOptions
{
    Dsn = Dsn.Parse("http://dev-public-key@localhost:5080/1"),
    BeforeSend = e =>
    {
        // Drop events that are not useful.
        if (e.Message == "health check") return null;

        // Modify the event in place...
        e.Tags["host"] = Environment.MachineName;

        // ...or return a different event.
        return e;
    },
};
```

- Returning `null` drops the event; capture still returns its event id and no
  relay sees the event.
- A callback that throws does **not** lose the event: the exception is
  swallowed and the event is sent as it was before the hook was called.
- If the returned event has an empty `event_id` or `timestamp`, the original id
  and the capture time are filled in.
- The callback is invoked synchronously on the capturing thread, so it must be
  fast and thread-safe. It also runs on the finalizer thread for unobserved
  task exceptions and inside the `AppDomain.UnhandledException` handler, so it
  must never block or throw.

## Sampling

`GlitchPortOptions.SampleRate` sends only the given fraction of events:

```csharp
var options = new GlitchPortOptions
{
    Dsn = Dsn.Parse("http://dev-public-key@localhost:5080/1"),
    SampleRate = 0.25, // send one event in four
};
```

- The value must be between `0` and `1` inclusive; any other value (including
  `NaN`) throws `ArgumentOutOfRangeException` from the property setter.
- The default is `1`: every event is sent. `0` drops every event.
- The decision is made per event, after the scope merge and before
  `BeforeSend`, so a sampled-out event never reaches the hook and is not
  counted by any relay. Capture still returns the event id.
- Sampling is independent of a relay's `MinLevel`: sampled-out events are not
  "failed", they simply never enter the relaying pipeline.

## SendDefaultPii

`GlitchPortOptions.SendDefaultPii` defaults to `false`.

- With `false`, the client removes `user.ip_address` from every event right
  before it is serialized — even when the application or `BeforeSend` set it.
  The user object is copied first, so the caller's instance is never mutated.
  Other user fields (`id`, `username`, `email`) are sent exactly as the
  application set them; the SDK never fills them in on its own.
- With `true`, `user.ip_address` is left untouched, `{{auto}}` can be used to
  let the Relay substitute the request address, and the ASP.NET Core middleware
  records the authenticated user name and the client IP (see
  [PII rules](#pii-rules) below).

## Unhandled exceptions

`GlitchPortOptions.CaptureUnhandledExceptions` defaults to `false` in the core
library. When it is `true`, the client registers
`UnhandledExceptionIntegration` for its lifetime and unregisters it on
`Dispose`:

```csharp
var options = new GlitchPortOptions
{
    Dsn = Dsn.Parse("http://dev-public-key@localhost:5080/1"),
    CaptureUnhandledExceptions = true,
};

using var client = new GlitchPortClient(options);
```

The integration subscribes to two process-wide events:

| Event                                          | Captured as                                                                                                                   |
| ---------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| `AppDomain.CurrentDomain.UnhandledException`    | `CaptureExceptionAsync(exception, Level.Fatal)`, followed by a bounded two-second `FlushAsync`. This is the only place where the SDK deliberately blocks. When `ExceptionObject` is not an `Exception`, a `Fatal` message `Unhandled exception: <value>` is captured instead. |
| `TaskScheduler.UnobservedTaskException`         | `CaptureExceptionAsync(e.Exception, Level.Error)` and then `e.SetObserved()`, so a fault nobody awaited is reported without tearing the process down. |

The subscription can also be managed manually; `Register` returns an
`IDisposable` that removes both handlers:

```csharp
using (UnhandledExceptionIntegration.Register(client))
{
    // ... run the application
}
```

Exceptions raised on the crash path must never be hidden by the SDK: capture
failures are swallowed, and the flush is bounded so a dead relay cannot delay
process termination.

## ASP.NET Core integration

Reference `GlitchPort.AspNetCore`, then register the integration in the service
collection and add the middleware to the pipeline.

### Registering the services

```csharp
using GlitchPort;
using GlitchPort.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGlitchPort(builder.Configuration, options =>
{
    // The callback runs after the GlitchPort section was bound; it can override
    // any value and add relays programmatically.
    options.SdkName = "star-raiders.api";
    options.Relays.Add(Relay.File(file => file.Directory = "logs/glitchport"));
});

var app = builder.Build();
```

`AddGlitchPort(IConfiguration? config = null, Action<GlitchPortOptions>? configure = null)`:

- With no `config` argument it uses the `IConfiguration` instance that the host
  already registered in the service collection.
- Binds the **`GlitchPort`** configuration section, then invokes `configure`
  last, so the callback can override every bound value or supply the relay list
  itself.
- Throws `InvalidOperationException` immediately when no relay is configured
  (`GlitchPort:Dsn`, `GLITCHPORT_DSN` or the callback) or when a configured
  value is invalid (bad sample rate, bad boolean, bad log level, malformed
  relay list).
- Registers `GlitchPortOptions`, `GlitchPortClient` and
  `GlitchPortAspNetCoreOptions` as singletons, adds the `ILogger` provider, and
  registers a hosted service that disposes the client on shutdown (a
  best-effort flush of at most two seconds).

### The GlitchPort configuration section

| Key                           | Default      | Meaning                                                                                                    |
| ----------------------------- | ------------ | ---------------------------------------------------------------------------------------------------------- |
| `Dsn`                         | none         | Semicolon separated relay list, the same syntax as `GLITCHPORT_DSN`. When the key is absent or blank, the `GLITCHPORT_DSN` environment variable is used. |
| `Release`                     | none         | Release identifier copied to every event.                                                                  |
| `Environment`                 | the host environment name | Deployment environment. Falls back to `IHostEnvironment.EnvironmentName` when unset.           |
| `SampleRate`                  | `1`          | Number between `0` and `1`; applied by the core client.                                                    |
| `CaptureUnhandledExceptions`  | `true`       | Captures `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`. The core library defaults to `false`; ASP.NET Core hosts default to `true`. |
| `SendDefaultPii`              | `false`      | Records the authenticated user name and the client IP address, and keeps `user.ip_address` on events.       |
| `MinimumBreadcrumbLevel`      | `Information`| Minimum `LogLevel` recorded as a breadcrumb.                                                               |
| `MinimumEventLevel`           | `Error`      | Minimum `LogLevel` that creates a captured event.                                                          |

The two `LogLevel` keys accept `Trace`, `Debug`, `Information`, `Warning`,
`Error`, `Critical` and `None`, case-insensitively; any other value is an
`InvalidOperationException`.

```json
{
  "GlitchPort": {
    "Dsn": "http://dev-public-key@localhost:5080/1;file:?dir=logs/glitchport&min_level=warning",
    "Release": "star-raiders@1.2.3",
    "Environment": "Staging",
    "SampleRate": 0.5,
    "CaptureUnhandledExceptions": true,
    "SendDefaultPii": false,
    "MinimumBreadcrumbLevel": "Information",
    "MinimumEventLevel": "Error"
  }
}
```

`Dsn` may list several relays separated by `;` (for example the collector and a
local file relay). Relay parameters such as `min_level` and the terminal/file
settings are described in [.NET SDK relays](sdk-dotnet-relays.md).

### Middleware order

```csharp
using GlitchPort.AspNetCore;

var app = builder.Build();

app.UseForwardedHeaders();      // before UseGlitchPort: correct client IP behind a proxy
app.UseDeveloperExceptionPage(); // or app.UseExceptionHandler(...): must be before UseGlitchPort
app.UseRouting();                // UseGlitchPort reads the route from the selected endpoint
app.UseAuthentication();
app.UseAuthorization();
app.UseGlitchPort();             // after UseRouting and UseAuthentication

app.MapControllers();
app.Run();
```

`app.UseGlitchPort()` adds one middleware and returns the application builder.
The required order is:

- **After `UseRouting()`** — the middleware reads the route pattern from
  `HttpContext.GetEndpoint()`, and routing selects the endpoint. With minimal
  APIs the implicit routing middleware already runs before user middleware, so
  a plain `app.UseGlitchPort()` after the endpoint-independent middleware is
  enough.
- **After exception handling is registered** — `UseDeveloperExceptionPage()`
  or `UseExceptionHandler()` must come **before** `UseGlitchPort()` in the
  pipeline. The glitch-port middleware always **rethrows the original
  exception instance** after capturing it, so the exception page/handler that
  wraps it still produces the response, and outer middleware observes the same
  exception instance and stack trace.
- **After `UseAuthentication()`** when you enable `SendDefaultPii`, so the
  authenticated identity is available for `user.username`.
- **After the forwarded headers middleware** when the application runs behind a
  reverse proxy, so `client_ip` and `user.ip_address` describe the real client.

### What the middleware records

For every request the middleware pushes a scope (an `AsyncLocal` scope, so
concurrent requests are isolated), fills the request context and the
`http.method`/`http.route` tags, and releases the scope when the request ends.

| Where                        | Value                                                                                      |
| ---------------------------- | ------------------------------------------------------------------------------------------ |
| `contexts.request.method`    | The HTTP method, for example `GET`.                                                         |
| `contexts.request.url`       | `PathBase` + `Path`; the query string is **not** included.                                  |
| `contexts.request.route`     | The endpoint's route pattern, for example `/api/players/{id}`; omitted when no endpoint was selected. |
| `contexts.request.headers`   | Only `User-Agent`, `Content-Type` and `Accept`, and only when present.                       |
| `contexts.request.client_ip` | The connection's remote IP address; only when `SendDefaultPii` is `true`.                    |
| `tags["http.method"]`        | The HTTP method.                                                                            |
| `tags["http.route"]`         | The route pattern; only when an endpoint with a route was selected.                          |

Headers outside that whitelist (`Cookie`, `Authorization`, `Host`, custom
headers, …) are never recorded. An unhandled request exception is captured at
`Level.Error` with the request scope attached, and then rethrown unchanged.

### ILogger integration

`AddGlitchPort` registers an `ILoggerProvider`, so every `ILogger<T>` in the
application forwards to glitch-port according to two thresholds:

- A log entry at or above `MinimumEventLevel` (default `Error`) becomes a
  **captured event**: `logger` is the logger category, `message` is the
  formatted message (or the category when the formatter produced nothing), and
  an attached exception is parsed into the `exception` array with the client
  options. The level is mapped as follows.

  | `Microsoft.Extensions.Logging.LogLevel` | glitch-port `Level` | JSON name |
  | --------------------------------------- | ------------------- | --------- |
  | `Trace`                                 | `Debug`             | `debug`   |
  | `Debug`                                 | `Debug`             | `debug`   |
  | `Information`                           | `Info`              | `info`    |
  | `Warning`                               | `Warning`           | `warning` |
  | `Error`                                 | `Error`             | `error`   |
  | `Critical`                              | `Fatal`             | `fatal`   |

- A log entry at or above `MinimumBreadcrumbLevel` (default `Information`) but
  below `MinimumEventLevel` becomes a **breadcrumb** on the current scope:
  `category` is the logger category, `message` is the formatted message,
  `type` is `log`, and the level is mapped with the same table. Inside a
  request the breadcrumb lands on the middleware's request scope and is sent
  with the request's next event; outside a request it lands on the global
  scope.

Entries below both thresholds are dropped. Categories whose name starts with
`GlitchPort` (case-insensitive) are ignored so the SDK can never report its own
diagnostics recursively. The provider adds its own filter at `Trace` level, so
the host's global filter cannot drop entries before the provider applies the
configured minimums; a filter added explicitly for `GlitchPortLoggerProvider`
still applies.

### Duplicate suppression

An **exception instance is reported at most once per process**, no matter how
many places see it. The request middleware and the logger provider share a
process-wide mark table (`ConditionalWeakTable`, so marking never keeps an
exception alive) with an atomic check-and-mark:

- If the application logs an exception and then rethrows the same instance, the
  logger reports it and the middleware skips it.
- If the middleware captures the exception first (the request handler did not
  log it), a later outer `ILogger` call for that instance is skipped.
- Two log calls with the same exception instance produce one event; calls with
  different instances produce one event each.

### PII rules

`SendDefaultPii` (configuration key `GlitchPort:SendDefaultPii`, default
`false`) controls exactly which person-related values reach glitch-port:

| Value                          | `SendDefaultPii: false` | `SendDefaultPii: true`                                                                 |
| ------------------------------ | ----------------------- | --------------------------------------------------------------------------------------- |
| `user.username`                | never recorded by the middleware | the authenticated identity's name (`HttpContext.User.Identity.Name` when `IsAuthenticated`). |
| `user.ip_address`              | removed by the core client, even if the application set it | the connection's remote address (`HttpContext.Connection.RemoteIpAddress`).       |
| `contexts.request.client_ip`   | never recorded          | the same remote address as `user.ip_address`.                                           |
| request headers                | `User-Agent`, `Content-Type`, `Accept` only | unchanged; still only the three whitelisted headers.                     |

When the middleware records PII, it creates a `user` object with the available
fields. When `SendDefaultPii` is `false`, no `user` object and no `client_ip`
are added by the middleware, and the core's filtering removes any
`user.ip_address` that application code or `BeforeSend` set. `user.id`,
`user.username` and `user.email` that the application sets itself are always
sent; the SDK never invents them.

### Complete example

```csharp
using GlitchPort;
using GlitchPort.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGlitchPort(builder.Configuration);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseDeveloperExceptionPage();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseGlitchPort();

app.MapGet("/ok", () => "ok");
app.MapGet("/boom", (GlitchPortClient client) =>
{
    client.AddBreadcrumb(new Breadcrumb
    {
        Category = "match",
        Message = "joining the match",
    });

    throw new InvalidOperationException("the matchmaker is unavailable");
});

app.Run();
```

A request to `/boom` is answered by the developer exception page with `500`,
and the captured event carries:

```json
{
  "event_id": "5c1a2b3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d",
  "timestamp": "2026-11-15T10:22:31.123Z",
  "level": "error",
  "platform": "dotnet",
  "release": "star-raiders@1.2.3",
  "environment": "Development",
  "sdk": { "name": "glitchport.dotnet", "version": "0.1.0" },
  "exception": [ { "type": "System.InvalidOperationException", "value": "the matchmaker is unavailable", "module": "System", "stacktrace": { "frames": [ … ] } } ],
  "tags": { "http.method": "GET", "http.route": "/boom" },
  "contexts": {
    "request": {
      "method": "GET",
      "url": "/boom",
      "route": "/boom",
      "headers": { "User-Agent": "curl/8.7.1", "Accept": "*/*" }
    }
  },
  "breadcrumbs": [
    {
      "timestamp": "2026-11-15T10:22:31.110Z",
      "category": "match",
      "message": "joining the match",
      "level": "info"
    }
  ]
}
```

## See also

- [.NET SDK relays](sdk-dotnet-relays.md) — relay types, `GLITCHPORT_DSN`,
  delivery reports, terminal and file behaviour.
- [Event ingest API (protocol v1)](http-api-events.md) — the event JSON, field
  limits, authentication and responses.
- [Unity SDK](sdk-unity.md) — the Unity package that wraps `GlitchPort.Core`.
