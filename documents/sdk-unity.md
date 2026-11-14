# Unity SDK

`com.glitchport.unity` is the glitch-port SDK for Unity. It wraps the
`GlitchPort.Core` assembly and turns Unity log messages, exceptions and manual
captures into glitch-port events. Once a DSN is configured, the package
initializes itself before the first scene loads, captures errors automatically
and flushes on quit — no game code is required.

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

| Field              | Default  | Meaning                                                                                         |
| ------------------ | -------- | ----------------------------------------------------------------------------------------------- |
| `Enabled`          | `true`   | Master switch. When `false`, nothing initializes, not even with `GLITCHPORT_DSN`.                |
| `Dsn`              | `""`     | Remote relay DSN. An empty value disables HTTP delivery but keeps the local relays usable.       |
| `AutoInitialize`   | `true`   | Initialize automatically before the first scene loads.                                          |
| `HttpMinLevel`     | `Error`  | Minimum level sent to the HTTP relay.                                                           |
| `CaptureExceptions`| `true`   | Capture `LogType.Exception` messages as `error` events.                                         |
| `CaptureErrors`    | `true`   | Capture `LogType.Error` and `LogType.Assert` messages as `error` events.                        |
| `CaptureWarnings`  | `false`  | Capture `LogType.Warning` messages as `warning` events.                                         |
| `CaptureLogs`      | `false`  | Capture `LogType.Log` messages as `debug` events.                                               |
| `TerminalMode`     | `Auto`   | `Auto`, `On` or `Off` for the local terminal relay. `Auto` follows the rules below.             |
| `TerminalWindow`   | `New`    | `New` opens a separate console window; `Inherit` writes to the current console.                 |
| `FileMode`         | `Auto`   | `Auto`, `On` or `Off` for the local file relay.                                                 |
| `FileDirectory`    | `""`     | File relay directory. Empty uses `Application.persistentDataPath/glitchport/logs`.              |
| `FileMinLevel`     | `Debug`  | Minimum level written to the file relay (NDJSON, one event per line).                           |
| `DebugLogging`     | `false`  | Print SDK diagnostics (`[GlitchPort] ...`) to the Unity console.                                |

## What happens automatically

### Initialization

- A `RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)`
  hook calls `GlitchPortUnity.Init()` when `Enabled` and `AutoInitialize` are
  both `true`. Initialization therefore happens before the first scene's
  `Awake`.
- A `SubsystemRegistration` hook disposes a client left over from a previous
  Play session when **Enter Play Mode Options** disables the domain reload.
- `GlitchPortUnity.Init()` builds the relay list from the settings asset,
  `GLITCHPORT_DSN` (see below) and the current platform, then subscribes to the
  Unity log callbacks. If no relay can be created, initialization is skipped and
  a diagnostic is printed when `DebugLogging` is enabled.
- On platforms without HTTP support (WebGL) initialization is skipped and a
  single warning is printed.

### Log capture

While the SDK is initialized it listens to
`Application.logMessageReceivedThreaded` (safe for logs emitted off the main
thread) and maps:

| Unity log type                 | Event level | Captured when          |
| ------------------------------ | ----------- | ---------------------- |
| `LogType.Exception`            | `error`     | `CaptureExceptions`    |
| `LogType.Error`, `LogType.Assert` | `error`  | `CaptureErrors`        |
| `LogType.Warning`              | `warning`   | `CaptureWarnings`      |
| `LogType.Log`                  | `debug`     | `CaptureLogs`          |

For error and exception messages the Unity stack trace is appended to the event
message. Messages that start with `[GlitchPort]` are SDK diagnostics and are
never captured, which also prevents feedback loops.

### Quit-time flush and cleanup

`GlitchPortUnity` subscribes to `Application.quitting`, which Unity raises when
the player quits and when Play mode ends. `Close()` unsubscribes the callbacks,
flushes the client for at most two seconds and disposes every relay:

- the file relay flushes its buffered NDJSON lines and closes the file;
- the terminal relay closes the terminal process input, and the renderer exits
  with the player even when the game crashes or is killed (Windows job object).

Call `GlitchPortUnity.FlushAsync(...)` yourself before `Application.Quit` when
you need a deterministic result (for example to pick the exit code).

## Relay rules

Each `GlitchPortSettings` value is combined with the current platform:

- **HTTP relay** — added when `Dsn` is not blank and the platform supports HTTP.
  `HttpMinLevel` filters what is sent.
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

| Environment                       | HTTP | File | Terminal |
| --------------------------------- | ---- | ---- | -------- |
| Editor on Windows                 | DSN  | yes  | yes, when the executable was built |
| Editor on macOS/Linux             | DSN  | yes  | no (Windows-only) |
| Windows development build         | DSN  | yes  | yes, when the postprocessor copied the executable |
| Windows release build             | DSN  | no   | no |
| macOS/Linux development build     | DSN  | yes  | no |
| macOS/Linux release build         | DSN  | no   | no |
| WebGL                             | no (SDK does not initialize) | no | no |
| Mobile / console                  | DSN (unverified) | development builds only (unverified) | no |

`On` overrides the `Auto` column for the file and terminal relays; the terminal
relay still requires Windows and an executable.

## `GLITCHPORT_DSN`: overriding the asset

When the `GLITCHPORT_DSN` environment variable is set to a non-blank value, the
SDK ignores the asset's relay fields (`Dsn`, `HttpMinLevel`, `FileMode`,
`FileDirectory`, `FileMinLevel`, `TerminalMode`, `TerminalWindow`) and parses the
variable with the same syntax as the .NET SDK. `Enabled` still gates
initialization, and `AutoInitialize` still decides whether the automatic hook
runs.

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
environment, so it is convenient for CI jobs, headless runs — and for
`scripts/e2e-unity.sh`, which runs a real Windows player with a file relay only.

## Manual API

`GlitchPortUnity` is safe to call before initialization and after `Close()`;
capture methods return `null` instead of throwing, and only enqueue work.

```csharp
using System;
using GlitchPort;
using GlitchPort.Unity;
using UnityEngine;

// A message event. The level defaults to Level.Info.
Guid? messageId = GlitchPortUnity.CaptureMessage("boss phase 2 failed", Level.Warning);

// An exception event. By default it is sent at Level.Error.
try
{
    LoadSaveFile();
}
catch (Exception exception)
{
    GlitchPortUnity.CaptureException(exception);
}

// Drains every relay and reports per-relay results. The timeout applies to each
// relay; pass null to use the Core default of five seconds.
DeliveryReport? report = await GlitchPortUnity.FlushAsync(TimeSpan.FromSeconds(10));
if (report is null || !report.AllSucceeded)
{
    Debug.LogWarning("glitch-port did not deliver every event.");
}

// Unsubscribes from Unity callbacks, flushes for up to two seconds and disposes
// the client. Idempotent; a later Init() works again.
GlitchPortUnity.Close();
```

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

`Init(options)` ignores `Enabled` and never replaces an already active client;
the SDK name and version are always reported as `glitchport.unity`. Other
members:

| Member                              | Description                                                          |
| ----------------------------------- | -------------------------------------------------------------------- |
| `GlitchPortUnity.IsInitialized`     | `true` while a client is active.                                     |
| `GlitchPortUnity.Client`            | The active `GlitchPortClient`, or `null`.                            |
| `GlitchPortUnity.Init()`            | Initializes from the settings asset and `GLITCHPORT_DSN`.            |
| `GlitchPortUnity.Init(options)`     | Initializes from caller-supplied `GlitchPortOptions`.                |
| `GlitchPortUnity.CaptureMessage`    | Enqueues a message event and returns its id.                         |
| `GlitchPortUnity.CaptureException`  | Enqueues an exception as text and returns its id.                    |
| `GlitchPortUnity.FlushAsync`        | Flushes every relay and returns a `DeliveryReport`.                  |
| `GlitchPortUnity.Close`             | Flushes briefly, disposes the client and allows a later `Init`.      |

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
  builds are unverified.**
- **Exceptions are sent as text**, `Type: message` followed by the stack trace,
  not as structured stack frames. Use the message and level for grouping.
- The SDK ships `GlitchPort.Core` as a precompiled `netstandard2.1` DLL
  (`Runtime/Plugins/GlitchPort.Core.dll`). It is the same Core assembly used by
  the .NET SDK; there is no Unity-specific fork.
- No breadcrumbs, device context or screenshots are collected.
- The terminal renderer is Windows-only and is not distributed with the git
  package; build it locally with `scripts/build-unity-plugins.sh`.
