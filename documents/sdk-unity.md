# Unity SDK

`com.glitchport.unity` is the glitch-port SDK for Unity. It wraps the
`GlitchPort.Core` assembly and turns Unity log messages, exceptions and manual
captures into glitch-port events. Once a DSN is configured, the package
initializes itself before the first scene loads, captures errors automatically
and flushes on quit — no game code is required.

Every event reports `platform` `unity`, `sdk.name` `glitchport.unity` and the
package version as `sdk.version`. Protocol v1 requires a platform on every
event, so the SDK always sends `unity` and no setting is needed. Exceptions are
sent as structured `exception` objects with their inner-exception chain and
stack frames, and the SDK adds device contexts, tags and lifecycle breadcrumbs
to each event.

The package targets Unity 2022.3 and later and is verified on Unity 6000.6.8f1.
It is shipped as source plus a precompiled `netstandard2.1` Core DLL
(`Runtime/Plugins/GlitchPort.Core.dll`) and has no other package dependencies.

## Installation

### From the git repository

In Unity, open **Window > Package Manager**, click **+**, choose **Install
package from git URL...** and enter:

```text
https://github.com/lolisia/glitch-port.git?path=sdk/unity/com.glitchport.unity
```

Alternatively add the dependency to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.glitchport.unity": "https://github.com/lolisia/glitch-port.git?path=sdk/unity/com.glitchport.unity"
  }
}
```

The package contains committed `.meta` files with fixed GUIDs, so it works even
though a package installed from a git URL is read-only.

### From a local checkout

Use **Install package from disk...** and select
`sdk/unity/com.glitchport.unity/package.json`, or reference the checkout by
path:

```json
{
  "dependencies": {
    "com.glitchport.unity": "file:C:/DEV/glitch-port/sdk/unity/com.glitchport.unity"
  }
}
```

Relative `file:` paths are resolved from the project's `Packages` folder, e.g.
`file:../../glitch-port/sdk/unity/com.glitchport.unity` when both checkouts are
siblings.

## Minimal setup: DSN only

1. Open **Project Settings > Glitch Port**. The settings page creates the
   project's settings asset on first use. (You can also create it through
   **Assets > Create > Glitch Port > Settings**.)
2. Set **DSN** to the relay URL of your glitch-port project:

   ```text
   http://<publicKey>@<host>:<port>/<projectId>
   ```

   For the local development collector from this repository that is
   `http://dev-public-key@localhost:5080/1`. The public key is the URL
   userinfo; the project id is the last path segment.

The asset lives at
`Assets/GlitchPort/Resources/GlitchPortSettings.asset`; the runtime loads it
with `Resources.Load<GlitchPortSettings>("GlitchPortSettings")`. Everything else
has sensible defaults, so the DSN is the only required value. Press Play: the
SDK initializes before the first scene loads and starts reporting.

### Settings

| Field                        | Default | Meaning                                                                                                                                            |
| ---------------------------- | ------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Enabled`                    | `true`  | Master switch. When `false`, nothing initializes, not even with `GLITCHPORT_DSN`.                                                                  |
| `Dsn`                        | `""`    | Remote relay DSN. An empty value disables HTTP delivery but keeps the local relays usable.                                                         |
| `AutoInitialize`             | `true`  | Initialize automatically before the first scene loads.                                                                                             |
| `HttpMinLevel`               | `Error` | Minimum level sent to the HTTP relay.                                                                                                              |
| `Release`                    | `""`    | Release identifier copied onto every event. Empty uses `Application.productName@Application.version`, for example `Star Raiders@1.2.3`.            |
| `Environment`                | `""`    | Deployment environment copied onto every event. Empty uses `editor` in the editor, `development` in development builds and `production` otherwise. |
| `SendDefaultPii`             | `false` | Enables the hashed device user id described in [Contexts and tags](#contexts-and-tags). No other personal data is collected automatically.         |
| `MaxBreadcrumbs`             | `100`   | Maximum number of breadcrumbs kept for each event; the newest entries win.                                                                         |
| `SampleRate`                 | `1.0`   | Fraction of events that is sent, from `0` to `1`; values outside the range are clamped. `1.0` sends every event, `0` sends none.                   |
| `OfflineQueue`               | `true`  | Keep events the HTTP relay cannot deliver in an on-disk queue and resend them later. See [Offline queue](#offline-queue).                          |
| `OfflineQueueMaxEvents`      | `500`   | Maximum number of queued events. Values below `1` are clamped to `1`; the oldest events are dropped first.                                         |
| `CaptureExceptions`          | `true`  | Capture `LogType.Exception` messages as structured exception events.                                                                               |
| `CaptureErrors`              | `true`  | Capture `LogType.Error` and `LogType.Assert` messages.                                                                                             |
| `CaptureWarnings`            | `false` | Capture `LogType.Warning` messages as `warning` events.                                                                                            |
| `CaptureLogs`                | `false` | Capture `LogType.Log` messages as `debug` events.                                                                                                  |
| `CaptureBreadcrumbsFromLogs` | `true`  | Record warning and log messages as `log` breadcrumbs even when `CaptureWarnings`/`CaptureLogs` are off.                                            |
| `TerminalMode`               | `Auto`  | `Auto`, `On` or `Off` for the local terminal relay. `Auto` follows the rules below.                                                                |
| `TerminalWindow`             | `New`   | `New` opens a separate console window; `Inherit` writes to the current console.                                                                    |
| `FileMode`                   | `Auto`  | `Auto`, `On` or `Off` for the local file relay.                                                                                                    |
| `FileDirectory`              | `""`    | File relay directory. Empty uses `Application.persistentDataPath/glitchport/logs`.                                                                 |
| `FileMinLevel`               | `Debug` | Minimum level written to the file relay (NDJSON, one event per line).                                                                              |
| `DebugLogging`               | `false` | Print SDK diagnostics (`[GlitchPort] ...`) to the Unity console.                                                                                   |

`SendDefaultPii` has exactly one effect in the Unity SDK: when it is `true`, the
SDK adds a `user` object whose `id` is derived from the device (see
[Contexts and tags](#contexts-and-tags)). The SDK never reads a user name,
e-mail address or IP address; a `user` set through `SetUser` is sent as the game
supplied it, and the Core client still removes `user.ip_address` unless
`SendDefaultPii` is enabled.

## What happens automatically

### Initialization

- A `RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)`
  hook calls `GlitchPortUnity.Init()` when `Enabled` and `AutoInitialize` are
  both `true`. Initialization therefore happens before the first scene's
  `Awake`.
- A `SubsystemRegistration` hook disposes a client left over from a previous
  Play session when **Enter Play Mode Options** disables the domain reload.
- `GlitchPortUnity.Init()` builds the relay list from the settings asset,
  `GLITCHPORT_DSN` (see below) and the current platform, creates the client,
  takes one context snapshot on the main thread, applies the context namespaces
  and tags, adds the optional PII user, starts the lifecycle breadcrumb tracker
  and subscribes to the Unity log callback. If no relay can be created,
  initialization is skipped and a diagnostic is printed when `DebugLogging` is
  enabled.
- The `game` context (active scene, frame count, realtime clock) is refreshed
  and every context namespace and `unity.*` tag is re-applied before each event,
  so an event describes the scene it was captured in.
- On platforms without HTTP support (WebGL) initialization is skipped and a
  single warning is printed.

### Log capture

While the SDK is initialized it listens to
`Application.logMessageReceivedThreaded` (safe for logs emitted off the main
thread) and maps:

| Unity log type                                          | Event                                             | Breadcrumb                                                      |
| ------------------------------------------------------- | ------------------------------------------------- | --------------------------------------------------------------- |
| `LogType.Exception`                                     | Structured exception event at `error`             | none                                                            |
| `LogType.Error`, `LogType.Assert` with a stack trace    | Structured exception event at `error`             | none                                                            |
| `LogType.Error`, `LogType.Assert` without a stack trace | Message event at `error`                          | none                                                            |
| `LogType.Warning`                                       | Message event at `warning` when `CaptureWarnings` | `log` breadcrumb at `warning` when `CaptureBreadcrumbsFromLogs` |
| `LogType.Log`                                           | Message event at `debug` when `CaptureLogs`       | `log` breadcrumb at `debug` when `CaptureBreadcrumbsFromLogs`   |

Exception events use `logger` `unity` and carry the parsed `exception` array
described in [Structured exceptions](#structured-exceptions). For
`LogType.Exception` the condition's `Type: value` supplies the innermost
exception (index `0`); in Unity 6's `Rethrow as` format each
`Rethrow as <type>: <value>` line below it introduces the next outer exception,
while the .NET `--->` format is the opposite and starts with the outermost
exception as the condition. Both shapes are normalized to innermost first.
Unity reports no exception type for `LogType.Error` and `LogType.Assert`, so a
log with a stack trace becomes one synthetic `LogError` (error) or `Assert`
(assert) exception whose value is the condition's first line and whose frames
were parsed from the stack trace. `CaptureExceptions` and `CaptureErrors`
decide whether the event is captured at all.

A warning or log message can therefore produce both an event and a breadcrumb,
and the breadcrumb is recorded whenever `CaptureBreadcrumbsFromLogs` is
enabled, even when `CaptureWarnings`/`CaptureLogs` is off. That way a later
error still shows what the game logged before it.

Messages that start with `[GlitchPort]` are SDK diagnostics and are never
captured, which also prevents feedback loops. Nothing is captured while the SDK
is not initialized or while `Enabled` is `false`.

Logs emitted on another thread are captured too. The Unity context values are
only refreshed on the main thread; off the main thread the SDK uses the last
snapshot it took.

### Contexts and tags

Initialization takes one snapshot of the device, GPU, runtime, application and
platform values. The `game` namespace is refreshed from the main thread before
every event. Values the platform cannot report are left empty or `null` rather
than invented.

| Context   | Key                      | Value                                                                                         |
| --------- | ------------------------ | --------------------------------------------------------------------------------------------- |
| `os`      | `name`                   | `SystemInfo.operatingSystem`, for example `Windows 11  (10.0.22000) 64bit`                    |
| `os`      | `family`                 | `SystemInfo.operatingSystemFamily`, for example `Windows`                                     |
| `device`  | `model`                  | `SystemInfo.deviceModel`                                                                      |
| `device`  | `type`                   | `SystemInfo.deviceType`, for example `Desktop` or `Laptop`                                    |
| `device`  | `memory_size`            | `SystemInfo.systemMemorySize` in megabytes; multiplied by 1024 × 1024 to **bytes**            |
| `device`  | `processor_type`         | `SystemInfo.processorType`                                                                    |
| `device`  | `processor_count`        | `SystemInfo.processorCount`                                                                   |
| `device`  | `battery_level`          | `SystemInfo.batteryLevel`, `0` to `1`; the key is omitted when Unity reports an unknown level |
| `gpu`     | `name`                   | `SystemInfo.graphicsDeviceName`                                                               |
| `gpu`     | `vendor`                 | `SystemInfo.graphicsDeviceVendor`                                                             |
| `gpu`     | `version`                | `SystemInfo.graphicsDeviceVersion`                                                            |
| `gpu`     | `api`                    | `SystemInfo.graphicsDeviceType`, for example `Direct3D11`                                     |
| `gpu`     | `memory_size`            | `SystemInfo.graphicsMemorySize` in **megabytes**                                              |
| `runtime` | `name`                   | Always `Unity`                                                                                |
| `runtime` | `version`                | `Application.unityVersion`                                                                    |
| `runtime` | `scripting_backend`      | `Mono` or `IL2CPP`, selected at compile time                                                  |
| `app`     | `product_name`           | `Application.productName`                                                                     |
| `app`     | `version`                | `Application.version`                                                                         |
| `app`     | `identifier`             | `Application.identifier`                                                                      |
| `app`     | `company`                | `Application.companyName`                                                                     |
| `app`     | `is_editor`              | `Application.isEditor`                                                                        |
| `app`     | `is_development_build`   | `Debug.isDebugBuild`                                                                          |
| `game`    | `scene`                  | Active scene name, or `null` when no scene is loaded                                          |
| `game`    | `frame_count`            | `Time.frameCount`                                                                             |
| `game`    | `realtime_since_startup` | `Time.realtimeSinceStartup` in seconds                                                        |

The two automatic tags are:

| Tag              | Value                                                      |
| ---------------- | ---------------------------------------------------------- |
| `unity.platform` | `Application.platform`, for example `WindowsPlayer`        |
| `unity.scene`    | Active scene name; an empty string when no scene is loaded |

With `SendDefaultPii` enabled the SDK also adds a `user` object with one field:

| Field | Value                                                                                                                      |
| ----- | -------------------------------------------------------------------------------------------------------------------------- |
| `id`  | The first 16 lowercase hexadecimal characters of the SHA-256 hash of `SystemInfo.deviceUniqueIdentifier`, hashed as UTF-8. |

The clear-text device identifier is never sent and nothing else is derived from
the device. The hash is stable for a device, so it can count affected players
without identifying them. When the platform does not report a device identifier,
no `user` is added. The option defaults to `false`.

### Breadcrumbs

While initialized, the SDK records these breadcrumbs automatically. The recorded
trail is attached to every later event in chronological order, together with any
breadcrumb the game adds through `AddBreadcrumb`:

| Trigger                           | `category`      | `level`   | Message                                                 | `data`                            |
| --------------------------------- | --------------- | --------- | ------------------------------------------------------- | --------------------------------- |
| `SceneManager.sceneLoaded`        | `scene`         | `info`    | `Scene loaded: <name>`                                  | `scene`                           |
| `SceneManager.sceneUnloaded`      | `scene`         | `info`    | `Scene unloaded: <name>`                                | `scene`                           |
| `SceneManager.activeSceneChanged` | `scene`         | `info`    | `Active scene changed from '<previous>' to '<current>'` | `previous_scene`, `current_scene` |
| `Application.focusChanged`        | `app.lifecycle` | `info`    | `Application gained focus` or `Application lost focus`  | `focused`                         |
| `OnApplicationPause`              | `app.lifecycle` | `info`    | `Application paused` or `Application resumed`           | `paused`                          |
| `Application.lowMemory`           | `device`        | `warning` | `Low memory warning`                                    | `low_memory`                      |
| `LogType.Warning`                 | `log`           | `warning` | The message text                                        | none                              |
| `LogType.Log`                     | `log`           | `debug`   | The message text                                        | none                              |

Every breadcrumb has `type` `default` and a UTC timestamp. Pause and resume
arrive through a hidden `DontDestroyOnLoad` object because `OnApplicationPause`
is only delivered to `MonoBehaviour` components; `Close()` disposes the tracker
and unsubscribes every callback.

The client merges the breadcrumbs on the scope with the event's own breadcrumbs
and keeps only the newest `MaxBreadcrumbs` entries (default `100`) before
building the event, so older entries are dropped first. The protocol accepts at
most 100 breadcrumbs per event and sends them oldest first, so raising
`MaxBreadcrumbs` above `100` does not make a Relay store more.

### Structured exceptions

The `exception` array is ordered **innermost cause first and outermost observed
exception last**. Stack frames inside each exception are ordered the other way
around: **outermost caller first and the crash site last**. Unity prints stack
traces in the opposite direction (throw site first), so the SDK reverses every
frame list and removes the frames of the logging call itself.

- In Unity 6's `Rethrow as` format the condition supplies the innermost
  exception (index `0`) and every `Rethrow as <type>: <value>` line introduces
  the next outer exception. The .NET `--->` format is the opposite: the
  condition is the outermost exception and the `--->` segments are its inner
  causes. Both shapes are normalized into the same inner-to-outer list, so the
  last item is always the outermost observed exception.
- For `LogType.Exception` the exception type is parsed from the condition (type
  `Exception` when the condition has no type prefix); the log-only
  `LogType.Error` and `LogType.Assert` shapes are described below.
- For `LogType.Error` and `LogType.Assert`, Unity reports a log rather than an
  exception. The event contains one synthetic `LogError` or `Assert` exception
  with the condition's first line as its value and the parsed frames.
- Frames added by the logging call are removed: `UnityEngine.Debug:Log*`,
  `UnityEngine.Logger` and `UnityEngine.DebugLogHandler` frames, and for
  `Debug.LogException` the `UnityEngine.Debug:LogException` frame plus the call
  site below it. When a logged exception was never thrown and the whole chain has
  no frames, the log call-site frames are attached to the outermost exception so
  the event still points at the code that logged it.

The `in_app` flag of every frame is decided by these rules, checked from top to
bottom:

| Condition                                                                                                    | `in_app` |
| ------------------------------------------------------------------------------------------------------------ | -------- |
| The file name starts with `Assets/`                                                                          | `true`   |
| The file name contains `Library/PackageCache/`                                                               | `false`  |
| The declaring type starts with `UnityEngine.`, `UnityEditor.`, `Unity.`, `System.`, `Mono.` or `GlitchPort.` | `false`  |
| Any other named module (game assemblies, plugins)                                                            | `true`   |
| No declaring type (and the file name did not start with `Assets/`)                                           | `false`  |

A `LogType.Exception` event looks like this. The example has two exceptions: a
`NullReferenceException` thrown inside `Weapon.Fire` (innermost, index `0`) and
the `InvalidOperationException` that wrapped it (outermost, last).

```json
{
  "event_id": "9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e",
  "timestamp": "2026-11-15T10:22:31.123Z",
  "level": "error",
  "platform": "unity",
  "logger": "unity",
  "release": "Star Raiders@1.2.3",
  "environment": "production",
  "sdk": {
    "name": "glitchport.unity",
    "version": "0.1.0"
  },
  "exception": [
    {
      "type": "System.NullReferenceException",
      "value": "Object reference not set to an instance of an object",
      "stacktrace": {
        "frames": [
          {
            "function": "Update",
            "module": "PlayerController",
            "filename": "Assets/Scripts/PlayerController.cs",
            "lineno": 42,
            "in_app": true
          },
          {
            "function": "Fire",
            "module": "Weapon",
            "filename": "Assets/Scripts/Weapon.cs",
            "lineno": 88,
            "in_app": true
          }
        ]
      }
    },
    {
      "type": "System.InvalidOperationException",
      "value": "could not fire the weapon",
      "stacktrace": {
        "frames": [
          {
            "function": "Run",
            "module": "GameLoop",
            "filename": "Assets/Scripts/GameLoop.cs",
            "lineno": 17,
            "in_app": true
          },
          {
            "function": "Update",
            "module": "PlayerController",
            "filename": "Assets/Scripts/PlayerController.cs",
            "lineno": 44,
            "in_app": true
          }
        ]
      }
    }
  ]
}
```

`in_app` is what the Processor prefers when it computes the default
fingerprint, so the game's own frames decide the issue even when engine frames
wrap them.

#### IL2CPP

Release builds compiled with IL2CPP usually strip managed source information.
Unity then reports only method and type frames, without file names or line
numbers: `filename` and `lineno` are absent, while `function`, `module` and
`in_app` remain, and `in_app` is decided by the module rules above. Grouping
still uses the method and type, but the dashboard cannot show source lines or
line numbers. Mono builds and the editor normally include both. IL2CPP also does
not guarantee that every managed frame survives compilation; missing frames are
simply absent rather than synthesized.

### Quit-time flush and cleanup

`GlitchPortUnity` subscribes to `Application.quitting`, which Unity raises when
the player quits and when Play mode ends. `Close()` stops the breadcrumb
tracker, unsubscribes the log callback, clears the context provider, flushes the
client for at most two seconds and disposes every relay:

- the file relay flushes its buffered NDJSON lines and closes the file;
- the terminal relay closes the terminal process input, and the renderer exits
  with the player even when the game crashes or is killed (Windows job object).

Call `GlitchPortUnity.FlushAsync(...)` yourself before `Application.Quit` when
you need a deterministic result (for example to pick the exit code).

## Relay rules

Each `GlitchPortSettings` value is combined with the current platform:

- **HTTP relay** — added when `Dsn` is not blank and the platform supports HTTP.
  `HttpMinLevel` filters what is sent. With `OfflineQueue` enabled (the default)
  the relay keeps events it cannot deliver right now in
  `Application.persistentDataPath/glitchport/queue` and resends them later; see
  [Offline queue](#offline-queue).
- **File relay** — `Auto` enables it in the editor and in development builds;
  `On` always enables it; `Off` disables it. The default directory is
  `Application.persistentDataPath/glitchport/logs`.
- **Terminal relay** — `Auto` enables it only on Windows in the editor and in
  development builds. `On` requests it everywhere, but it is only created on a
  Windows platform that can start processes and when
  `Tools~/win-x64/glitchport-terminal.exe` (or the StreamingAssets copy in a
  development build) exists; otherwise a diagnostic explains how to build it.
  `Off` disables it.

The resulting matrix for the default `Auto` modes:

| Environment                   | HTTP                         | File                                 | Terminal                                          |
| ----------------------------- | ---------------------------- | ------------------------------------ | ------------------------------------------------- |
| Editor on Windows             | DSN                          | yes                                  | yes, when the executable was built                |
| Editor on macOS/Linux         | DSN                          | yes                                  | no (Windows-only)                                 |
| Windows development build     | DSN                          | yes                                  | yes, when the postprocessor copied the executable |
| Windows release build         | DSN                          | no                                   | no                                                |
| macOS/Linux development build | DSN                          | yes                                  | no                                                |
| macOS/Linux release build     | DSN                          | no                                   | no                                                |
| WebGL                         | no (SDK does not initialize) | no                                   | no                                                |
| Mobile / console              | DSN (unverified)             | development builds only (unverified) | no                                                |

`On` overrides the `Auto` column for the file and terminal relays; the terminal
relay still requires Windows and an executable.

## Offline queue

The HTTP relay built from the asset `Dsn` keeps events it cannot deliver right
now in an on-disk queue and resends them later, so a player that loses
connectivity does not lose the events captured during that time. The queue is
**on by default**.

| Setting                 | Meaning                                                                                                                                  |
| ----------------------- | ---------------------------------------------------------------------------------------------------------------------------------------- |
| `OfflineQueue`          | `true` enables the queue, `false` disables it.                                                                                           |
| `OfflineQueueMaxEvents` | Maximum number of queued events, `500` by default. Values below `1` are clamped to `1`; when the limit is exceeded the **oldest events are dropped first**. |

- Only the HTTP relay built from the asset `Dsn` is affected. The file and
  terminal relays never queue, and when `Dsn` is blank or the platform does not
  support HTTP there is no HTTP relay, so no queue either.
- Queued events are written to
  `Application.persistentDataPath/glitchport/queue`.
- A reachable relay still delivers immediately: the queue receives only events
  whose delivery failed transiently (for example the collector is down or the
  device is offline), and the relay retries them in the background and before
  the next delivery.
- `MaxAgeHours` stays at its Core default of `72` hours and
  `RetryIntervalSeconds` at `30` seconds; the Unity settings do not change them.
- `GLITCHPORT_DSN` ignores `OfflineQueue` and `OfflineQueueMaxEvents`. An HTTP
  relay from the environment DSN follows its own `offline_dir`,
  `offline_max_events`, `offline_max_age_hours` and
  `offline_retry_interval_seconds` parameters instead, and has no queue without
  `offline_dir`. See [.NET SDK relays](sdk-dotnet-relays.md) for the queue file
  format, directory lock and retry behaviour.

## `GLITCHPORT_DSN`: overriding the asset

When the `GLITCHPORT_DSN` environment variable is set to a non-blank value, the
SDK ignores the asset's relay fields (`Dsn`, `HttpMinLevel`, `OfflineQueue`,
`OfflineQueueMaxEvents`, `FileMode`, `FileDirectory`, `FileMinLevel`,
`TerminalMode`, `TerminalWindow`) and parses the variable with the same syntax as
the .NET SDK. Only the relay fields are replaced: `Release`, `Environment`,
`SendDefaultPii`, `MaxBreadcrumbs`, `SampleRate` and the log capture flags still
come from the asset or their defaults. `Enabled` still gates initialization, and
`AutoInitialize` still decides whether the automatic hook runs.

The HTTP relay's offline queue is part of the relay configuration, so the
variable decides it too: `offline_dir` and `offline_max_events` (plus
`offline_max_age_hours` and `offline_retry_interval_seconds`) replace
`OfflineQueue` and `OfflineQueueMaxEvents`, and a DSN without `offline_dir` has
no queue. See [Offline queue](#offline-queue).

```text
http://<publicKey>@<host>:<port>/<projectId>
file:?dir=logs/glitchport
terminal:?window=new
```

Several relays are separated by `;`. bash (Git Bash), for an editor started from
the command line or a built player:

```bash
GLITCHPORT_DSN="file:?dir=logs/glitchport;http://dev-public-key@localhost:5080/1?min_level=error" \
  ./Build/Windows/MyGame.exe
```

PowerShell:

```powershell
$env:GLITCHPORT_DSN = "file:?dir=logs/glitchport"
./Build/Windows/MyGame.exe
Remove-Item Env:GLITCHPORT_DSN
```

The environment variable is read from the editor's or player's process
environment, so it is convenient for CI jobs and headless runs.
`scripts/e2e-unity.sh` uses it to run a real Windows player against a file
relay. To let the same script also check a real collector, set
`GLITCHPORT_E2E_HTTP_DSN` together with `GLITCHPORT_E2E_ADMIN_URL` and
`GLITCHPORT_E2E_ADMIN_TOKEN`: the player then reports through the collector and
the script verifies the stored event through the Management API. Without those
variables the e2e run stays local and self-contained.

## Manual API

`GlitchPortUnity` is safe to call before initialization and after `Close()`.
Capture methods only enqueue work and return `null` instead of throwing, scope
methods are ignored while no client is active, and no member throws into the
game.

```csharp
using System;
using System.Collections.Generic;
using GlitchPort;
using GlitchPort.Unity;
using UnityEngine;

// A message event. The level defaults to Level.Info.
Guid? messageId = GlitchPortUnity.CaptureMessage("boss phase 2 failed", Level.Warning);

// A structured exception event with the full exception chain and stack frames.
// The level defaults to Level.Error and the frames follow the rules above.
try
{
    LoadSaveFile();
}
catch (Exception exception)
{
    GlitchPortUnity.CaptureException(exception);
}

// Scope state is attached to every later event.
GlitchPortUnity.SetTag("game_mode", "coop");
GlitchPortUnity.SetUser(new UserInfo { Id = "player-42" });
GlitchPortUnity.SetContext("build", new Dictionary<string, object?>
{
    ["channel"] = "steam",
    ["commit"] = "a1b2c3d",
});
GlitchPortUnity.AddBreadcrumb(new Breadcrumb
{
    Category = "match",
    Message = "joining the match",
    Level = Level.Info,
});

// Drains every relay and reports per-relay results. The timeout applies to each
// relay; pass null to use the Core default of five seconds.
DeliveryReport? report = await GlitchPortUnity.FlushAsync(TimeSpan.FromSeconds(10));
if (report is null || !report.AllSucceeded)
{
    Debug.LogWarning("glitch-port did not deliver every event.");
}

// Stops the breadcrumb tracker, unsubscribes from Unity callbacks, flushes for
// up to two seconds and disposes the client. Idempotent; a later Init() works
// again.
GlitchPortUnity.Close();
```

`CaptureException` builds the protocol `exception` list from the .NET exception
and its inner exceptions, using the same inner-to-outer order and
outermost-first frames described above.

Every scope call is serialized with the threaded log callback and the lifecycle
tracker, so `SetTag`, `SetUser`, `SetContext`, `AddBreadcrumb` and the capture
methods can be called from any thread. Context values are only refreshed on the
main thread; elsewhere the last snapshot is used.

The SDK re-applies its `os`, `device`, `gpu`, `runtime`, `app` and `game`
namespaces and the `unity.platform` and `unity.scene` tags before every event.
Use `SetContext` and `SetTag` for your own names; values written into those six
namespaces or those two tags are replaced by the SDK's snapshot.

Use `Init(options)` when you want to build the relay list in code instead of the
settings asset — normally together with `AutoInitialize = false`:

```csharp
var options = new GlitchPortOptions();
options.Relays.Add(Relay.Http("http://dev-public-key@localhost:5080/1", relay =>
{
    relay.MinLevel = Level.Warning;
}));
options.Relays.Add(Relay.File(relay => relay.Directory = "logs/glitchport"));

if (!GlitchPortUnity.Init(options))
{
    Debug.LogWarning("glitch-port is not initialized.");
}
```

`Init(options)` ignores `Enabled` and never replaces an already active client.
The caller's options decide `Release`, `Environment`, `SampleRate`,
`SendDefaultPii`, `MaxBreadcrumbs` and the relay list; the settings asset still
decides which Unity log types are captured. Contexts, tags and breadcrumbs are
attached exactly as on the automatic path. Other members:

| Member                             | Description                                                        |
| ---------------------------------- | ------------------------------------------------------------------ |
| `GlitchPortUnity.IsInitialized`    | `true` while a client is active.                                   |
| `GlitchPortUnity.Client`           | The active `GlitchPortClient`, or `null`.                          |
| `GlitchPortUnity.Init()`           | Initializes from the settings asset and `GLITCHPORT_DSN`.          |
| `GlitchPortUnity.Init(options)`    | Initializes from caller-supplied `GlitchPortOptions`.              |
| `GlitchPortUnity.CaptureMessage`   | Enqueues a message event and returns its id.                       |
| `GlitchPortUnity.CaptureException` | Enqueues a structured exception event and returns its id.          |
| `GlitchPortUnity.SetTag`           | Sets one tag; later values replace earlier ones with the same key. |
| `GlitchPortUnity.SetUser`          | Sets the affected user; `null` is ignored.                         |
| `GlitchPortUnity.SetContext`       | Replaces one context namespace.                                    |
| `GlitchPortUnity.AddBreadcrumb`    | Appends a breadcrumb to the active scope.                          |
| `GlitchPortUnity.FlushAsync`       | Flushes every relay and returns a `DeliveryReport`.                |
| `GlitchPortUnity.Close`            | Flushes briefly, disposes the client and allows a later `Init`.    |

## Several assemblies, one terminal

A Unity game often runs code from other assemblies in the same process. Examples
are a shared simulation or battle package referenced through UPM (`file:` or
git), a precompiled DLL, or game modules split into asmdefs. Report from these
assemblies through the client that `GlitchPortUnity` already owns. That way
every event reaches the same relays and the same `glitchport-terminal` window.

Each `GlitchPortClient` starts its own terminal renderer. If a library creates a
second client with a terminal relay, a second console window opens. That client
also has its own scope, rate of flushing and offline queue. Do not create a
second client inside a library that runs in the Unity player.

### What already reaches the terminal

Unhandled exceptions and `UnityEngine.Debug` logs from any assembly need no
extra wiring. The SDK listens to `Application.logMessageReceivedThreaded`, so it
captures them from every thread. Only diagnostics that the library wants to
report itself still need wiring. Examples are a desync, a rejected command or an
invariant that was repaired.

### Recommended: a diagnostics hook in the library

The library has no dependency on glitch-port or UnityEngine. It exposes a small
hook. The game's startup code connects that hook to `GlitchPortUnity`. A server,
a headless simulator or a test runner can connect the same hook to its own
logger, or leave it unset. With no sink set, reporting does nothing. This keeps
deterministic or engine-independent code free of SDK and I/O concerns.

In the library (for example a `Contracts` assembly that every module already
references):

```csharp
using System;

namespace MyGame.Battle
{
    public enum BattleLogLevel { Debug, Info, Warning, Error }

    public readonly struct BattleLogEntry
    {
        public BattleLogEntry(BattleLogLevel level, string category, string message, Exception? exception = null)
        {
            Level = level;
            Category = category;
            Message = message;
            Exception = exception;
        }

        public BattleLogLevel Level { get; }
        public string Category { get; }   // e.g. "sync", "rules", "net"
        public string Message { get; }
        public Exception? Exception { get; }
    }

    public static class BattleDiagnostics
    {
        // Set once by the host (Unity client, server, tests). Null means "drop".
        public static Action<BattleLogEntry>? Sink { get; set; }

        public static void Report(BattleLogLevel level, string category, string message, Exception? exception = null)
        {
            var sink = Sink;
            if (sink is null)
            {
                return;
            }

            try
            {
                sink(new BattleLogEntry(level, category, message, exception));
            }
            catch
            {
                // Diagnostics must never break the simulation.
            }
        }
    }
}
```

Library code calls `BattleDiagnostics.Report(...)` and nothing else:

```csharp
BattleDiagnostics.Report(BattleLogLevel.Warning, "sync", $"checksum mismatch at tick {tick}");
```

In the Unity client, connect the hook once at startup. The bridge can be
installed before the SDK initializes (the SDK does so at `BeforeSceneLoad`).
It reads `GlitchPortUnity.Client` on every entry and drops entries while no
client is active.

```csharp
using System.Collections.Generic;
using GlitchPort;
using GlitchPort.Unity;
using MyGame.Battle;
using UnityEngine;

static class BattleDiagnosticsBridge
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
    static void Install()
    {
        BattleDiagnostics.Sink = Forward;
        Application.quitting += () => BattleDiagnostics.Sink = null;
    }

    static void Forward(BattleLogEntry entry)
    {
        var client = GlitchPortUnity.Client;
        if (client is null)
        {
            return;
        }

        // Low levels become breadcrumbs: they appear with the next event, not as events.
        if (entry.Level <= BattleLogLevel.Info)
        {
            GlitchPortUnity.AddBreadcrumb(new Breadcrumb
            {
                Category = "battle." + entry.Category,
                Message = entry.Message,
                Level = entry.Level == BattleLogLevel.Debug ? Level.Debug : Level.Info,
            });
            return;
        }

        // A pushed scope tags only this event, so the terminal shows where it came from.
        using (client.PushScope())
        {
            client.ConfigureScope(scope =>
            {
                scope.SetTag("source", "battle");
                scope.SetTag("battle.category", entry.Category);
            });

            var level = entry.Level == BattleLogLevel.Error ? Level.Error : Level.Warning;
            if (entry.Exception is not null)
            {
                GlitchPortUnity.CaptureException(entry.Exception, level);
            }
            else
            {
                GlitchPortUnity.CaptureMessage(entry.Message, level);
            }
        }
    }
}
```

The result is one terminal window with the client's and the library's events
side by side. Library events carry `source=battle`, which the terminal prints
with the other tags. In the dashboard, filter on that tag. Repeat the pattern for
every further assembly: one hook per library, one bridge in the game, one
`source` tag value per library.

Notes:

- `PushScope` is async-local. Capture inside the `using` block, on the same
  flow, as above. A sink called from worker threads works the same way.
- Map levels on the bridge side. The library then keeps its own small enum and
  never references `GlitchPort.Level`.
- For a server or tool built from the same library, connect the hook to that
  host's own logging. If the host is a .NET process that reports to glitch-port,
  connect it to its own `GlitchPortClient`. Separate processes always have
  separate terminals.

### Alternative: inject the client

The library can instead reference `GlitchPort.Core` (`netstandard2.1`, no
package dependencies) and receive a `GlitchPortClient` from the host. In Unity
the host passes `GlitchPortUnity.Client`. This is simpler, but every build of
the library then depends on glitch-port. It also has to handle the client being
`null` before initialization and after `Close()`. Prefer the hook unless the
library is glitch-port-specific anyway.

## Preparing the terminal executable

The terminal relay needs the `glitchport-terminal` renderer for win-x64. Build
it together with the bundled Core DLL from the repository root:

```bash
bash scripts/build-unity-plugins.sh
```

The script:

1. builds `GlitchPort.Core` in Release and copies `GlitchPort.Core.dll` and
   `GlitchPort.Core.xml` to `sdk/unity/com.glitchport.unity/Runtime/Plugins/`;
2. publishes `glitchport-terminal.exe` (native AOT, falling back to a
   self-contained single-file build) into
   `sdk/unity/com.glitchport.unity/Tools~/win-x64/`. `Tools~` is not imported by
   Unity and is not committed to git;
3. records a SHA-256 over the Core sources in
   `Runtime/Plugins/GlitchPort.Core.sources.sha256`; run the script with
   `--check` to verify the committed DLL is current (this is what
   `scripts/verify.sh` does).

In the editor, the package locator resolves the executable and the terminal
relay uses it. For a **Windows development build**, the build postprocessor
copies it to:

```text
<Player>_Data/StreamingAssets/GlitchPort/glitchport-terminal.exe
```

The runtime only looks for it there in development builds; release builds never
receive the binary. Without the executable the terminal relay is skipped with a
diagnostic and the HTTP/file relays keep working.

## Constraints

- **WebGL is not supported.** It has no HTTP support for the SDK, so
  initialization is skipped with a single warning. The file and terminal relays
  are not available there either.
- Only the Windows editor and Windows standalone players are verified, with
  Unity 6000.6.8f1 and the Mono scripting backend. **IL2CPP, mobile and console
  builds are unverified.** IL2CPP release builds normally report method and type
  frames without file names or line numbers; see
  [IL2CPP](#il2cpp).
- Exceptions are reported as structured `exception` objects with their
  inner-exception chain and stack frames. The Processor fingerprints them by
  exception type and their crash-side frames, preferring `in_app` frames,
  instead of using a flattened message.
- The SDK ships `GlitchPort.Core` as a precompiled `netstandard2.1` DLL
  (`Runtime/Plugins/GlitchPort.Core.dll`). It is the same Core assembly used by
  the .NET SDK; there is no Unity-specific fork.
- The SDK collects breadcrumbs, the six context namespaces, the two `unity.*`
  tags and, when enabled, the hashed device user id. It does not collect
  screenshots, view-hierarchy dumps, session replay or native crash dumps; only
  managed exceptions and Unity log messages are reported.
- The terminal renderer is Windows-only and is not distributed with the git
  package; build it locally with `scripts/build-unity-plugins.sh`.
